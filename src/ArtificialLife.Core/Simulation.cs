namespace ArtificialLife.Core;

public enum SimulationLifecycle { Episodic, Continuous }

public sealed record StepResult(ObservationToken[] Observations, ActionCandidate[] Actions, double Reward, bool Terminal);

/// <summary>Presentation-independent orchestrator. Modules own their state and physical equations.</summary>
public sealed class Simulation
{
    private readonly IWorldSystem[] systems;
    private readonly IObservationProvider[] observations;
    private readonly IActionProvider[] actions;
    private readonly IDriveProvider[] drives;
    private readonly RewardAggregator rewards;
    private readonly Entity[] initialEntities;

    public WorldState World { get; }
    public SimulationLifecycle Lifecycle { get; }
    public AgentState Agent { get; } = new();
    public double LastReward { get; private set; }

    public Simulation(WorldOptions options, RewardOptions rewardOptions, IEnumerable<IWorldSystem> systems,
        IEnumerable<IObservationProvider> observations, IEnumerable<IActionProvider> actions, IEnumerable<IDriveProvider> drives,
        SimulationLifecycle lifecycle = SimulationLifecycle.Episodic, IEnumerable<Entity>? entities = null)
    {
        options.Validate();
        if (!Enum.IsDefined(lifecycle)) throw new ArgumentOutOfRangeException(nameof(lifecycle));
        Lifecycle = lifecycle;
        World = new WorldState(options);
        initialEntities = entities?.ToArray() ?? [];
        this.systems = systems.ToArray();
        this.observations = observations.ToArray();
        this.actions = actions.ToArray();
        this.drives = drives.ToArray();
        rewards = new RewardAggregator(rewardOptions);
    }

    public void Reset(int seed)
    {
        var random = new Random(seed);
        World.Time = random.NextDouble() * 600;
        World.Step = 0;
        LastReward = 0;
        World.Entities.Clear();
        World.Entities.AddRange(initialEntities);
        Agent.Position = new Position(random.NextDouble() * World.Options.Width, random.NextDouble() * World.Options.Height);
        Agent.OrientationRadians = 0;
        foreach (IWorldSystem system in systems)
        {
            system.Reset(World, Agent, random);
        }
    }

    public ObservationToken[] Observe() => observations.SelectMany(provider => provider.Observe(World, Agent)).ToArray();
    public ActionCandidate[] LegalActions() => actions.SelectMany(provider => provider.GetLegalActions(World, Agent)).ToArray();

    public StepResult Step(ActionCandidate action)
    {
        if (!LegalActions().Any(candidate => candidate.Type == action.Type && candidate.Parameters.SequenceEqual(action.Parameters)))
        {
            throw new InvalidOperationException("Candidate is not currently legal.");
        }
        double previousError = rewards.Error(drives.SelectMany(provider => provider.GetDrives(Agent)));
        actions.Single(provider => provider.Handles(action.Type)).Execute(World, Agent, action);
        World.Time += World.Options.TimeStep;
        World.Step++;
        foreach (IWorldSystem system in systems)
        {
            system.Update(World, Agent, World.Options.TimeStep);
        }
        double error = rewards.Error(drives.SelectMany(provider => provider.GetDrives(Agent)));
        LastReward = rewards.Reward(previousError, error);
        return new StepResult(Observe(), LegalActions(), LastReward,
            Lifecycle == SimulationLifecycle.Episodic && World.Step >= World.Options.EpisodeSteps);
    }
}
