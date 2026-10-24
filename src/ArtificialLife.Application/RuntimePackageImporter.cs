using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record ImportedPackage(PackageReference Reference, bool AlreadyPresent);

/// <summary>Imports immutable packages without loading plugin code or changing a running world.</summary>
public sealed class RuntimePackageImporter(string packageRoot)
{
    private readonly PackageCatalog catalog = new(packageRoot);

    public ImportedPackage[] Import(IEnumerable<string> paths)
    {
        string[] inputs = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Length == 0) throw new InvalidDataException("Select a package folder, module.json or ZIP archive.");
        Directory.CreateDirectory(catalog.Root);
        string stage = Path.Combine(catalog.Root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var staged = new PackageCatalog(Path.Combine(stage, "packages"));
            var prepared = new List<(PackageReference Reference, string Source, string Target, bool Exists)>();
            foreach (string input in inputs)
            foreach (string source in Sources(input, stage))
            {
                if (Within(source, stage)) throw new InvalidDataException("Select the package folder itself, not its enclosing catalogue.");
                RejectLink(source);
                string manifestPath = Path.Combine(source, "module.json"); RejectLink(manifestPath);
                var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath), PackageCatalog.Json)
                    ?? throw new InvalidDataException("Empty module.json.");
                if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version))
                    throw new InvalidDataException("module.json must specify a package id and version.");
                var reference = new PackageReference(manifest.Id, manifest.Version);
                string target = catalog.DirectoryFor(reference), temporary = staged.DirectoryFor(reference);
                if (prepared.Any(p => p.Reference.Id == reference.Id))
                    throw new InvalidDataException($"Select only one package version per id: {reference.Id}.");
                CopyDirectory(source, temporary);
                _ = staged.Read(reference); // Identity, API, file paths and all declared SHA256 hashes.
                bool exists = Directory.Exists(target);
                if (exists)
                {
                    _ = catalog.Read(reference);
                    if (!SameFiles(temporary, target))
                        throw new InvalidDataException($"Published package {reference.Id}@{reference.Version} differs. Publish a new version.");
                }
                prepared.Add((reference, temporary, target, exists));
            }
            // Validate the whole selection before publishing; activation remains a separate host transaction.
            foreach (var package in prepared.Where(p => !p.Exists))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(package.Target)!);
                Directory.Move(package.Source, package.Target); // Never overwrite a published version.
            }
            return prepared.Select(p => new ImportedPackage(p.Reference, p.Exists)).ToArray();
        }
        finally
        {
            // Only the exact, locally generated staging directory can be recursively removed.
            if (!Within(catalog.Root, stage) || !Path.GetFileName(stage).StartsWith(".import-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected package staging path.");
            Directory.Delete(stage, true);
        }
    }

    private static IEnumerable<string> Sources(string input, string stage)
    {
        RejectLink(input);
        if (Directory.Exists(input))
        {
            if (!File.Exists(Path.Combine(input, "module.json"))) throw new InvalidDataException("The selected package folder must contain module.json.");
            return [input];
        }
        if (Path.GetFileName(input).Equals("module.json", StringComparison.OrdinalIgnoreCase)) return [Path.GetDirectoryName(input)!];
        if (!Path.GetExtension(input).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use a package folder, module.json or ZIP. A standalone DLL is not a complete package.");
        string extracted = Path.Combine(stage, "zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extracted);
        using var archive = ZipFile.OpenRead(input);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.Split('/').Any(s => s is "." or ".." || s.Contains(':')) || Path.IsPathRooted(name) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP contains an unsafe path or symbolic link.");
            string destination = PackageCatalog.FileWithin(extracted, name.TrimEnd('/'));
            if (name.EndsWith('/')) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }
        }
        string[] manifests = Directory.GetFiles(extracted, "module.json", SearchOption.AllDirectories);
        if (manifests.Length == 0) throw new InvalidDataException("The ZIP contains no module.json.");
        return manifests.Select(p => Path.GetDirectoryName(p)!).ToArray();
    }

    private static void CopyDirectory(string source, string target)
    {
        RejectLink(source); Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source)) { RejectLink(file); File.Copy(file, Path.Combine(target, Path.GetFileName(file))); }
        foreach (string child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static bool SameFiles(string a, string b)
    {
        var first = Files(a); var second = Files(b);
        return first.Count == second.Count && first.All(p => second.TryGetValue(p.Key, out string? hash) && hash == p.Value);
    }

    private static Dictionary<string, string> Files(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string directory)
        {
            RejectLink(directory);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                RejectLink(file);
                files.Add(Path.GetRelativePath(root, file), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            }
            foreach (string child in Directory.EnumerateDirectories(directory)) Visit(child);
        }
        Visit(root); return files;
    }

    private static bool Within(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package files and folders must not be symbolic links.");
    }
}
