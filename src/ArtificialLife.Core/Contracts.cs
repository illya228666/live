namespace ArtificialLife.Core;

/// <summary>A world position in simulation units, independent of rendering.</summary>
public readonly record struct Position(double X, double Y)
{
    public double DistanceTo(Position other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

/// <summary>Module-owned state attached to one entity.</summary>
public sealed class AgentState
{
    private readonly Dictionary<Type, object> components = new();
    public Position Position { get; set; }

    public void Set<T>(T component) where T : class => components[typeof(T)] = component;
    public T Get<T>() where T : class => (T)components[typeof(T)];
}

/// <summary>Only spatial and temporal information shared by all systems.</summary>
public sealed class WorldState(WorldOptions options)
{
    public WorldOptions Options { get; } = options;
    public double Time { get; internal set; }
    public long Step { get; internal set; }
    public bool Contains(Position point) => point.X >= 0 && point.X <= Options.Width && point.Y >= 0 && point.Y <= Options.Height;
}

/// <summary>Features are normalized and padded by the brain, never semantically interpreted there.</summary>
public sealed record ObservationToken(int Type, float[] Features);

/// <summary>A legal capability instance. Parameters describe this candidate, not a fixed output neuron.</summary>
public sealed record ActionCandidate(int Type, float[] Parameters);

/// <summary>A bounded, scale-normalized homeostatic need; reward belongs to the core.</summary>
public sealed record DriveState(string Key, double Current, double Target, double Scale, double Weight);

/// <summary>Updates module-owned state after an action.</summary>
public interface IWorldSystem
{
    void Reset(WorldState world, AgentState agent, Random random);
    void Update(WorldState world, AgentState agent, double elapsedSeconds);
}

/// <summary>Produces sensory tokens from a narrow spatial/entity context.</summary>
public interface IObservationProvider
{
    IEnumerable<ObservationToken> Observe(WorldState world, AgentState agent);
}

/// <summary>Enumerates only executable candidates and handles its own registered types.</summary>
public interface IActionProvider
{
    IEnumerable<ActionCandidate> GetLegalActions(WorldState world, AgentState agent);
    bool Handles(int actionType);
    void Execute(WorldState world, AgentState agent, ActionCandidate action);
}

/// <summary>Reports internal needs rather than arbitrary rewards.</summary>
public interface IDriveProvider
{
    IEnumerable<DriveState> GetDrives(AgentState agent);
}

/// <summary>Stable logical keys occupy preallocated network slots. Order is checkpoint metadata.</summary>
public sealed class TypeRegistry(int capacity)
{
    private readonly List<string> keys = [];
    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public IReadOnlyList<string> Keys => keys.AsReadOnly();

    public int Register(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        int existing = keys.IndexOf(key);
        if (existing >= 0)
        {
            return existing;
        }
        if (keys.Count >= Capacity)
        {
            throw new InvalidOperationException($"Type capacity {Capacity} exhausted; a new model is required.");
        }
        keys.Add(key);
        return keys.Count - 1;
    }
}
