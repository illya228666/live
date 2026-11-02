using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ArtificialLife.Application;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class RuntimePackageImporterTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows directory publication must tolerate transient readers.")]
    public async Task PackagePublicationSurvivesATransientWindowsReader()
    {
        using var fixture = new Fixture();
        string source = fixture.Package("sample");
        // Leave copying work after the manifest so the watcher can acquire a real OS handle before publication.
        File.WriteAllBytes(Path.Combine(source, "padding.bin"), new byte[32 * 1024 * 1024]);
        Directory.CreateDirectory(fixture.Catalogue);
        var opened = new TaskCompletionSource<SafeFileHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(fixture.Catalogue, "module.json") { IncludeSubdirectories = true };
        watcher.Created += (_, e) =>
        {
            for (int attempt = 0; attempt < 100 && !opened.Task.IsCompleted; attempt++)
            {
                try
                {
                    // A directory scanner may omit FILE_SHARE_DELETE even while sharing child-file reads.
                    var reader = OpenDirectory(Path.GetDirectoryName(e.FullPath)!, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                    if (reader.IsInvalid) { reader.Dispose(); Thread.Sleep(1); continue; }
                    if (!opened.TrySetResult(reader)) reader.Dispose();
                    return;
                }
                catch (IOException) { Thread.Sleep(1); }
            }
        };
        watcher.EnableRaisingEvents = true;
        var publication = Task.Run(() => new RuntimePackageImporter(fixture.Catalogue).Import([source]), TestContext.Current.CancellationToken);
        using (var reader = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            await Task.Delay(400, TestContext.Current.CancellationToken);
        var result = Assert.Single(await publication);
        Assert.Equal(new("sample", "1.0.0"), result.Reference);
        Assert.Equal("logic", File.ReadAllText(Path.Combine(fixture.Catalogue, "sample/1.0.0/Fixture.dll")));
        Assert.Empty(Directory.GetDirectories(fixture.Catalogue, ".import-*"));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FolderAndManifestImportCompletePackageAndRepeatIsIdempotent(bool manifest)
    {
        using var fixture = new Fixture(); string source = fixture.Package("sample");
        var imported = new RuntimePackageImporter(fixture.Catalogue).Import([manifest ? Path.Combine(source, "module.json") : source]);
        var result = Assert.Single(imported);
        Assert.Equal(new("sample", "1.0.0"), result.Reference); Assert.False(result.AlreadyPresent);
        Assert.Equal("image", File.ReadAllText(Path.Combine(fixture.Catalogue, "sample/1.0.0/assets/sprite.png")));
        Assert.True(Assert.Single(new RuntimePackageImporter(fixture.Catalogue).Import([source])).AlreadyPresent);
        Assert.Empty(Directory.GetDirectories(fixture.Catalogue, ".import-*"));
        Assert.True(File.Exists(Path.Combine(source, "module.json")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ZipSupportsRootPackageAndContainingFolder(bool wrapped)
    {
        using var fixture = new Fixture(); string source = fixture.Package("sample");
        string zip = Path.Combine(fixture.Root, "package.zip");
        ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Fastest, includeBaseDirectory: wrapped);
        Assert.Equal("sample", Assert.Single(new RuntimePackageImporter(fixture.Catalogue).Import([zip])).Reference.Id);
    }

    [Fact]
    public void MultipleInputsAndZipBundlesAreImportedTogether()
    {
        using var fixture = new Fixture(); fixture.Package("first"); fixture.Package("second");
        string zip = Path.Combine(fixture.Root, "bundle.zip"); ZipFile.CreateFromDirectory(fixture.Sources, zip);
        var imported = new RuntimePackageImporter(fixture.Catalogue).Import([zip]);
        Assert.Equal(2, imported.Length);
        Assert.Equal(new[] { "first", "second" }, imported.Select(p => p.Reference.Id).Order());
    }

    [Fact]
    public void InvalidSelectionDoesNotPublishItsValidSiblingOrChangeAnInstalledVersion()
    {
        using var fixture = new Fixture(); string a = fixture.Package("first"), b = fixture.Package("second");
        File.WriteAllText(Path.Combine(b, "Fixture.dll"), "changed");
        Assert.Throws<InvalidDataException>(() => new RuntimePackageImporter(fixture.Catalogue).Import([a, b]));
        Assert.False(Directory.Exists(Path.Combine(fixture.Catalogue, "first")));
        new RuntimePackageImporter(fixture.Catalogue).Import([a]);
        string installed = Path.Combine(fixture.Catalogue, "first/1.0.0/module.json"); string before = File.ReadAllText(installed);
        File.AppendAllText(Path.Combine(a, "module.json"), " ");
        Assert.Throws<InvalidDataException>(() => new RuntimePackageImporter(fixture.Catalogue).Import([a]));
        Assert.Equal(before, File.ReadAllText(installed));
    }

    [Theory]
    [InlineData("../escape.dll")] [InlineData("/escape.dll")]
    [InlineData("folder/../../escape.dll")] [InlineData("file.dll:stream")]
    public void ZipRejectsUnsafePaths(string path)
    {
        using var fixture = new Fixture(); string zip = Path.Combine(fixture.Root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(path).Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => new RuntimePackageImporter(fixture.Catalogue).Import([zip]));
        Assert.Empty(Directory.GetDirectories(fixture.Catalogue));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.dll")));
    }

    [Fact]
    public void ZipRejectsSymbolicLinksAndMissingManifests()
    {
        using var fixture = new Fixture(); string zip = Path.Combine(fixture.Root, "link.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("outside");
        }
        Assert.Throws<InvalidDataException>(() => new RuntimePackageImporter(fixture.Catalogue).Import([zip]));
        string empty = Path.Combine(fixture.Root, "empty.zip");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        Assert.Throws<InvalidDataException>(() => new RuntimePackageImporter(fixture.Catalogue).Import([empty]));
    }

    [Fact]
    public void DuplicateIdsAndStandaloneDllAreRejected()
    {
        using var fixture = new Fixture(); string source = fixture.Package("sample");
        var importer = new RuntimePackageImporter(fixture.Catalogue);
        Assert.Throws<InvalidDataException>(() => importer.Import([source, Path.Combine(source, "module.json")]));
        Assert.Throws<InvalidDataException>(() => importer.Import([Path.Combine(source, "Fixture.dll")]));
        Assert.Empty(Directory.GetDirectories(fixture.Catalogue));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ArtificialLife-import", Guid.NewGuid().ToString("N"));
        public string Catalogue => Path.Combine(Root, "catalogue");
        public string Sources => Path.Combine(Root, "sources");
        public string Package(string id)
        {
            string path = Path.Combine(Sources, id); Directory.CreateDirectory(Path.Combine(path, "assets"));
            File.WriteAllText(Path.Combine(path, "Fixture.dll"), "logic");
            File.WriteAllText(Path.Combine(path, "Fixture.deps.json"), "{}");
            File.WriteAllText(Path.Combine(path, "assets/sprite.png"), "image");
            string Hash(string relative) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(path, relative))));
            var manifest = new ModuleManifest(id, "1.0.0", 1, "Fixture.dll", "Fixture.Module")
            {
                LogicHash = Hash("Fixture.dll"), LogicFiles = new() { ["Fixture.deps.json"] = Hash("Fixture.deps.json") },
                PresentationFiles = new() { ["assets/sprite.png"] = Hash("assets/sprite.png") }
            };
            File.WriteAllText(Path.Combine(path, "module.json"), JsonSerializer.Serialize(manifest, PackageCatalog.Json));
            return path;
        }
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.Combine(Path.GetTempPath(), "ArtificialLife-import") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test path.");
            Directory.Delete(target, true);
        }
    }
}
