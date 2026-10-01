using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Brain;

namespace ArtificialLife.Application;

public sealed record CheckpointManifest(int FormatVersion, ExperimentOptions Options, string[] ObservationKeys,
    string[] ActionKeys, string WeightSha256, int TrainingSteps);

/// <summary>Weights plus schema/configuration identity; refuses silent registry remapping.</summary>
public static class Checkpoint
{
    public static void Save(string directory, SimulationSession session, DqnBrain brain, int? trainingSteps = null)
    {
        if (trainingSteps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trainingSteps));
        }
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "weights.tmp");
        brain.Save(temporary);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temporary)));
        var manifest = new CheckpointManifest(1, session.Options, session.Observations.Keys.ToArray(), session.Actions.Keys.ToArray(), hash, trainingSteps ?? session.Options.Learning.TrainingSteps);
        File.WriteAllText(Path.Combine(directory, "manifest.tmp"), JsonSerializer.Serialize(manifest, ExperimentOptions.Json));
        File.Move(temporary, Path.Combine(directory, "weights.bin"), overwrite: true);
        File.Move(Path.Combine(directory, "manifest.tmp"), Path.Combine(directory, "manifest.json"), overwrite: true);
    }

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

    public static void Load(string directory, SimulationSession session, DqnBrain brain)
    {
        CheckpointManifest manifest = Read(directory);
        if (manifest.Options.Network != session.Options.Network || !manifest.ObservationKeys.SequenceEqual(session.Observations.Keys) ||
            !manifest.ActionKeys.SequenceEqual(session.Actions.Keys))
        {
            throw new InvalidDataException("Checkpoint network or registry identity does not match the session.");
        }
        brain.Load(Path.Combine(directory, "weights.bin"));
    }
}
