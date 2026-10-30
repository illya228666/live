using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record WorldSave(int FormatVersion, RuntimeOptions Options, RuntimeWorld World, long Revision,
    double LastReward, string[] ObservationKeys, string[] ActionKeys, ModuleManifest[] Modules);

/// <summary>Complete physical state; controller optimizer/replay are deliberately not part of this format.</summary>
public static class WorldCheckpoint
{
    public static string Save(string directory, RuntimeSession session)
    {
        var world = session.CaptureWorld();
        Directory.CreateDirectory(directory);
        string generation = "world-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, generation);
        Directory.CreateDirectory(path);
        foreach (var package in session.PresentationPackages)
        {
            new RuntimePackageImporter(Path.Combine(path, "packages")).Import([package.Directory]);
            var manifest = new PackageCatalog(Path.Combine(path, "packages")).Read(new(package.Manifest.Id, package.Manifest.Version));
            if (!RuntimeSession.SameLogic(package.Manifest, manifest with { Settings = package.Manifest.Settings })) throw new InvalidDataException("Package changed during checkpoint.");
        }
        var options = session.Options with { PackageRoot = "packages", Packages = session.Packages.Select(p => new PackageReference(p.Id, p.Version)).ToArray(), Settings = session.Packages.ToDictionary(p => p.Id, p => p.Settings.Clone()) };
        var save = new WorldSave(1, options, world, session.Revision, session.LastReward, session.ObservationKeys.ToArray(), session.ActionKeys.ToArray(), session.Packages.ToArray());
        string file = Path.Combine(path, "world.json");
        Atomic(file, save);
        Atomic(Path.Combine(path, "checksum.json"), Hash(file));
        Atomic(Path.Combine(directory, "current"), new Pointer(generation, Hash(file)));
        return path;
    }
    public static string Resolve(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "current"))) return Path.GetFullPath(directory);
        var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(Path.Combine(directory, "current")), PackageCatalog.Json) ?? throw new InvalidDataException("Missing world pointer.");
        string path = PackageCatalog.FileWithin(directory, pointer.Generation);
        if (Hash(Path.Combine(path, "world.json")) != pointer.Sha256) throw new InvalidDataException("World checkpoint checksum mismatch.");
        return path;
    }
    public static WorldSave Read(string directory)
    {
        string path = Resolve(directory);
        if (JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(path, "checksum.json"))) != Hash(Path.Combine(path, "world.json"))) throw new InvalidDataException("World checksum mismatch.");
        var save = JsonSerializer.Deserialize<WorldSave>(File.ReadAllText(Path.Combine(path, "world.json")), PackageCatalog.Json) ?? throw new InvalidDataException("Empty world checkpoint.");
        if (save.FormatVersion != 1 || save.Revision < 0 || !double.IsFinite(save.LastReward)) throw new InvalidDataException("Invalid world checkpoint.");
        var options = save.Options with { PackageRoot = Path.Combine(path, "packages") };
        if (!options.Packages.SequenceEqual(save.Modules.Select(m => new PackageReference(m.Id, m.Version)))) throw new InvalidDataException("World package mismatch.");
        var catalog = new PackageCatalog(options.PackageRoot);
        foreach (var expected in save.Modules)
        {
            var actual = catalog.Read(new(expected.Id, expected.Version));
            if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual with { Settings = expected.Settings }) || !options.Settings.TryGetValue(expected.Id, out var settings) || settings.GetRawText() != expected.Settings.GetRawText()) throw new InvalidDataException("World module mismatch.");
        }
        if ((options.Mode == RuntimeMode.WorldOnly) == save.World.HasAgent || JsonSerializer.Serialize(options.World) != JsonSerializer.Serialize(save.World.Options)) throw new InvalidDataException("World options mismatch.");
        return save with { Options = options };
    }
    public static RuntimeSession Open(string directory, IAgentController? controller = null)
    {
        var save = Read(directory);
        if (save.Options.Mode != RuntimeMode.WorldOnly && controller is null) throw new InvalidOperationException("Provide an explicit controller to restore an inhabited world; optimizer/replay are not restored.");
        var runtime = new RuntimeSession(save.Options, controller, observationKeys: save.ObservationKeys, actionKeys: save.ActionKeys, restoredWorld: save.World, restoredModules: save.Modules);
        try { runtime.RestoreState(save.World, save.Revision, save.ObservationKeys, save.ActionKeys, save.LastReward); return runtime; }
        catch { runtime.Dispose(); throw; }
    }
    internal static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    public static void Atomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value, PackageCatalog.Json); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private sealed record Pointer(string Generation, string Sha256);
}
