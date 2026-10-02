using ArtificialLife.Core;

namespace ArtificialLife.Modules.Hunger;

public sealed record HungerOptions
{
    public double GrowthPerSecond { get; init; } = 0.001;
    public double EatRadius { get; init; } = 2;

    public void Validate()
    {
        if (!double.IsFinite(GrowthPerSecond) || GrowthPerSecond <= 0 ||
            !double.IsFinite(EatRadius) || EatRadius <= 0)
            throw new ArgumentException("Hunger growth and eating radius must be positive and finite.");
    }
}

public sealed class HungerState
{
    private double level;
    public double Level
    {
        get => level;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            level = Math.Clamp(value, 0, 1);
        }
    }
}

/// <summary>Hunger dynamics and automatic contact feeding; reward is aggregated by Core.</summary>
public sealed class HungerModule : IWorldSystem, IObservationProvider, IDriveProvider
{
    private readonly int hungerType;
    public HungerOptions Options { get; }

    public HungerModule(HungerOptions options, TypeRegistry registry)
    {
        options.Validate();
        Options = options;
        hungerType = registry.Register("hunger.body.v1");
    }

    public void Reset(WorldState world, AgentState agent, Random random) => agent.Set(new HungerState());

    public void Update(WorldState world, AgentState agent, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        HungerState hunger = agent.Get<HungerState>();
        hunger.Level = Math.Min(1, hunger.Level + Options.GrowthPerSecond * elapsedSeconds);
        for (int index = world.Entities.Count - 1; index >= 0; index--)
        {
            Entity entity = world.Entities[index];
            if (!entity.TryGet<Nutrition>(out var nutrition) ||
                agent.Position.DistanceTo(entity.Position) > Options.EatRadius) continue;
            hunger.Level = Math.Max(0, hunger.Level - nutrition.HungerReduction);
            world.Entities.RemoveAt(index);
        }
    }

    public IEnumerable<ObservationToken> Observe(WorldState world, AgentState agent) =>
        [new(hungerType, [(float)agent.Get<HungerState>().Level])];

    public IEnumerable<DriveState> GetDrives(AgentState agent) =>
        [new("hunger", agent.Get<HungerState>().Level, 0, 1, 1)];
}
