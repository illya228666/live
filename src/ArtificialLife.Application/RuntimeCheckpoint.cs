using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record PolicyManifest(int FormatVersion, string Architecture, RuntimeOptions Options,
    string[] ObservationKeys, string[] ActionKeys, ModuleManifest[] Modules, long TrainingSteps, string WeightSha256)
{
    public JsonElement ControllerState { get; init; } = JsonSerializer.SerializeToElement(new { });
}

/// <summary>Immutable generations plus one atomically replaced pointer; inference export, not life persistence.</summary>
public static class RuntimeCheckpoint
{
    public static void Save(string directory, RuntimeSession session)
    {
        var resolved = ControllerRegistry.Resolve(session.Options);
        var factory = ControllerRegistry.Factory(resolved.Controller!.Algorithm);
        if (!factory.Supports(session.Controller)) throw new InvalidOperationException("Configured controller does not match the exported controller.");
        Directory.CreateDirectory(directory);
        string generation = "policy-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, generation);
        Directory.CreateDirectory(path);
        string weights = Path.Combine(path, "weights.bin");
        session.Controller.Export(weights);
        string packageRoot = Path.GetFullPath(Path.Combine(path, "packages"));
        foreach (var package in session.PresentationPackages)
        {
            string target = Path.Combine(packageRoot, package.Manifest.Id, package.Manifest.Version);
            foreach (string source in Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(target, Path.GetRelativePath(package.Directory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
            var archived = new PackageCatalog(packageRoot).Read(new(package.Manifest.Id, package.Manifest.Version));
            if (!RuntimeSession.SameLogic(package.Manifest, archived with { Settings = package.Manifest.Settings }))
                throw new InvalidDataException($"Package changed while exporting: {package.Manifest.Id}.");
        }
        var options = resolved with
        {
            PackageRoot = "packages",
            Packages = session.Packages.Select(p => new PackageReference(p.Id, p.Version)).ToArray(),
            Settings = session.Packages.ToDictionary(p => p.Id, p => p.Settings.Clone())
        };
        var manifest = new PolicyManifest(3, factory.Architecture, options, session.ObservationKeys.ToArray(), session.ActionKeys.ToArray(),
            session.Packages.ToArray(), session.Controller.Status.Steps, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(weights))))
            { ControllerState = session.Controller.InferenceState.Clone() };
        string manifestPath = Path.Combine(path, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, PackageCatalog.Json));
        string manifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));
        File.WriteAllText(Path.Combine(directory, "current.tmp"), JsonSerializer.Serialize(new PolicyPointer(generation, manifestHash), PackageCatalog.Json));
        File.Move(Path.Combine(directory, "current.tmp"), Path.Combine(directory, "current"), true);
    }
    public static string Resolve(string directory)
    {
        string pointer = Path.Combine(directory, "current");
        if (!File.Exists(pointer)) return directory;
        string text = File.ReadAllText(pointer).Trim();
        if (!text.StartsWith('{')) return PackageCatalog.FileWithin(directory, text); // earlier v2 exports
        var metadata = JsonSerializer.Deserialize<PolicyPointer>(text, PackageCatalog.Json) ?? throw new InvalidDataException("Invalid policy pointer.");
        string path = PackageCatalog.FileWithin(directory, metadata.Generation);
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(path, "manifest.json"))));
        if (actual != metadata.ManifestSha256) throw new InvalidDataException("Policy metadata checksum mismatch.");
        return path;
    }
    public static PolicyManifest Read(string directory)
    {
        string path = Resolve(directory);
        var manifest = JsonSerializer.Deserialize<PolicyManifest>(File.ReadAllText(Path.Combine(path, "manifest.json")), PackageCatalog.Json)
            ?? throw new InvalidDataException("Missing policy metadata.");
        if (manifest.FormatVersion is not (2 or 3)) throw new InvalidDataException("Unsupported policy format.");
        var resolved = ControllerRegistry.Resolve(manifest.FormatVersion == 2 ? manifest.Options with { Controller = null } : manifest.Options);
        if (manifest.FormatVersion == 2 && manifest.Architecture != DqnController.Architecture ||
            manifest.FormatVersion == 3 && manifest.Architecture != ControllerRegistry.Factory(resolved.Controller!.Algorithm).Architecture)
            throw new InvalidDataException("Unsupported policy architecture.");
        manifest = manifest with { Options = resolved };
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(path, "weights.bin"))));
        if (actual != manifest.WeightSha256) throw new InvalidDataException("Policy checksum mismatch.");
        if (!Path.IsPathRooted(manifest.Options.PackageRoot)) manifest = manifest with
        {
            Options = manifest.Options with { PackageRoot = Path.GetFullPath(Path.Combine(path, manifest.Options.PackageRoot)) }
        };
        return manifest;
    }
    public static RuntimeSession Open(string directory, RuntimeMode mode = RuntimeMode.Inference)
    {
        string path = Resolve(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        if (document.RootElement.GetProperty("FormatVersion").GetInt32() == 1)
        {
            var old = Checkpoint.Read(path);
            var options = RuntimeDefaults.FromLegacy(old.Options) with { Mode = mode };
            var session = RuntimeSessionFactory.Create(options, new DqnController(options.Network, options.Learning), observationKeys: old.ObservationKeys, actionKeys: old.ActionKeys);
            try { if (!session.ObservationKeys.SequenceEqual(old.ObservationKeys) || !session.ActionKeys.SequenceEqual(old.ActionKeys)) throw new InvalidDataException("Legacy policy registry mismatch."); session.Controller.Import(Path.Combine(path, "weights.bin")); return session; }
            catch { session.Dispose(); throw; }
        }
        var manifest = Read(directory);
        var catalog = new PackageCatalog(manifest.Options.PackageRoot);
        foreach (var expected in manifest.Modules)
        {
            var installed = catalog.Read(new(expected.Id, expected.Version));
            if (!RuntimeSession.SameLogic(expected, installed with { Settings = expected.Settings })) throw new InvalidDataException($"Policy module changed: {expected.Id}.");
        }
        var runtime = RuntimeSessionFactory.Create(manifest.Options with { Mode = mode }, ControllerRegistry.Create(manifest.Options), catalog,
            manifest.ObservationKeys, manifest.ActionKeys);
        try
        {
            if (!runtime.ObservationKeys.SequenceEqual(manifest.ObservationKeys) || !runtime.ActionKeys.SequenceEqual(manifest.ActionKeys)) throw new InvalidDataException("Policy registry mismatch.");
            runtime.Controller.Import(Path.Combine(path, "weights.bin"));
            runtime.Controller.ImportInferenceState(manifest.ControllerState);
            return runtime;
        }
        catch { runtime.Dispose(); throw; }
    }
    private sealed record PolicyPointer(string Generation, string ManifestSha256);
}
