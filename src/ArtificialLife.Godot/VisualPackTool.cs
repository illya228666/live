using Godot;

namespace ArtificialLife.Godot;

/// <summary>Resource packaging only; never constructs a simulation.</summary>
public static class VisualPackTool
{
    public static bool TryRun(string[] args, SceneTree tree)
    {
        int sourceIndex = Array.IndexOf(args, "--pack-source");
        if (sourceIndex < 0) return false;
        try
        {
            string source = Path.GetFullPath(args[sourceIndex + 1]);
            string output = Path.GetFullPath(args[Array.IndexOf(args, "--pack-output") + 1]);
            int prefixIndex = Array.IndexOf(args, "--pack-prefix");
            string prefix = prefixIndex < 0 ? "live-foundation/1" : args[prefixIndex + 1].Trim('/');
            if (prefix.Split('/').Length < 2 || prefix.Split('/').Any(p => string.IsNullOrWhiteSpace(p) || p is ".." or "." || p.Contains(':'))) throw new ArgumentException("Pack namespace must include module/version.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var pack = new PckPacker();
            if (pack.PckStart(output) != Error.Ok) throw new IOException("Cannot create resource pack.");
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (pack.AddFile("res://" + prefix + "/" + relative, file) != Error.Ok) throw new IOException("Cannot pack " + relative);
            }
            if (pack.Flush() != Error.Ok) throw new IOException("Cannot finish resource pack.");
            GD.Print("VISUAL_PACK_OK " + output); tree.Quit();
        }
        catch (Exception ex) { GD.PushError(ex.Message); tree.Quit(1); }
        return true;
    }
}
