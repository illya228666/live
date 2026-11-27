using System.Security.Cryptography;

namespace ArtificialLife.Packages;

/// <summary>Reads the raster subset of published visual resources by verified content.</summary>
public sealed record PortableSprite(string Name, byte[] Bytes)
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static PortableSprite? Read(string directory, string path, IReadOnlyDictionary<string, string> declared)
    {
        if (!declared.TryGetValue(path, out var expected) || path.Contains(':')) return null;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".webp")) return null;
        try
        {
            string file = PackageCatalog.FileWithin(directory, path);
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 12 or > MaxBytes) return null;
            var bytes = File.ReadAllBytes(file);
            bool raster = extension == ".png"
                ? bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                : bytes.AsSpan().StartsWith("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
            if (!raster) return null;
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return hash.Equals(expected, StringComparison.OrdinalIgnoreCase) ? new(hash + extension, bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
