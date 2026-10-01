using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData(0, 0, 4)]
    [InlineData(100, 100, 4)]
    [InlineData(0, 50, 6)]
    [InlineData(50, 50, 9)]
    public void MovementOnlyOffersLegalCandidates(double x, double y, int expected)
    {
        var session = new SimulationSession(new ExperimentOptions());
        session.Simulation.Agent.Position = new Position(x, y);
        ActionCandidate[] actions = session.Simulation.LegalActions();
        Assert.Equal(expected, actions.Length);
        Assert.Contains(actions, candidate => candidate.Parameters.SequenceEqual(new float[] { 0, 0 }));
        foreach (ActionCandidate candidate in actions)
        {
            session.Simulation.Agent.Position = new Position(x, y);
            session.Simulation.Step(candidate);
            Assert.True(session.Simulation.World.Contains(session.Simulation.Agent.Position));
        }
    }

    [Fact]
    public void IllegalActionCannotChangeWorld()
    {
        var session = new SimulationSession(new ExperimentOptions());
        Position before = session.Simulation.Agent.Position;
        Assert.Throws<InvalidOperationException>(() => session.Simulation.Step(new ActionCandidate(0, [500, 0])));
        Assert.Equal(before, session.Simulation.Agent.Position);
        Assert.Equal(0, session.Simulation.World.Step);
    }

    [Fact]
    public void SeedAndActionsReproduceThermalTrajectory()
    {
        var first = new SimulationSession(new ExperimentOptions());
        var second = new SimulationSession(new ExperimentOptions());
        first.Simulation.Reset(123);
        second.Simulation.Reset(123);
        var random = new Random(5);
        for (int step = 0; step < 500; step++)
        {
            ActionCandidate[] choices = first.Simulation.LegalActions();
            ActionCandidate action = choices[random.Next(choices.Length)];
            StepResult a = first.Simulation.Step(action);
            StepResult b = second.Simulation.Step(action);
            Assert.Equal(first.Simulation.Agent.Position, second.Simulation.Agent.Position);
            Assert.Equal(first.Simulation.Agent.Get<ThermalBody>().Temperature, second.Simulation.Agent.Get<ThermalBody>().Temperature);
            Assert.Equal(a.Reward, b.Reward);
        }
        second.Simulation.Reset(124);
        Assert.NotEqual(first.Simulation.Agent.Position, second.Simulation.Agent.Position);
    }

    [Fact]
    public void RegistryRetainsIdentityAndRejectsOverflow()
    {
        foreach (var registry in new[] { new TypeRegistry(2), new TypeRegistry(2) })
        {
            Assert.Equal(0, registry.Register("first"));
            Assert.Equal(1, registry.Register("second"));
            Assert.Equal(0, registry.Register("first"));
            Assert.Throws<InvalidOperationException>(() => registry.Register("third"));
            Assert.Equal(new[] { "first", "second" }, registry.Keys);
        }
    }

    [Fact]
    public void RewardIsBoundedAndComfortHasPersistentSignal()
    {
        var reward = new RewardAggregator(new RewardOptions());
        Assert.Equal(1, reward.Reward(0, 0));
        Assert.True(reward.Reward(1, 0.5) > reward.Reward(1, 1));
        Assert.InRange(reward.Reward(10000, 0), -1, 1);
        Assert.InRange(reward.Reward(0, 10000), -1, 1);
        Assert.Equal(reward.Error([new DriveState("a", 10, 0, 10, 1)]), reward.Error([new DriveState("a", 100, 0, 100, 1)]));
        Assert.Throws<InvalidOperationException>(() => reward.Error([new DriveState("a", double.NaN, 0, 1, 1)]));
    }

    [Fact]
    public void ReplayIsManagedBoundedAndOwnsFeatureCopies()
    {
        var replay = new ReplayBuffer(2);
        var experience = new Experience([new ObservationToken(0, [0.25f])], new ActionCandidate(0, [1, 0]), 0,
            [new ObservationToken(0, [0.5f])], [new ActionCandidate(0, [0, 0])], false);
        replay.Add(experience);
        experience.State[0].Features[0] = -1;
        Assert.Equal(0.25f, replay.Sample(1, new Random(1))[0].State[0].Features[0]);
        replay.Add(experience);
        replay.Add(experience);
        Assert.Equal(2, replay.Count);
    }

    [Fact]
    public void DomainProjectsNeverReferenceGodot()
    {
        foreach (var assembly in new[] { typeof(Simulation).Assembly, typeof(DqnBrain).Assembly, typeof(TemperatureModule).Assembly, typeof(WorldViewModel).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.Contains("Godot", StringComparison.Ordinal));
        }
    }
}
