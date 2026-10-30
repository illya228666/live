using System.Text.Json;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using ArtificialLife.Modules.Vision;
using ArtificialLife.Modules.Hunger;

namespace ArtificialLife.Application;

public sealed record EvaluationOptions
{
    public int[] Seeds { get; init; } = [1001, 2002, 3003, 4004, 5005];
    public int EpisodesPerSeed { get; init; } = 4;
}

public sealed record ExperimentOptions
{
    public WorldOptions World { get; init; } = new();
    public TemperatureOptions Temperature { get; init; } = new();
    public HungerOptions Hunger { get; init; } = new();
    public VisionOptions Vision { get; init; } = new();
    public FoodSpawnerOptions FoodSpawner { get; init; } = new();
    public RewardOptions Reward { get; init; } = new();
    public NetworkOptions Network { get; init; } = new();
    public LearningOptions Learning { get; init; } = new();
    public EvaluationOptions Evaluation { get; init; } = new();
    public static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static ExperimentOptions Load(string path)
    {
        ExperimentOptions options = JsonSerializer.Deserialize<ExperimentOptions>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Configuration is empty.");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        World.Validate();
        Temperature.Validate();
        Hunger.Validate();
        FoodSpawner.Validate();
        if (Vision.MaxPerAppearance < 1) throw new ArgumentException("Vision.MaxPerAppearance must be positive.");
        Network.Validate();
        Learning.Validate();
        if (Evaluation.Seeds.Length == 0 || Evaluation.EpisodesPerSeed < 1 || !double.IsFinite(Reward.ImprovementWeight) ||
            Reward.ImprovementWeight is < 0 or > 1 || !double.IsFinite(Reward.MaximumDriveWeight) || Reward.MaximumDriveWeight <= 0)
        {
            throw new ArgumentException("Invalid evaluation or reward options.");
        }
    }
}
