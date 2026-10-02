using ArtificialLife.Application;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class TemperatureTests
{
    [Fact]
    public void HeatFieldDecaysSmoothlyTowardAmbient()
    {
        var session = new SimulationSession(new ExperimentOptions());
        WorldState world = session.Simulation.World;
        Position source = session.Fire.Position;
        double center = session.Temperature.EnvironmentAt(world, source);
        double nearby = session.Temperature.EnvironmentAt(world, new Position(source.X + 10, source.Y));
        double far = session.Temperature.EnvironmentAt(world, new Position(source.X + 1000, source.Y));
        Assert.True(center > nearby);
        Assert.True(nearby > far);
        Assert.Equal(session.Options.Temperature.Ambient, far, 6);
        Assert.InRange(Math.Abs(center - session.Temperature.EnvironmentAt(world, new Position(source.X + 0.001, source.Y))), 0, 0.001);
    }

    [Fact]
    public void HeatFieldVariesPeriodicallyWithoutTeleporting()
    {
        var session = new SimulationSession(new ExperimentOptions());
        double period = session.Options.Temperature.OscillationPeriod;
        HeatEmitter emitter = session.Fire.Get<HeatEmitter>();
        Assert.NotEqual(emitter.Strength(0), emitter.Strength(period / 4));
        Assert.Equal(emitter.Strength(0), emitter.Strength(period), 9);
        Assert.InRange(Math.Abs(emitter.Strength(1) - emitter.Strength(0)), 0, 1);
    }

    [Fact]
    public void BodyHasInertiaAndMatchesAnalyticEquation()
    {
        var session = new SimulationSession(new ExperimentOptions());
        ThermalBody body = session.Simulation.Agent.Get<ThermalBody>();
        body.Temperature = 36.6;
        double local = session.Temperature.EnvironmentAt(session.Simulation.World, session.Simulation.Agent.Position);
        double equilibrium = local + session.Options.Temperature.HeatProduction / session.Options.Temperature.HeatTransfer;
        double expected = equilibrium + (36.6 - equilibrium) * Math.Exp(-0.12);
        session.Temperature.Update(session.Simulation.World, session.Simulation.Agent, 1);
        Assert.Equal(expected, body.Temperature, 10);
        Assert.NotEqual(local, body.Temperature);
    }

    [Theory]
    [InlineData(50, 50, true)]
    [InlineData(0, 0, false)]
    public void CloseOverheatsAndFarAwayCools(double x, double y, bool shouldHeat)
    {
        var session = new SimulationSession(new ExperimentOptions());
        session.Simulation.Agent.Position = new Position(x, y);
        session.Simulation.Agent.Get<ThermalBody>().Temperature = 36.6;
        session.Temperature.Update(session.Simulation.World, session.Simulation.Agent, 10);
        double body = session.Simulation.Agent.Get<ThermalBody>().Temperature;
        Assert.True(shouldHeat ? body > 36.6 : body < 36.6);
    }

    [Fact]
    public void ThermalTokensAreLocalNormalizedAndDriveIsModuleOwned()
    {
        var session = new SimulationSession(new ExperimentOptions());
        ObservationToken[] tokens = session.Temperature.Observe(session.Simulation.World, session.Simulation.Agent).ToArray();
        int[] thermalTypes = [session.Observations.Register("temperature.body.v1"), session.Observations.Register("temperature.sensor.v1")];
        Assert.Equal(6, tokens.Length);
        Assert.Equal(thermalTypes, tokens.Select(token => token.Type).Distinct());
        Assert.Single(tokens[0].Features);
        Assert.All(tokens, token => Assert.All(token.Features, value => Assert.InRange(value, -1, 1)));
        Assert.Equal(new[] { 0f, -1f }, tokens[2].Features.Skip(1));
        DriveState drive = Assert.Single(session.Temperature.GetDrives(session.Simulation.Agent));
        Assert.Equal(36.6, drive.Target);
        Assert.Equal(session.Simulation.Agent.Get<ThermalBody>().Temperature, drive.Current);
    }
}
