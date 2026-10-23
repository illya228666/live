using ArtificialLife.Application;
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
                if (package.Manifest.PresentationAssembly is null) continue;
                var loaded = new LoadedPresentation(package, canvas, viewport, frame);
                prepared.Add(loaded);
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
            string path = PackageCatalog.FileWithin(package.Directory, package.Manifest.PresentationAssembly!);
            string type = package.Manifest.PresentationType!;
            context = new(path, typeof(IGodotPresentation).Assembly, typeof(Node2D).Assembly);
            Reference = new(context);
            Context = new(canvas, viewport, frame.Width, frame.Height, package.Directory, package.Manifest);
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
}
