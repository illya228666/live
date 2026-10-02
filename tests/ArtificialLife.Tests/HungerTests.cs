using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Hunger;
using ArtificialLife.Modules.Temperature;
using ArtificialLife.Modules.Vision;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class HungerTests
{
    [Fact]
    public void HungerStartsAtZeroAndGrowsLinearlyWithSimulationTime()
    {
        var session = new SimulationSession(new ExperimentOptions());
        HungerState hunger = session.Simulation.Agent.Get<HungerState>();
        session.Simulation.World.Entities.Clear();
        Assert.Equal(0, hunger.Level);
        session.Hunger.Update(session.Simulation.World, session.Simulation.Agent, 100);
        Assert.Equal(0.1, hunger.Level, 12);
        session.Hunger.Update(session.Simulation.World, session.Simulation.Agent, 200);
        Assert.Equal(0.3, hunger.Level, 12);
        Assert.Equal(0.3f, Assert.Single(session.Hunger.Observe(session.Simulation.World, session.Simulation.Agent)).Features[0]);
    }

    [Fact]
    public void HungerAlwaysStaysWithinZeroAndOne()
    {
        var session = new SimulationSession(new ExperimentOptions());
        HungerState hunger = session.Simulation.Agent.Get<HungerState>();
        session.Simulation.World.Entities.Clear();
        session.Hunger.Update(session.Simulation.World, session.Simulation.Agent, 100000);
        Assert.Equal(1, hunger.Level);
        session.Hunger.Update(session.Simulation.World, session.Simulation.Agent, 1);
        Assert.Equal(1, hunger.Level);
        hunger.Level = -1;
        Assert.Equal(0, hunger.Level);
        hunger.Level = 2;
        Assert.Equal(1, hunger.Level);
        Assert.Throws<ArgumentOutOfRangeException>(() => hunger.Level = double.NaN);
    }

    [Theory]
    [InlineData(0.7, 0, 0.5, true)]
    [InlineData(0.7, 2, 0.5, true)]
    [InlineData(0.1, 0, 0, true)]
    [InlineData(0.7, 2.00001, 0.7, false)]
    public void ContactEatingSubtractsPercentagePointsAndRemovesOnlyReachableApple(
        double initialHunger, double distance, double expectedHunger, bool eaten)
    {
        var session = new SimulationSession(new ExperimentOptions());
        WorldState world = session.Simulation.World;
        Entity apple = world.Entities.First(entity => entity.TryGet<Nutrition>(out _));
        world.Entities.RemoveAll(entity => entity != apple);
        session.Simulation.Agent.Position = new Position(apple.Position.X + distance, apple.Position.Y);
        session.Simulation.Agent.Get<HungerState>().Level = initialHunger;
        session.Hunger.Update(world, session.Simulation.Agent, 0); // Isolate feeding from elapsed hunger growth.
        Assert.Equal(expectedHunger, session.Simulation.Agent.Get<HungerState>().Level, 12);
        Assert.Equal(!eaten, world.Entities.Contains(apple));
        Assert.Equal(eaten ? 0 : 1, session.Vision.Observe(world, session.Simulation.Agent).Count());
    }

    [Fact]
    public void FeedingUsesNutritionWithoutRequiringAnAppleOrVisualComponent()
    {
        var session = new SimulationSession(new ExperimentOptions());
        WorldState world = session.Simulation.World;
        world.Entities.Clear();
        var food = new Entity { Position = session.Simulation.Agent.Position };
        food.Set(new Nutrition(0.35));
        world.Entities.Add(food);
        session.Simulation.Agent.Get<HungerState>().Level = 0.8;
        session.Hunger.Update(world, session.Simulation.Agent, 0);
        Assert.Equal(0.45, session.Simulation.Agent.Get<HungerState>().Level, 12);
        Assert.Empty(world.Entities);
    }

    [Fact]
    public void HungerAndTemperatureUseOnlyTheCommonDriveRewardIncludingFeeding()
    {
        var options = new ExperimentOptions();
        var session = new SimulationSession(options);
        Simulation simulation = session.Simulation;
        Entity apple = simulation.World.Entities.First(entity => entity.TryGet<Nutrition>(out _));
        simulation.Agent.Position = apple.Position;
        simulation.Agent.Get<ThermalBody>().Temperature = options.Temperature.Target;
        simulation.Agent.Get<HungerState>().Level = 0.8;
        var rewards = new RewardAggregator(options.Reward);
        DriveState hunger = Assert.Single(session.Hunger.GetDrives(simulation.Agent));
        Assert.Equal(new DriveState("hunger", 0.8, 0, 1, 1), hunger);
        double before = rewards.Error([.. session.Temperature.GetDrives(simulation.Agent), hunger]);
        Assert.Equal(0.4, before, 12);
        Assert.True(rewards.Reward(before, before) < rewards.Reward(0, 0));

        ActionCandidate stay = simulation.LegalActions().Single(action => action.Parameters.SequenceEqual(new float[] { 0, 0 }));
        StepResult result = simulation.Step(stay);
        double after = rewards.Error([.. session.Temperature.GetDrives(simulation.Agent), .. session.Hunger.GetDrives(simulation.Agent)]);
        Assert.Equal(0.8 + options.Hunger.GrowthPerSecond * options.World.TimeStep - 0.20,
            simulation.Agent.Get<HungerState>().Level, 12);
        Assert.DoesNotContain(apple, simulation.World.Entities);
        Assert.Equal(rewards.Reward(before, after), result.Reward, 12); // No separate eating bonus.
    }

    [Fact]
    public void VisionDistinguishesNeutralAppearancesAndBrainAcceptsOnlyGenericTokens()
    {
        var session = new SimulationSession(new ExperimentOptions());
        Entity[] apples = session.Simulation.World.Entities.Where(entity => entity.TryGet<Nutrition>(out _)).ToArray();
        Assert.Equal(4, apples.Length);
        Assert.All(apples, apple =>
        {
            Assert.Equal(0.20, apple.Get<Nutrition>().HungerReduction);
            Assert.Equal(AppearanceType.Diamond, apple.Get<VisualAppearance>().Type);
            Assert.False(apple.TryGet<HeatEmitter>(out _));
        });
        Assert.Equal(AppearanceType.Disc, session.Fire.Get<VisualAppearance>().Type);
        ObservationToken[] sightings = session.Vision.Observe(session.Simulation.World, session.Simulation.Agent).ToArray();
        Assert.Single(sightings, token => token.Type == session.Observations.Register("vision.appearance.disc.v1"));
        Assert.Equal(4, sightings.Count(token => token.Type == session.Observations.Register("vision.appearance.diamond.v1")));
        Assert.DoesNotContain(session.Observations.Keys, key => key.Contains("apple") || key.Contains("food"));

        ObservationToken[] tokens = session.Simulation.Observe();
        Assert.Equal(12, tokens.Length);
        Assert.All(tokens, token => Assert.All(token.Features, value => Assert.InRange(value, -1, 1)));
        using var brain = new DqnBrain(session.Options.Network, session.Options.Learning);
        Assert.Equal(9, brain.Scores(tokens, session.Simulation.LegalActions()).Length);
        Assert.DoesNotContain(typeof(DqnBrain).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.StartsWith("ArtificialLife.Modules.", StringComparison.Ordinal));
        session.Simulation.Agent.Position = apples[0].Position;
        session.Hunger.Update(session.Simulation.World, session.Simulation.Agent, 0);
        Assert.Equal(9, brain.Scores(session.Simulation.Observe(), session.Simulation.LegalActions()).Length);
    }

    [Fact]
    public void ContinuousLifeDoesNotRespawnFoodButExplicitResetStartsANewWorld()
    {
        var session = new SimulationSession(new ExperimentOptions { World = new() { EpisodeSteps = 2 } },
            SimulationLifecycle.Continuous);
        Entity apple = session.Simulation.World.Entities.First(entity => entity.TryGet<Nutrition>(out _));
        session.Simulation.Agent.Position = apple.Position;
        ActionCandidate stay = session.Simulation.LegalActions().Single(action => action.Parameters.SequenceEqual(new float[] { 0, 0 }));
        for (int step = 0; step < 5; step++) Assert.False(session.Simulation.Step(stay).Terminal);
        Assert.DoesNotContain(apple, session.Simulation.World.Entities);
        Assert.Equal(3, session.Simulation.World.Entities.Count(entity => entity.TryGet<Nutrition>(out _)));
        session.Simulation.Reset(42);
        Assert.Equal(0, session.Simulation.Agent.Get<HungerState>().Level);
        Assert.Equal(4, session.Simulation.World.Entities.Count(entity => entity.TryGet<Nutrition>(out _)));
    }
}
