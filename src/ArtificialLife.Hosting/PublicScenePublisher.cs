using System.Globalization;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Core;
using ArtificialLife.Observation;
using ArtificialLife.Packages;

namespace ArtificialLife.Hosting;

/// <summary>Projects copied world state onto the public, read-only scene contract.</summary>
internal sealed class PublicScenePublisher(string directory)
{
    private readonly string assets = Path.Combine(directory, "sprites");
    private string epoch = Guid.NewGuid().ToString("N");
    private long sequence, lastAge = -1, metadata = -1;
    private List<(string Key, VisualDefinition Visual, string? Sprite)> visuals = [];
    private HashSet<string> referenced = [], previous = [];
    private bool failed;

    public void Publish(RuntimeSession live, RuntimeFrame frame)
    {
        try
        {
            Directory.CreateDirectory(assets);
            bool changed = metadata != live.MetadataRevision;
            if (changed) Refresh(live.PresentationPackages);
            if (frame.Age < lastAge)
            {
                epoch = Guid.NewGuid().ToString("N");
                sequence = 0;
            }
            lastAge = frame.Age;
            var entities = frame.Entities.Select(Project).ToList();
            if (frame.HasAgent) entities.Add(Project(frame.Agent));
            var scene = new PublicScene(2, DateTimeOffset.UtcNow, live.Fault is null ? "running" : "faulted",
                Environment.GetEnvironmentVariable("LIVE_WORLD_SESSION"), epoch, ++sequence, frame.Age, frame.Revision,
                frame.Width, frame.Height, [.. entities]);
            // This replaceable projection needs atomic visibility but requires no durable checkpoint.
            PublishFile(Path.Combine(directory, "scene.json"), JsonSerializer.SerializeToUtf8Bytes(scene, ObservationProtocol.Json));
            if (changed)
            {
                metadata = live.MetadataRevision;
                foreach (string path in Directory.EnumerateFiles(assets))
                {
                    string name = Path.GetFileName(path);
                    if (!referenced.Contains(name) && !previous.Contains(name)) File.Delete(path);
                }
            }
            failed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!failed) Console.Error.WriteLine($"Public scene unavailable: {ex.Message}");
            failed = true;
        }
    }

    private void Refresh(IEnumerable<PresentationPackage> packages)
    {
        var next = new List<(string Key, VisualDefinition Visual, string? Sprite)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long availableBytes = 32 * 1024 * 1024;
        foreach (var package in packages)
        foreach (var visual in package.Manifest.Visuals ?? [])
        {
            if (visual is null || string.IsNullOrEmpty(visual.Component) || string.IsNullOrEmpty(visual.Key)) continue;
            // A Godot scene may declare a loose raster as its portable counterpart.
            if (visual.Scene is not null && visual.Sprite is null && visual.Browser is null) continue;
            string? url = null;
            if (visual.Sprite is string path &&
                PortableSprite.Read(package.Directory, path, package.Manifest.PresentationFiles) is { } sprite &&
                (names.Contains(sprite.Name) || sprite.Bytes.Length <= availableBytes))
            {
                if (names.Add(sprite.Name))
                {
                    PublishFile(Path.Combine(assets, sprite.Name), sprite.Bytes);
                    availableBytes -= sprite.Bytes.Length;
                }
                url = "/api/scene/sprites/" + sprite.Name;
            }
            next.Add(($"{package.Manifest.Id}/{package.Manifest.Version}/{visual.Key}", visual, url));
        }
        previous = referenced;
        referenced = names;
        visuals = next.OrderByDescending(v => v.Visual.Layer).ThenBy(v => v.Key, StringComparer.Ordinal).ToList();
    }

    private PublicEntity Project(EntitySnapshot entity)
    {
        var slots = new HashSet<string>(StringComparer.Ordinal);
        var appearance = new List<PublicAppearance>();
        foreach (var visual in visuals)
        {
            if (!entity.Components.ContainsKey(visual.Visual.Component)) continue;
            if (visual.Visual.Browser?.Slot is { Length: > 0 } slot && !slots.Add(slot)) continue;
            appearance.Add(Resolve(entity, visual.Key, visual.Visual, visual.Sprite));
        }
        return new(entity.Id.ToString(CultureInfo.InvariantCulture), entity.Position.X, entity.Position.Y, [.. appearance]);
    }

    private static PublicAppearance Resolve(EntitySnapshot entity, string key, VisualDefinition visual, string? sprite)
    {
        var browser = visual.Browser;
        double size = browser is null ? 3 : PositiveSize(browser.Size);
        double rotation = 0, alpha = 1;
        double scale = browser is null && double.IsFinite(visual.Scale) ? Math.Clamp(visual.Scale, 0, 8) : 1;
        bool visible = true;
        foreach (var binding in visual.Bindings ?? [])
        {
            if (binding is null || string.IsNullOrEmpty(binding.Component) || string.IsNullOrEmpty(binding.Field)) continue;
            if (Read(entity, binding.Component, binding.Field) is not double raw) continue;
            double value = raw * binding.Scale + binding.Offset;
            if (!double.IsFinite(value)) continue;
            switch (binding.Property)
            {
                case "rotation": rotation = value % Math.Tau; break;
                case "scale": scale = Math.Clamp(value, 0, 8); break;
                case "alpha": alpha = Math.Clamp(value, 0, 1); break;
                case "visible": visible = value != 0; break;
                case "size" when browser is not null: size = PositiveSize(value); break;
            }
        }
        string color = visual.Color?.TrimStart('#') ?? "";
        if (color.Length != 6 || !color.All(char.IsAsciiHexDigit)) color = "9DB5AB";
        return new(key, sprite, "#" + color, Math.Clamp(visual.Layer, -4096, 4096), browser?.Label,
            browser?.Primitive == "glow" ? "glow" : "disc", browser is null ? null : size, rotation, scale, alpha, visible);
    }

    private static double PositiveSize(double size) => double.IsFinite(size) ? Math.Clamp(size, .01, 1024) : 3;

    private static double? Read(EntitySnapshot entity, string component, string field)
    {
        if (!entity.Components.TryGetValue(component, out var data) || !data.Values.TryGetValue(field, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number) => number,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
    }

    private static void PublishFile(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
