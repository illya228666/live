using ArtificialLife.Core;
using Godot;

namespace ArtificialLife.Godot.Contracts;

/// <summary>A plugin may create Godot nodes/resources and run arbitrary presentation code on the main thread.</summary>
public interface IGodotPresentation : IDisposable
{
    void Prepare(GodotPresentationContext context, RuntimeFrame initial);
    void Update(RuntimeFrame frame);
    void Draw();
}

/// <summary>Own nodes and event subscriptions through this scope so retiring a generation releases them.</summary>
public sealed class GodotPresentationContext(Node2D canvas, Rect2 viewport, double width, double height, string packageDirectory, ModuleManifest manifest) : IDisposable
{
    private readonly List<Node> nodes = [];
    private readonly List<Action> cleanup = [];
    private bool attached;
    public Node2D Canvas { get; } = canvas;
    public Rect2 Viewport { get; } = viewport;
    public string PackageDirectory { get; } = packageDirectory;
    public ModuleManifest Manifest { get; } = manifest;
    public RuntimeSettings Settings { get; } = new(manifest.Settings);
    public Vector2 ToScreen(Position p) => Viewport.Position + new Vector2((float)(p.X / width), (float)(p.Y / height)) * Viewport.Size;
    public float Pixels(double distance) => (float)(distance / width) * Viewport.Size.X;
    public T Own<T>(T node) where T : Node
    {
        nodes.Add(node);
        if (attached) Canvas.AddChild(node);
        return node;
    }
    public void OnDispose(Action action) => cleanup.Add(action);
    public T OwnResource<T>(T resource) where T : RefCounted
    {
        cleanup.Add(resource.Dispose);
        return resource;
    }
    public void Attach()
    {
        foreach (var node in nodes) if (node.GetParent() is null) Canvas.AddChild(node);
        attached = true;
    }
    public void Dispose()
    {
        string? failure = null;
        foreach (var action in cleanup.AsEnumerable().Reverse())
            try { action(); } catch (Exception ex) { failure ??= ex.Message; }
        cleanup.Clear();
        foreach (var node in nodes)
            try { if (GodotObject.IsInstanceValid(node)) node.Free(); } catch (Exception ex) { failure ??= ex.Message; }
        nodes.Clear();
        if (failure is not null) throw new InvalidOperationException("Presentation cleanup failed: " + failure);
    }
}
