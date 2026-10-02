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
        Network.Validate();
        Learning.Validate();
        if (Evaluation.Seeds.Length == 0 || Evaluation.EpisodesPerSeed < 1 || !double.IsFinite(Reward.ImprovementWeight) ||
            Reward.ImprovementWeight is < 0 or > 1 || !double.IsFinite(Reward.MaximumDriveWeight) || Reward.MaximumDriveWeight <= 0)
        {
            throw new ArgumentException("Invalid evaluation or reward options.");
        }
    }
}

/// <summary>Composition root; new modules register here, without edits in Brain or Godot.</summary>
public sealed class SimulationSession
{
    public TypeRegistry Observations { get; }
    public TypeRegistry Actions { get; }
    public TemperatureModule Temperature { get; }
    public VisionModule Vision { get; }
    public HungerModule Hunger { get; }
    public Entity Fire { get; }
    public Simulation Simulation { get; }
    public ExperimentOptions Options { get; }

    public SimulationSession(ExperimentOptions options, SimulationLifecycle lifecycle = SimulationLifecycle.Episodic)
    {
        options.Validate();
        Options = options;
        Observations = new TypeRegistry(options.Network.ObservationCapacity);
        Actions = new TypeRegistry(options.Network.ActionCapacity);
        Temperature = new TemperatureModule(options.Temperature, Observations);
        Vision = new VisionModule(new DirectGeometryVisionBackend(), Observations);
        Hunger = new HungerModule(options.Hunger, Observations);
        Fire = new Entity { Position = new Position(options.World.Width / 2, options.World.Height / 2) };
        Fire.Set(new HeatEmitter(options.Temperature.FireStrength, options.Temperature.FireRadius,
            options.Temperature.OscillationPeriod, options.Temperature.OscillationAmplitude));
        Fire.Set(new VisualAppearance(AppearanceType.Disc, diameter: 4));
        List<Entity> entities = [Fire];
        foreach (Position fraction in new Position[] { new(0.3, 0.3), new(0.7, 0.3), new(0.3, 0.7), new(0.7, 0.7) })
        {
            var apple = new Entity { Position = new Position(fraction.X * options.World.Width, fraction.Y * options.World.Height) };
            apple.Set(new VisualAppearance(AppearanceType.Diamond, diameter: 2));
            apple.Set(new Nutrition(0.20));
            entities.Add(apple);
        }
        var movement = new MovementProvider(Actions);
        Simulation = new Simulation(options.World, options.Reward, [Temperature, Hunger], [Temperature, Vision, Hunger],
            [movement], [Temperature, Hunger], lifecycle, entities);
        Simulation.Reset(options.Learning.Seed);
    }
}
