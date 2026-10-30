using ArtificialLife.Packages;
using ArtificialLife.Core;
using ArtificialLife.Godot.Contracts;
using Godot;

namespace ArtificialLife.Godot;

public sealed class PresentationHost(Node2D canvas, Rect2 viewport) : IRuntimePresentationHost, IDisposable
{
    private List<LoadedPresentation> active = [];
    private readonly List<WeakReference> retired = [];
    public IReadOnlyList<WeakReference> RetiredContexts => retired.AsReadOnly();
    public string? LastError { get; private set; }
    public IPreparedPresentation Prepare(IReadOnlyList<PresentationPackage> packages, RuntimeFrame frame)
    {
        var prepared = new List<LoadedPresentation>();
        try
        {
            foreach (var package in packages.OrderBy(p => p.Manifest.PresentationOrder).ThenBy(p => p.Manifest.Id, StringComparer.Ordinal))
            {
                if (package.Manifest.PresentationAssembly is null && package.Manifest.Visuals.Length == 0) continue;
                try { var loaded = new LoadedPresentation(package, canvas, viewport, frame); prepared.Add(loaded); }
                catch (Exception ex) { LastError = ex.Message; GD.PushWarning("Presentation fallback: " + ex.Message); }
            }
            return new Prepared(this, prepared);
        }
        catch { foreach (var p in prepared) p.Dispose(); throw; }
    }
    public void Draw(RuntimeFrame frame)
    {
        foreach (var presentation in active.ToArray())
        {
            try { presentation.Renderer.Update(frame); presentation.Renderer.Draw(); }
            catch (Exception ex)
            {
                LastError = ex.Message;
                active.Remove(presentation);
                Retire(presentation);
                GD.PushError("Presentation disabled: " + LastError);
            }
        }
    }
    private void Retire(LoadedPresentation presentation)
    {
        retired.Add(presentation.Reference);
        try { presentation.Dispose(); } catch (Exception ex) { LastError = ex.Message; }
        if (retired.Count > 256) retired.RemoveAll(r => !r.IsAlive);
    }
    public void Dispose() { foreach (var presentation in active) Retire(presentation); active.Clear(); }
    private sealed class Prepared(PresentationHost owner, List<LoadedPresentation> items) : IPreparedPresentation
    {
        private bool committed;
        public void Commit()
        {
            foreach (var item in items) item.Context.Attach();
            var old = owner.active;
            owner.active = items;
            committed = true;
            foreach (var item in old) owner.Retire(item);
        }
        public void Dispose() { if (!committed) foreach (var item in items) item.Dispose(); }
    }
    private sealed class LoadedPresentation : IDisposable
    {
        private ModuleLoadContext? context;
        private IGodotPresentation? renderer;
        public IGodotPresentation Renderer => renderer!;
        public GodotPresentationContext Context { get; }
        public WeakReference Reference { get; }
        public LoadedPresentation(PresentationPackage package, Node2D canvas, Rect2 viewport, RuntimeFrame frame)
        {
            Context = new(canvas, viewport, frame.Width, frame.Height, package.Directory, package.Manifest);
            if (package.Manifest.Visuals.Length > 0)
            {
                Reference = new(new object());
                var items = new List<IGodotPresentation>();
                try
                {
                    if (package.Manifest.PresentationAssembly is string dependency)
                    {
                        // Godot C# scene types register in the default context. Replacing
                        // such an assembly can require a Viewer restart, independently of World.
                        var assembly = System.Reflection.Assembly.LoadFrom(PackageCatalog.FileWithin(package.Directory, dependency));
                        if (package.Manifest.PresentationType is string entry)
                            items.Add((IGodotPresentation)Activator.CreateInstance(assembly.GetType(entry, true)!)!);
                    }
                    items.Add(new DeclarativePresentation());
                    renderer = new CombinedPresentation(items);
                    renderer.Prepare(Context, frame);
                }
                catch { Dispose(); throw; }
                return;
            }
            string path = PackageCatalog.FileWithin(package.Directory, package.Manifest.PresentationAssembly!);
            string type = package.Manifest.PresentationType!;
            context = new(path, typeof(IGodotPresentation).Assembly, typeof(Node2D).Assembly);
            Reference = new(context);
            try
            {
                renderer = (IGodotPresentation)Activator.CreateInstance(context.LoadPluginAssembly(path).GetType(type, true)!)!;
                renderer.Prepare(Context, frame);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try { renderer?.Dispose(); }
            finally
            {
                renderer = null;
                try { Context.Dispose(); }
                finally { context?.Unload(); context = null; }
            }
        }
    }
    private sealed class CombinedPresentation(List<IGodotPresentation> items) : IGodotPresentation
    {
        public void Prepare(GodotPresentationContext context, RuntimeFrame initial) { foreach (var item in items) item.Prepare(context, initial); }
        public void Update(RuntimeFrame frame) { foreach (var item in items) item.Update(frame); }
        public void Draw() { foreach (var item in items) item.Draw(); }
        public void Dispose() { foreach (var item in items) try { item.Dispose(); } catch (Exception ex) { GD.PushWarning(ex.Message); } items.Clear(); }
    }
}
