using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Core;
using ArtificialLife.Packages;

namespace ArtificialLife.Cli;

/// <summary>Package tooling delegates publication and integrity to the existing importer/catalogue.</summary>
internal static class PackageCommands
{
    public static int Run(string[] args)
    {
        string operation = args.FirstOrDefault() ?? "verify";
        var options = ArtificialLife.Application.RuntimeCommands.Arguments(args.Skip(1).ToArray());
        if (operation is not ("verify" or "publish") || options.Keys.Any(k => k is not ("--sources" or "--root")))
            throw new ArgumentException("package verify|publish --sources PATH1;PATH2 [--root CATALOG]");
        string[] sources = options["--sources"].Split(';', StringSplitOptions.RemoveEmptyEntries);
        string temporary = Path.Combine(Path.GetTempPath(), "live-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            var imported = new RuntimePackageImporter(temporary).Import(sources);
            var catalog = new PackageCatalog(temporary);
            foreach (var package in imported)
            {
                ModuleManifest manifest = catalog.Read(package.Reference);
                string directory = catalog.DirectoryFor(package.Reference);
                foreach (var file in manifest.PresentationFiles.Append(new KeyValuePair<string, string>(manifest.PresentationAssembly ?? "", manifest.PresentationHash ?? "")))
                {
                    if (file.Key.Length == 0) continue;
                    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PackageCatalog.FileWithin(directory, file.Key))));
                    if (!actual.Equals(file.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Presentation checksum mismatch: " + file.Key);
                }
                string assembly = PackageCatalog.FileWithin(directory, manifest.Assembly);
                var context = new ModuleLoadContext(assembly);
                try
                {
                    Type type = context.LoadPluginAssembly(assembly).GetType(manifest.EntryType, throwOnError: true)!;
                    if (!typeof(IRuntimeModule).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                        throw new InvalidDataException("EntryType must be a concrete IRuntimeModule with a public default constructor.");
                }
                finally { context.Unload(); }
            }
            if (operation == "publish") imported = new RuntimePackageImporter(options["--root"]).Import(sources);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", operation, packages = imported }, PackageCatalog.Json));
            return 0;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
