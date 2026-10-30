using ArtificialLife.Core;
using ArtificialLife.Packages;
using ArtificialLife.Godot.Contracts;
using Godot;

namespace ArtificialLife.Godot;

/// <summary>Component-selected scenes/sprites with a deliberately small property binding vocabulary.</summary>
public sealed class DeclarativePresentation : IGodotPresentation
{
    private GodotPresentationContext context = null!;
    private readonly Dictionary<(string, long), Node2D> nodes = [];
    private readonly Dictionary<string, Texture2D> textures = [];
    private readonly System.Diagnostics.Stopwatch animationClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<(string, long), double> appeared = [];
    private readonly Dictionary<Node2D, double> disappearing = [];
    public void Prepare(GodotPresentationContext context, RuntimeFrame initial)
    {
        this.context = context;
        foreach (var file in context.Manifest.PresentationFiles.Keys.Where(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
            if (!ProjectSettings.LoadResourcePack(PackageCatalog.FileWithin(context.PackageDirectory, file), false)) throw new IOException("Cannot load visual resource pack.");
        foreach (var visual in context.Manifest.Visuals.Where(v => v.Sprite is not null))
        {
            if (visual.Sprite!.StartsWith("res://", StringComparison.Ordinal))
                textures[visual.Key] = ResourceLoader.Load<Texture2D>(visual.Sprite) ?? throw new IOException("Missing packed sprite.");
            else
            {
                using var image = new Image();
                if (image.Load(PackageCatalog.FileWithin(context.PackageDirectory, visual.Sprite)) != Error.Ok) throw new IOException("Missing visual sprite.");
                textures[visual.Key] = context.OwnResource(ImageTexture.CreateFromImage(image));
            }
        }
        Update(initial);
    }
    public void Update(RuntimeFrame frame)
    {
        double now = animationClock.Elapsed.TotalSeconds;
        foreach (var item in disappearing.ToArray())
        {
            float alpha = (float)(1 - (now - item.Value) / .18);
            if (alpha <= 0) { context.Release(item.Key); disappearing.Remove(item.Key); }
            else item.Key.SelfModulate = new Color(1, 1, 1, alpha);
        }
        var alive = new HashSet<(string, long)>();
        foreach (var visual in context.Manifest.Visuals)
        foreach (var entity in frame.Entities.Append(frame.Agent).Where(e => e is not null && e.Components.ContainsKey(visual.Component)))
        {
            var key = (visual.Key, entity.Id); alive.Add(key);
            if (!nodes.TryGetValue(key, out var node))
            {
                if (visual.Scene is not null) node = ResourceLoader.Load<PackedScene>(visual.Scene)?.Instantiate<Node2D>() ?? throw new IOException("Missing visual scene.");
                else if (visual.Sprite is not null)
                {
                    node = new Sprite2D { Texture = textures[visual.Key], Hframes = Math.Max(1, visual.Frames) };
                }
                else node = new Polygon2D { Polygon = [new(-5,-5), new(5,-5), new(5,5), new(-5,5)], Color = new Color(visual.Color) };
                nodes[key] = context.Own(node); node.ZIndex = Math.Clamp(visual.Layer, -4096, 4096);
                appeared[key] = now;
                if (visual.Animation is string animation)
                {
                    if (node is AnimatedSprite2D animated) animated.Play(animation);
                    else node.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")?.Play(animation);
                }
            }
            node.Position = context.ToScreen(entity.Position);
            node.SelfModulate = new Color(1, 1, 1, (float)Math.Clamp((now - appeared[key]) / .18, 0, 1));
            node.Scale = Vector2.One * (float)(visual.Scale * (visual.Pulse ? 1 + .08 * Math.Sin(frame.Time * 3) : 1));
            if (node is Sprite2D sequence && visual.Frames > 1)
                sequence.Frame = (int)(Math.Abs(frame.Time * visual.FramesPerSecond) % visual.Frames);
            foreach (var binding in visual.Bindings)
            {
                if (!entity.Components.TryGetValue(binding.Component, out var component) || !component.Values.TryGetValue(binding.Field, out var field)) continue;
                double value;
                if (field.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) value = field.GetBoolean() ? 1 : 0;
                else if (field.ValueKind != System.Text.Json.JsonValueKind.Number || !field.TryGetDouble(out value)) continue;
                float number = (float)(value * binding.Scale + binding.Offset);
                switch (binding.Property)
                {
                    case "rotation": node.Rotation = number; break;
                    case "scale": node.Scale = Vector2.One * number; break;
                    case "alpha": node.Modulate = new Color(1, 1, 1, Math.Clamp(number, 0, 1)); break;
                    case "visible": node.Visible = number != 0; break;
                    case "frame" when node is Sprite2D sprite: sprite.Frame = Math.Clamp((int)number, 0, sprite.Hframes * sprite.Vframes - 1); break;
                }
            }
        }
        foreach (var key in nodes.Keys.Where(k => !alive.Contains(k)).ToArray()) { disappearing[nodes[key]] = now; nodes.Remove(key); appeared.Remove(key); }
    }
    public void Draw() { }
    public void Dispose() { nodes.Clear(); textures.Clear(); appeared.Clear(); disappearing.Clear(); }
}
