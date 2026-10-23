using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Compatibility adapter for the original experiment configuration, outside the runtime host.</summary>
public static class RuntimeDefaults
{
    public static string RepositoryRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "configs", "default.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find configs/default.json; provide an explicit runtime configuration.");
    }
    public static RuntimeOptions FromLegacy(ExperimentOptions options, bool hunger = true)
    {
        string root = RepositoryRoot();
        return new()
        {
            World = options.World, Network = options.Network, Learning = options.Learning,
            PackageRoot = Path.Combine(root, "artifacts", "packages"), Inbox = Path.Combine(root, "artifacts", "activation"),
            Packages = (hunger ? new[] { "movement", "vision", "temperature", "homeostasis", "hunger", "food" } : ["movement", "vision", "temperature", "homeostasis"])
                .Select(id => new PackageReference(id, "1.0.0")).ToArray(),
            Settings = new()
            {
                ["temperature"] = JsonSerializer.SerializeToElement(options.Temperature),
                ["hunger"] = JsonSerializer.SerializeToElement(options.Hunger),
                ["vision"] = JsonSerializer.SerializeToElement(options.Vision),
                ["food"] = JsonSerializer.SerializeToElement(options.FoodSpawner),
                ["homeostasis"] = JsonSerializer.SerializeToElement(options.Reward)
            }
        };
    }
}
