using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using ArtificialLife.Modules.Hunger;

namespace ArtificialLife.Application;

public sealed record AgentViewModel(double X, double Y, double BodyTemperature, double LocalTemperature, double Hunger);
public sealed record AppleViewModel(double X, double Y);

/// <summary>Immutable value collection so deterministic world snapshots retain value equality.</summary>
public sealed class AppleViews(IEnumerable<AppleViewModel> apples) : IReadOnlyList<AppleViewModel>, IEquatable<AppleViews>
{
    private readonly AppleViewModel[] items = apples.ToArray();
    public int Count => items.Length;
    public AppleViewModel this[int index] => items[index];
    public IEnumerator<AppleViewModel> GetEnumerator() => ((IEnumerable<AppleViewModel>)items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(AppleViews? other) => other is not null && items.SequenceEqual(other.items);
    public override bool Equals(object? obj) => obj is AppleViews other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var apple in items) hash.Add(apple);
        return hash.ToHashCode();
    }
}
public sealed record HeatSourceViewModel(double X, double Y, double Strength, double Radius, double Ambient);
public sealed record WorldViewModel(double Width, double Height, AgentViewModel Agent, HeatSourceViewModel HeatSource,
    double TargetTemperature, double ComfortHalfWidth, double Reward, long Step, double SimulationTime, string Mode,
    AppleViews Apples, long TotalApplesSpawned);

/// <summary>Human-speed adapter with neutral immutable snapshots; it contains no engine types.</summary>
public sealed class VisualizationSession : IDisposable
{
    private readonly SimulationSession session;
    private readonly DqnBrain brain;
    private long totalSteps;
    public double TimeStep => session.Options.World.TimeStep;

    public VisualizationSession(string checkpointDirectory)
    {
        ExperimentOptions options = Checkpoint.Read(checkpointDirectory).Options;
        session = new SimulationSession(options);
        brain = new DqnBrain(options.Network, options.Learning);
        try
        {
            Checkpoint.Load(checkpointDirectory, session, brain);
        }
        catch
        {
            brain.Dispose();
            throw;
        }
        session.Simulation.Reset(101);
    }

    public void Advance()
    {
        Simulation simulation = session.Simulation;
        simulation.Step(brain.Choose(simulation.Observe(), simulation.LegalActions()));
        // Визуализация непрерывна: границы тренировочных эпизодов не перезапускают мир.
        totalSteps++;
    }

    public WorldViewModel Snapshot()
    {
        return WorldSnapshots.Create(session, totalSteps, "Evaluation · ε = 0");
    }

    public void Dispose() => brain.Dispose();
}

internal static class WorldSnapshots
{
    internal static WorldViewModel Create(SimulationSession session, long steps, string mode)
    {
        Simulation simulation = session.Simulation;
        Position source = session.Fire.Position;
        HeatEmitter emitter = session.Fire.Get<HeatEmitter>();
        return new WorldViewModel(session.Options.World.Width, session.Options.World.Height,
            new AgentViewModel(simulation.Agent.Position.X, simulation.Agent.Position.Y, simulation.Agent.Get<ThermalBody>().Temperature,
                session.Temperature.EnvironmentAt(simulation.World, simulation.Agent.Position), simulation.Agent.Get<HungerState>().Level),
            new HeatSourceViewModel(source.X, source.Y, emitter.Strength(simulation.World.Time), emitter.Radius, session.Options.Temperature.Ambient),
            session.Options.Temperature.Target, session.Options.Temperature.ComfortHalfWidth, simulation.LastReward,
            steps, simulation.World.Time, mode,
            new AppleViews(simulation.World.Entities.Where(entity => entity.TryGet<Apple>(out _))
                .Select(entity => new AppleViewModel(entity.Position.X, entity.Position.Y))), session.FoodSpawner.TotalSpawned);
    }
}
