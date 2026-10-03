using ArtificialLife.Core;
using ArtificialLife.Modules.Hunger;
using ArtificialLife.Modules.Vision;

namespace ArtificialLife.Application;

public sealed record FoodSpawnerOptions
{
    public int SpawnIntervalTicks { get; init; } = 200;
    public int MaxApples { get; init; } = 8;
    public double MinSpawnDistanceFromAgent { get; init; } = 10;

    public void Validate()
    {
        if (SpawnIntervalTicks < 1 || MaxApples < 1 || !double.IsFinite(MinSpawnDistanceFromAgent) || MinSpawnDistanceFromAgent < 0)
            throw new ArgumentException("Food spawn interval and capacity must be positive; distance must be finite and nonnegative.");
    }
}

/// <summary>Identifies apples independently of the generic nutrition and visual components.</summary>
public sealed class Apple;

/// <summary>Independent tick-based food supply. Eating never schedules a replacement.</summary>
public sealed class FoodSpawner : IWorldSystem
{
    private Random? random;
    private long nextSpawnTick;
    public FoodSpawnerOptions Options { get; }
    public long TotalSpawned { get; private set; }

    public FoodSpawner(FoodSpawnerOptions options)
    {
        options.Validate();
        Options = options;
    }

    public void Reset(WorldState world, AgentState agent, Random random)
    {
        this.random = random;
        nextSpawnTick = Options.SpawnIntervalTicks;
        TotalSpawned = 0;
    }

    public void Update(WorldState world, AgentState agent, double elapsedSeconds)
    {
        if (random is null) throw new InvalidOperationException("Reset the food spawner before updating it.");
        if (world.Step < nextSpawnTick) return;
        nextSpawnTick = world.Step + Options.SpawnIntervalTicks;
        if (world.Entities.Count(entity => entity.TryGet<Apple>(out _)) >= Options.MaxApples) return;

        // Bound rejection sampling so impossible or very restrictive worlds cannot stall a life.
        for (int attempt = 0; attempt < 256; attempt++)
        {
            var position = new Position(random.NextDouble() * world.Options.Width, random.NextDouble() * world.Options.Height);
            if (position.DistanceTo(agent.Position) < Options.MinSpawnDistanceFromAgent) continue;
            var apple = new Entity { Position = position };
            apple.Set(new Apple());
            apple.Set(new Nutrition(0.20));
            apple.Set(new VisualAppearance(AppearanceType.Diamond, diameter: 2));
            world.Entities.Add(apple);
            TotalSpawned++;
            return;
        }
    }
}
