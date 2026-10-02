using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using ArtificialLife.Modules.Vision;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class VisionTests
{
    [Theory]
    [InlineData(3, 4, 0, 5, 0.9272952180016122)]
    [InlineData(0, 5, Math.PI / 2, 5, 0)]
    [InlineData(5, 0, Math.PI / 2, 5, -Math.PI / 2)]
    [InlineData(-5, 0, 0, 5, Math.PI)]
    [InlineData(0, 0, 1, 0, 0)]
    public void GeometryUsesObserverRelativeDistanceAndBearing(double x, double y, double heading,
        double distance, double direction)
    {
        var world = new WorldState(new WorldOptions());
        var entity = new Entity { Position = new Position(10 + x, 20 + y) };
        entity.Set(new VisualAppearance(AppearanceType.Disc, 4));
        world.Entities.Add(entity);
        VisualPerception perception = Assert.Single(new DirectGeometryVisionBackend().Perceive(world,
            new VisionObserver(new Position(10, 20), heading)));
        Assert.Equal(distance, perception.Distance, 10);
        Assert.Equal(direction, perception.DirectionRadians, 10);
        Assert.Equal(2 * Math.Atan2(2, distance), perception.ApparentSize, 10);
        Assert.Equal(AppearanceType.Disc, perception.AppearanceType);
    }

    [Fact]
    public void TranslationAndRotationDoNotLeakWorldCoordinatesIntoTokens()
    {
        var world = new WorldState(new WorldOptions());
        var entity = new Entity { Position = new Position(13, 24) };
        entity.Set(new VisualAppearance(AppearanceType.Disc, 4));
        world.Entities.Add(entity);
        var agent = new AgentState { Position = new Position(10, 20) };
        var vision = new VisionModule(new DirectGeometryVisionBackend(), new TypeRegistry(256));
        ObservationToken before = Assert.Single(vision.Observe(world, agent));
        // Translate both, then rotate displacement and observer by a quarter turn.
        agent.Position = new Position(70, 80);
        entity.Position = new Position(66, 83);
        agent.OrientationRadians = Math.PI / 2;
        ObservationToken after = Assert.Single(vision.Observe(world, agent));
        Assert.Equal(before.Type, after.Type);
        Assert.Equal(before.Features, after.Features);
        Assert.Equal(new[] { "ApparentSize", "AppearanceType", "DirectionRadians", "Distance" },
            typeof(VisualPerception).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain(typeof(DqnBrain).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.StartsWith("ArtificialLife.Modules.", StringComparison.Ordinal));
    }

    [Fact]
    public void SwappableBackendProducesGenericNormalizedObservationTokens()
    {
        var registry = new TypeRegistry(256);
        IObservationProvider vision = new VisionModule(new FixedBackend(), registry);
        ObservationToken token = Assert.Single(vision.Observe(new WorldState(new WorldOptions()),
            new AgentState { Position = new Position(999, -999), OrientationRadians = 2 }));
        Assert.Equal(new[] { "vision.appearance.disc.v1", "vision.appearance.diamond.v1" }, registry.Keys);
        Assert.Equal(0, token.Type);
        Assert.Equal(4, token.Features.Length);
        Assert.Equal(0f, token.Features[0], 6);
        Assert.Equal(1f, token.Features[1], 6);
        Assert.Equal(0.8f, token.Features[2], 6);
        Assert.Equal(0.25f, token.Features[3], 6);
        Assert.All(token.Features, value => Assert.InRange(value, -1, 1));
    }

    [Fact]
    public void FireIsOneSharedEntityWithIndependentHeatAndAppearanceComponents()
    {
        var session = new SimulationSession(new ExperimentOptions());
        WorldState world = session.Simulation.World;
        Assert.Contains(session.Fire, world.Entities);
        world.Entities.RemoveAll(entity => entity != session.Fire);
        HeatEmitter emitter = session.Fire.Get<HeatEmitter>();
        Assert.Equal(AppearanceType.Disc, session.Fire.Get<VisualAppearance>().Type);
        Assert.Equal(session.Options.Temperature.Ambient + emitter.Strength(world.Time),
            session.Temperature.EnvironmentAt(world, session.Fire.Position), 10);
        Assert.Single(session.Vision.Observe(world, session.Simulation.Agent));
        Assert.Equal(8, session.Simulation.Observe().Length);

        session.Fire.Position = new Position(0, 0);
        Assert.Equal(session.Options.Temperature.Ambient + emitter.Strength(world.Time),
            session.Temperature.EnvironmentAt(world, new Position(0, 0)), 10);
        VisualPerception sighting = Assert.Single(new DirectGeometryVisionBackend().Perceive(world,
            new VisionObserver(new Position(3, 4), 0)));
        Assert.Equal(5, sighting.Distance);
        session.Simulation.Reset(42);
        Assert.Contains(session.Fire, world.Entities);

        // The same silhouette can be cold: visual type carries no heat semantics.
        world.Entities.Clear();
        var coldDisc = new Entity { Position = session.Fire.Position };
        coldDisc.Set(session.Fire.Get<VisualAppearance>());
        world.Entities.Add(coldDisc);
        Assert.Single(session.Vision.Observe(world, session.Simulation.Agent));
        Assert.Equal(session.Options.Temperature.Ambient, session.Temperature.EnvironmentAt(world, coldDisc.Position));

        var invisibleHeater = new Entity { Position = coldDisc.Position };
        invisibleHeater.Set(emitter);
        world.Entities.Add(invisibleHeater);
        Assert.Single(session.Vision.Observe(world, session.Simulation.Agent));
        Assert.Equal(session.Options.Temperature.Ambient + emitter.Strength(world.Time),
            session.Temperature.EnvironmentAt(world, coldDisc.Position), 10);
        world.Entities.Add(session.Fire);
        Assert.Equal(2, session.Vision.Observe(world, session.Simulation.Agent).Count());
        Assert.Equal(session.Options.Temperature.Ambient + 2 * emitter.Strength(world.Time),
            session.Temperature.EnvironmentAt(world, coldDisc.Position), 10);
    }

    [Fact]
    public void ObjectsWithoutAppearanceProduceNoVisualTokens()
    {
        var world = new WorldState(new WorldOptions());
        world.Entities.Add(new Entity());
        var vision = new VisionModule(new DirectGeometryVisionBackend(), new TypeRegistry(256));
        Assert.Empty(vision.Observe(world, new AgentState()));
    }

    private sealed class FixedBackend : IVisionBackend
    {
        public IEnumerable<VisualPerception> Perceive(WorldState world, VisionObserver observer) =>
            [new(AppearanceType.Disc, Math.PI / 2, 4, Math.PI / 4)];
    }
}
