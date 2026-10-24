using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record MotivationVariant
{
    public string Id { get; init; } = "homeostasis";
    public PackageReference Package { get; init; } = new("homeostasis", "1.0.0");
    public JsonElement Settings { get; init; } = JsonSerializer.SerializeToElement(new { });
}
public sealed record LearningVariant
{
    public string Id { get; init; } = "dqn";
    public ControllerSelection Controller { get; init; } = new();
}
public sealed record ExperimentPhase
{
    public string Id { get; init; } = "initial";
    public int Steps { get; init; } = 100000;
    public PackageReference[] Activate { get; init; } = [];
    /// <summary>Optional explicit evaluation world; otherwise capture effective phase conditions.</summary>
    public RuntimeOptions? EvaluationRuntime { get; init; }
}
public sealed record ExperimentDefinition
{
    public int SchemaVersion { get; init; } = 1;
    public RuntimeOptions Runtime { get; init; } = new();
    public string MotivationSlot { get; init; } = "homeostasis";
    public MotivationVariant[] Motivations { get; init; } = [new()];
    public LearningVariant[] Learners { get; init; } = [new()];
    public int[] TrainingSeeds { get; init; } = [42, 73, 101];
    /// <summary>Paired with TrainingSeeds; null uses the training seed for the world.</summary>
    public int[]? WorldSeeds { get; init; }
    public int[] EvaluationSeeds { get; init; } = [1001, 2002];
    public int EpisodesPerSeed { get; init; } = 1;
    public int EvaluationSteps { get; init; } = 1000;
    public int EvaluateEvery { get; init; } = 1000;
    public ExperimentPhase[] Phases { get; init; } = [new()];
    public BehaviorProfile Behavior { get; init; } = new();

    public static ExperimentDefinition Load(string path)
    {
        var value = JsonSerializer.Deserialize<ExperimentDefinition>(File.ReadAllText(path), PackageCatalog.Json)
            ?? throw new InvalidDataException("Missing experiment configuration.");
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        RuntimeOptions Paths(RuntimeOptions options) => options with
        {
            PackageRoot = Path.GetFullPath(options.PackageRoot, parent), Inbox = Path.GetFullPath(options.Inbox, parent)
        };
        value = value with
        {
            Runtime = Paths(value.Runtime),
            Phases = value.Phases.Select(p => p with { EvaluationRuntime = p.EvaluationRuntime is null ? null : Paths(p.EvaluationRuntime) }).ToArray()
        };
        value.Validate();
        return value;
    }

    public void Validate()
    {
        static bool Id(string id) => !string.IsNullOrWhiteSpace(id) && !id.Contains("--", StringComparison.Ordinal) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        if (SchemaVersion != 1 || TrainingSeeds.Length == 0 || TrainingSeeds.Distinct().Count() != TrainingSeeds.Length ||
            EvaluationSeeds.Length == 0 || EvaluationSeeds.Distinct().Count() != EvaluationSeeds.Length ||
            WorldSeeds is not null && WorldSeeds.Length != TrainingSeeds.Length ||
            EpisodesPerSeed < 1 || EvaluationSteps < 1 || EvaluateEvery < 1 || Phases.Length == 0 || Motivations.Length == 0 || Learners.Length == 0 ||
            Phases.Any(p => !Id(p.Id) || p.Steps < 1) || Motivations.Any(m => !Id(m.Id) || m.Settings.ValueKind != JsonValueKind.Object) ||
            Learners.Any(l => !Id(l.Id)) || Phases.Select(p => p.Id).Distinct().Count() != Phases.Length ||
            Motivations.Select(m => m.Id).Distinct().Count() != Motivations.Length || Learners.Select(l => l.Id).Distinct().Count() != Learners.Length ||
            Runtime.Packages.Count(p => p.Id == MotivationSlot) != 1)
            throw new InvalidDataException("Invalid experiment identities, budgets, seeds or motivation slot.");
        var evaluationWorlds = EvaluationSeeds.SelectMany(seed => Enumerable.Range(0, EpisodesPerSeed).Select(e => checked(seed + e * 10000))).ToArray();
        if (evaluationWorlds.Distinct().Count() != evaluationWorlds.Length || evaluationWorlds.Intersect(TrainingSeeds.Concat(WorldSeeds ?? TrainingSeeds)).Any())
            throw new InvalidDataException("Evaluation worlds must be unique and disjoint from training seeds.");
        if (Behavior.Criteria.Any(c => !Phases.Any(p => p.Id == c.Scenario) || string.IsNullOrWhiteSpace(c.Metric) ||
            c.Minimum is null && c.Maximum is null || c.Minimum is double min && !double.IsFinite(min) ||
            c.Maximum is double max && !double.IsFinite(max) || c.Minimum > c.Maximum))
            throw new InvalidDataException("Invalid behavioral criteria.");
        _ = BehaviorEvaluators.Complete(BehaviorEvaluators.Create(Behavior));
        foreach (var learner in Learners) _ = ControllerRegistry.Resolve(Runtime with { Controller = learner.Controller });
        foreach (var options in Phases.Where(p => p.EvaluationRuntime is not null).Select(p => p.EvaluationRuntime!).Prepend(Runtime)) options.World.Validate();
    }

    public IEnumerable<ExperimentJob> Jobs(string? variant = null)
    {
        foreach (var motivation in Motivations)
        foreach (var learner in Learners)
        {
            string id = motivation.Id + "--" + learner.Id;
            if (variant is not null && id != variant) continue;
            for (int i = 0; i < TrainingSeeds.Length; i++)
            {
                int seed = TrainingSeeds[i], worldSeed = (WorldSeeds ?? TrainingSeeds)[i];
                var settings = Runtime.Settings.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
                settings.Remove(MotivationSlot);
                settings[motivation.Package.Id] = motivation.Settings.Clone();
                var runtime = ControllerRegistry.Resolve(Runtime with
                {
                    Packages = Runtime.Packages.Select(p => p.Id == MotivationSlot ? motivation.Package : p).ToArray(),
                    Settings = settings, Controller = learner.Controller, ControllerSeed = seed, WorldSeed = worldSeed
                });
                yield return new(id, seed, worldSeed, runtime, this);
            }
        }
    }
}

public sealed record ExperimentJob(string Variant, int Seed, int WorldSeed, RuntimeOptions Runtime, ExperimentDefinition Definition)
{
    public string TreatmentHash { get; init; } = "";
    public string ConfigurationHash { get; init; } = "";
    public string ProtocolHash { get; init; } = "";
    public string ExecutionHash { get; init; } = "";
}

public static class ExperimentJson
{
    public static readonly JsonSerializerOptions Lines = new(PackageCatalog.Json) { WriteIndented = false };
    public static string Hash<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Lines);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
    public static void Atomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, PackageCatalog.Json));
        File.Move(path + ".tmp", path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), PackageCatalog.Json)
        ?? throw new InvalidDataException($"Empty {path}.");
}
