using ArtificialLife.Application;
using ArtificialLife.Core;
using ArtificialLife.Modules.Hunger;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class FoodSpawnerTests
{
    internal static void Advance(SimulationSession session, int ticks)
    {
        var stay = session.Simulation.LegalActions().Single(action => action.Parameters.SequenceEqual(new float[] { 0, 0 }));
        for (int tick = 0; tick < ticks; tick++) session.Simulation.Step(stay);
    }

    private static SimulationSession Create(FoodSpawnerOptions? food = null, int seed = 42) => new(
        new ExperimentOptions { FoodSpawner = food ?? new(), Learning = new() { Seed = seed } }, SimulationLifecycle.Continuous);
    private static Entity[] Apples(SimulationSession session) => session.Simulation.World.Entities.Where(entity => entity.TryGet<Apple>(out _)).ToArray();

    [Fact]
    public void SpawnsOneAppleAtEachConfiguredIntervalAndResetRestartsSchedule()
    {
        var session = Create(new() { SpawnIntervalTicks = 7 });
        Assert.Empty(Apples(session));
        Advance(session, 6);
        Assert.Empty(Apples(session));
        Advance(session, 1);
        Assert.Single(Apples(session));
        Advance(session, 7);
        Assert.Equal(2, Apples(session).Length);
        session.Simulation.Reset(42);
        Assert.Empty(Apples(session));
        Assert.Equal(0, session.FoodSpawner.TotalSpawned);
        Advance(session, 7);
        Assert.Single(Apples(session));
    }

    [Fact]
    public void MaxApplesIsRespectedAcrossManyIntervals()
    {
        var session = Create(new() { SpawnIntervalTicks = 2, MaxApples = 3 });
        Advance(session, 100);
        Assert.Equal(3, Apples(session).Length);
        Assert.Equal(3, session.FoodSpawner.TotalSpawned);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(999)]
    public void EverySpawnIsInsideWorldAndFarEnoughFromCurrentAgent(int seed)
    {
        var session = Create(new() { SpawnIntervalTicks = 1, MaxApples = 500, MinSpawnDistanceFromAgent = 30 }, seed);
        for (int tick = 0; tick < 100; tick++)
        {
            // Move the agent between attempts; distance is checked against its current position.
            session.Simulation.Agent.Position = new Position(tick % 2 == 0 ? 0 : 50, tick % 3 == 0 ? 100 : 50);
            Advance(session, 1);
            Entity apple = Apples(session)[^1];
            Assert.True(session.Simulation.World.Contains(apple.Position));
            Assert.True(apple.Position.DistanceTo(session.Simulation.Agent.Position) >= 30);
        }
        Assert.Equal(100, session.FoodSpawner.TotalSpawned);
    }

    [Fact]
    public void EqualSeedsReproduceSpawnSequenceAndDifferentSeedsChangeIt()
    {
        Position[] Sequence(int seed)
        {
            var session = Create(new() { SpawnIntervalTicks = 1, MaxApples = 50 }, seed);
            Advance(session, 50);
            return Apples(session).Select(apple => apple.Position).ToArray();
        }
        Assert.Equal(Sequence(123), Sequence(123));
        Assert.NotEqual(Sequence(123), Sequence(124));
    }

    [Fact]
    public void EatingFreesCapacityButReplacementWaitsForGlobalInterval()
    {
        var session = Create(new() { SpawnIntervalTicks = 10, MaxApples = 2 });
        Advance(session, 30); // Includes a skipped attempt at capacity.
        Entity eaten = Apples(session)[0];
        session.Simulation.Agent.Position = eaten.Position;
        session.Simulation.Agent.Get<HungerState>().Level = 0.8;
        Advance(session, 1);
        Assert.DoesNotContain(eaten, session.Simulation.World.Entities);
        Assert.Single(Apples(session));
        Advance(session, 8);
        Assert.Single(Apples(session));
        Advance(session, 1);
        Assert.Equal(2, Apples(session).Length);
        Assert.Equal(3, session.FoodSpawner.TotalSpawned);
        Assert.DoesNotContain(eaten, session.Simulation.World.Entities);
    }

    [Fact]
    public void FoodSupplyContinuesThroughoutALongLifeWhenCapacityIsFreed()
    {
        var session = Create();
        for (int interval = 1; interval <= 100; interval++)
        {
            Advance(session, 200 - (interval == 1 ? 0 : 1));
            Entity apple = Assert.Single(Apples(session));
            Assert.Equal(interval, session.FoodSpawner.TotalSpawned);
            session.Simulation.Agent.Position = apple.Position;
            Advance(session, 1);
            Assert.Empty(Apples(session));
        }
        Assert.Equal(20001, session.Simulation.World.Step);
    }

    [Fact]
    public void LiveSnapshotsRemoveEatenApplesAndRetainFoodThroughoutLife()
    {
        using var live = new TrainingVisualizationSession(new ExperimentOptions
        {
            Learning = new() { WarmupSteps = 30000 }
        });
        var previous = live.Snapshot().World;
        int observedRemovals = 0;
        for (int tick = 0; tick < 20000; tick++)
        {
            live.AdvanceTraining(1);
            var current = live.Snapshot().World;
            observedRemovals += previous.Frame.Entities.Count(apple => apple.Components.ContainsKey("food.apple") && !current.Frame.Entities.Any(entity => entity.Id == apple.Id));
            Assert.InRange(current.Frame.Entities.Count(e => e.Components.ContainsKey("food.apple")), 0, 8);
            Assert.Equal(current.Frame.ModuleState["food"].Read<long>("spawned") - current.Frame.Entities.Count(e => e.Components.ContainsKey("food.apple")), observedRemovals);
            previous = current;
        }
        Assert.True(observedRemovals > 0, "The live run must exercise removal through snapshots.");
    }

    [Fact]
    public void ImpossibleDistanceSkipsSpawnWithoutHangingOrViolatingDistance()
    {
        var session = Create(new() { SpawnIntervalTicks = 1, MinSpawnDistanceFromAgent = 1000 });
        Advance(session, 100);
        Assert.Empty(Apples(session));
    }

    [Theory]
    [InlineData(0, 8, 10)]
    [InlineData(200, 0, 10)]
    [InlineData(200, 8, -1)]
    [InlineData(200, 8, double.NaN)]
    public void InvalidOptionsAreRejected(int interval, int capacity, double distance) =>
        Assert.Throws<ArgumentException>(() => Create(new() { SpawnIntervalTicks = interval, MaxApples = capacity, MinSpawnDistanceFromAgent = distance }));

    [Fact]
    public void LiveSnapshotsShowAllSpawnsAndKeepPreviousSnapshotsImmutable()
    {
        using var live = new TrainingVisualizationSession(new ExperimentOptions
        {
            FoodSpawner = new() { SpawnIntervalTicks = 5 },
            Learning = new() { WarmupSteps = 1000 }
        });
        var initial = live.Snapshot();
        live.AdvanceTraining(10);
        var later = live.Snapshot();
        Assert.DoesNotContain(initial.World.Frame.Entities, e => e.Components.ContainsKey("food.apple"));
        Assert.Equal(2, later.World.Frame.Entities.Count(e => e.Components.ContainsKey("food.apple")));
        Assert.Equal(2, later.World.Frame.ModuleState["food"].Read<long>("spawned"));
        Assert.All(later.World.Frame.Entities.Where(e => e.Components.ContainsKey("food.apple")), apple => Assert.InRange(apple.Position.X, 0, later.World.Width));
    }
}
