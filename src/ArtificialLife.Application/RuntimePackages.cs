using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed class PackageCatalog(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string DirectoryFor(PackageReference reference)
    {
        static bool Valid(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && value is not ("." or "..");
        if (!Valid(reference.Id) || !Valid(reference.Version)) throw new InvalidDataException("Invalid package identity.");
        return Path.Combine(Root, reference.Id, reference.Version);
    }
    public ModuleManifest Read(PackageReference reference)
    {
        string directory = DirectoryFor(reference);
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(Path.Combine(directory, "module.json")), Json)
            ?? throw new InvalidDataException("Missing module manifest.");
        if (manifest.Id != reference.Id || manifest.Version != reference.Version || manifest.ApiVersion != 1)
            throw new InvalidDataException("Package identity or API version mismatch.");
        Verify(directory, manifest.Assembly, manifest.LogicHash);
        foreach (var file in manifest.LogicFiles.Concat(manifest.PresentationFiles)) Verify(directory, file.Key, file.Value);
        if (manifest.PresentationAssembly is not null)
        {
            if (manifest.PresentationType is null) throw new InvalidDataException("Missing presentation type.");
            Verify(directory, manifest.PresentationAssembly, manifest.PresentationHash ?? "");
        }
        return manifest;
    }
    public static string FileWithin(string directory, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Package paths must be relative.");
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Package file escapes its directory.");
        return path;
    }
    private static void Verify(string directory, string relative, string hash)
    {
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FileWithin(directory, relative))));
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Package checksum mismatch: {relative}.");
    }
    public static string ManifestHash(string directory) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "module.json"))));
}

/// <summary>One shared contract identity, private dependencies and cooperative unload per generation.</summary>
public sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;
    private readonly Dictionary<string, Assembly> shared;
    public ModuleLoadContext(string assemblyPath, params Assembly[] sharedAssemblies) : base(isCollectible: true)
    {
        resolver = new(assemblyPath);
        shared = sharedAssemblies.Append(typeof(IRuntimeModule).Assembly).Distinct().ToDictionary(a => a.GetName().Name!);
    }
    protected override Assembly? Load(AssemblyName name)
    {
        if (shared.TryGetValue(name.Name!, out var assembly)) return assembly;
        string? path = resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadPluginAssembly(path);
    }
    public Assembly LoadPluginAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        string? path = resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}

internal sealed class LoadedRuntimeModule : IDisposable
{
    private ModuleLoadContext? context;
    private IRuntimeModule? module;
    public ModuleManifest Manifest { get; }
    public string SourceManifestHash { get; }
    public string Directory { get; }
    public ModuleBuilder Builder { get; private set; }
    public IRuntimeModule Module => module!;
    public WeakReference UnloadReference { get; }
    public LoadedRuntimeModule(PackageCatalog catalog, ModuleManifest manifest, TypeRegistry observations, TypeRegistry actions)
    {
        Manifest = manifest;
        Directory = catalog.DirectoryFor(new(manifest.Id, manifest.Version));
        SourceManifestHash = PackageCatalog.ManifestHash(Directory);
        string path = PackageCatalog.FileWithin(Directory, manifest.Assembly);
        context = new(path);
        UnloadReference = new(context);
        Builder = new(manifest, observations, actions);
        try
        {
            module = (IRuntimeModule)(Activator.CreateInstance(context.LoadPluginAssembly(path).GetType(manifest.EntryType, throwOnError: true)!)
                ?? throw new InvalidDataException("Cannot create runtime module."));
            module.Register(Builder);
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        try { module?.Dispose(); }
        finally { module = null; Builder = null!; context?.Unload(); context = null; }
    }
}

public sealed record ActivationOutcome(string Id, bool Accepted, string Message, long Revision);

/// <summary>Only .ready.json requests are consumed. A producer renames a fully written .tmp file.</summary>
public sealed class ActivationInbox(string directory)
{
    public string Directory { get; } = Path.GetFullPath(directory);
    public IEnumerable<(string Path, ActivationRequest? Request, string? Error)> ReadReady()
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.ready.json").Order(StringComparer.Ordinal))
        {
            ActivationRequest? request = null;
            string? error = null;
            try { request = JsonSerializer.Deserialize<ActivationRequest>(File.ReadAllText(path), PackageCatalog.Json) ?? throw new InvalidDataException("Empty request."); }
            catch (Exception ex) { error = ex.Message; }
            yield return (path, request, error);
        }
    }
    public static void Complete(string path, ActivationOutcome outcome)
    {
        string result = path[..^".ready.json".Length] + ".result.json";
        string temporary = result + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(outcome, PackageCatalog.Json));
        File.Move(temporary, result, overwrite: true);
        File.Delete(path);
    }
}
