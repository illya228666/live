using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Brain;

namespace ArtificialLife.Application;

public sealed record CheckpointManifest(int FormatVersion, ExperimentOptions Options, string[] ObservationKeys,
    string[] ActionKeys, string WeightSha256, int TrainingSteps);

/// <summary>Weights plus schema/configuration identity; refuses silent registry remapping.</summary>
public static class Checkpoint
{
    public static CheckpointManifest Read(string directory)
    {
        var manifest = JsonSerializer.Deserialize<CheckpointManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")), ExperimentOptions.Json)
            ?? throw new InvalidDataException("Missing checkpoint metadata.");
        if (manifest.FormatVersion != 1)
        {
            throw new InvalidDataException("Unsupported checkpoint format.");
        }
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "weights.bin"))));
        if (hash != manifest.WeightSha256)
        {
            throw new InvalidDataException("Checkpoint weight checksum mismatch.");
        }
        manifest.Options.Validate();
        return manifest;
    }

}
