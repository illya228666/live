using System.Collections.ObjectModel;
using System.Text.Json;

namespace ArtificialLife.Core;

/// <summary>Host-owned values only. Never retain plugin objects, Types or delegates here.</summary>
public sealed class ComponentData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, JsonElement> Values { get; init; } = new(StringComparer.Ordinal);
    public T Read<T>(string key, T fallback = default!)
    {
        RequireValueType(typeof(T));
        return Values.TryGetValue(key, out var value) ? value.Deserialize<T>()! : fallback;
    }
    public void Write<T>(string key, T value)
    {
        RequireValueType(typeof(T));
        Values[key] = JsonSerializer.SerializeToElement(value);
    }
    internal static void RequireValueType(Type type)
    {
        if (type.IsArray) { RequireValueType(type.GetElementType()!); return; }
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(JsonElement)) return;
        throw new ArgumentException("Component values must be primitives, primitive arrays or JsonElement; plugin objects are not state.");
    }
    public ComponentData Copy() => new() { Version = Version, Values = Values.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
}

public sealed class RuntimeEntity
{
    public long Id { get; init; }
    public Position Position { get; set; }
    public Dictionary<string, ComponentData> Components { get; init; } = new(StringComparer.Ordinal);
    public ComponentData Require(string key) => Components.TryGetValue(key, out var data) ? data : throw new InvalidOperationException($"Entity {Id} has no {key}.");
    public RuntimeEntity Copy() => new() { Id = Id, Position = Position, Components = Components.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal) };
}

public sealed class RuntimeWorld
{
    public WorldOptions Options { get; init; } = new();
    [System.Text.Json.Serialization.JsonInclude]
    public long Age { get; internal set; }
    [System.Text.Json.Serialization.JsonInclude]
    public double Time { get; internal set; }
    [System.Text.Json.Serialization.JsonInclude]
    public long NextEntityId { get; internal set; } = 2;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAgent => Agent is not null;
    public RuntimeEntity Agent { get; init; } = new() { Id = 1 };
    public List<RuntimeEntity> Entities { get; init; } = [];
    public Dictionary<string, ComponentData> ModuleState { get; init; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<RuntimeEntity> AllEntities => (HasAgent ? new[] { Agent } : Array.Empty<RuntimeEntity>()).Concat(Entities);
    public RuntimeEntity CreateEntity(Position position)
    {
        var entity = new RuntimeEntity { Id = NextEntityId++, Position = position };
        Entities.Add(entity);
        return entity;
    }
    public bool Contains(Position p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= Options.Width && p.Y >= 0 && p.Y <= Options.Height;
    public ComponentData State(string module) => ModuleState.TryGetValue(module, out var state) ? state : ModuleState[module] = new();
    // SplitMix64: state travels with the world and can be copied without consuming the live RNG.
    public double Random(string stream)
    {
        var state = State("rng:" + stream);
        ulong value = unchecked(state.Read<ulong>("state", 1) + 0x9E3779B97F4A7C15UL);
        state.Write("state", value);
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return ((value ^ (value >> 31)) >> 11) * (1.0 / (1UL << 53));
    }
    public RuntimeWorld Copy() => new()
    {
        Options = Options, Age = Age, Time = Time, NextEntityId = NextEntityId, Agent = HasAgent ? Agent.Copy() : null!,
        Entities = Entities.Select(e => e.Copy()).ToList(),
        ModuleState = ModuleState.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal)
    };
}

public sealed record ComponentSnapshot(int Version, IReadOnlyDictionary<string, JsonElement> Values)
{
    public T Read<T>(string key, T fallback = default!)
    {
        ComponentData.RequireValueType(typeof(T));
        return Values.TryGetValue(key, out var value) ? value.Deserialize<T>()! : fallback;
    }
    public static ComponentSnapshot Capture(ComponentData data) => new(data.Version,
        new ReadOnlyDictionary<string, JsonElement>(data.Values.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal)));
}
public sealed record EntitySnapshot(long Id, Position Position, IReadOnlyDictionary<string, ComponentSnapshot> Components);
public sealed record RuntimeFrame(double Width, double Height, long Age, double Time, long Revision,
    EntitySnapshot Agent, IReadOnlyList<EntitySnapshot> Entities, IReadOnlyDictionary<string, ComponentSnapshot> ModuleState)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAgent => Agent is not null;
    public bool Equals(RuntimeFrame? other) => other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
    public override int GetHashCode() => HashCode.Combine(Width, Height, Age, Time, Revision, Agent?.Id);
}

public sealed record ModuleDependency(string Id, string Version);
public sealed record ModuleManifest(string Id, string Version, int ApiVersion, string Assembly, string EntryType)
{
    public bool SupportsWorldOnly { get; init; }
    public ModuleDependency[] Dependencies { get; init; } = [];
    public string[] Provides { get; init; } = [];
    public string[] Requires { get; init; } = [];
    public Dictionary<string, int> Schemas { get; init; } = new();
    public int StateSchemaVersion { get; init; } = 1;
    public JsonElement Settings { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? PresentationAssembly { get; init; }
    public string? PresentationType { get; init; }
    public int PresentationOrder { get; init; }
    public string LogicHash { get; init; } = "";
    public string? PresentationHash { get; init; }
    public Dictionary<string, string> LogicFiles { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PresentationFiles { get; init; } = new(StringComparer.Ordinal);
}
public sealed record PackageReference(string Id, string Version);
public sealed record ActivationRequest(string Id, PackageReference[] Packages);

public interface IRuntimeModule : IDisposable
{
    void Register(ModuleBuilder builder);
    /// <summary>Only candidate state is mutable. Called for new installs and upgrades, never to reset a life.</summary>
    void Migrate(RuntimeWorld candidate, ModuleManifest? previous);
    void IDisposable.Dispose() { }
}

public sealed record RuntimeSystem(string Key, Action<RuntimeWorld> Update, string[] After);
public sealed record ComponentRegistration(string Key, int Version, Func<RuntimeWorld, RuntimeEntity, bool> Applies,
    Action<RuntimeWorld, RuntimeEntity, ComponentData> Initialize);
public sealed record RuntimeAction(int Type, Func<RuntimeWorld, IEnumerable<ActionCandidate>> Candidates,
    Action<RuntimeWorld, ActionCandidate> Execute);
public sealed record MotivationInput(DriveState[] Drives, IReadOnlyDictionary<string, double> Signals);
public interface IMotivationPolicy
{
    double Evaluate(MotivationInput before, MotivationInput after);
}

public sealed class RegistrationList<T> : Collection<T>
{
    public bool Frozen { get; private set; }
    public void Freeze() => Frozen = true;
    private void Check() { if (Frozen) throw new InvalidOperationException("Module registrations are frozen until the next activation."); }
    protected override void InsertItem(int index, T item) { Check(); base.InsertItem(index, item); }
    protected override void SetItem(int index, T item) { Check(); base.SetItem(index, item); }
    protected override void RemoveItem(int index) { Check(); base.RemoveItem(index); }
    protected override void ClearItems() { Check(); base.ClearItems(); }
}

/// <summary>Registration delegates live only for the lifetime of a loaded module.</summary>
public sealed class ModuleBuilder(ModuleManifest manifest, TypeRegistry observations, TypeRegistry actions)
{
    public ModuleManifest Manifest { get; } = manifest;
    public TypeRegistry Observations { get; } = observations;
    public TypeRegistry Actions { get; } = actions;
    private bool frozen;
    private IMotivationPolicy? motivation;
    public RegistrationList<ComponentRegistration> Components { get; } = [];
    public RegistrationList<RuntimeSystem> Systems { get; } = [];
    public RegistrationList<Func<RuntimeWorld, IEnumerable<ObservationToken>>> Sensors { get; } = [];
    public RegistrationList<RuntimeAction> ActionProviders { get; } = [];
    public RegistrationList<Func<RuntimeWorld, ActionCandidate, bool>> ActionFilters { get; } = [];
    public RegistrationList<Func<RuntimeWorld, IEnumerable<DriveState>>> Drives { get; } = [];
    public RegistrationList<Func<RuntimeFrame, IEnumerable<KeyValuePair<string, double>>>> Metrics { get; } = [];
    public RegistrationList<DriveDescriptor> Descriptors { get; } = [];
    public RegistrationList<Func<RuntimeWorld, IEnumerable<KeyValuePair<string, double>>>> Signals { get; } = [];
    public IMotivationPolicy? Motivation
    {
        get => motivation;
        set { if (frozen) throw new InvalidOperationException("Motivation registration is frozen."); motivation = value; }
    }
    public void Freeze()
    {
        frozen = true;
        Metrics.Freeze(); Components.Freeze(); Systems.Freeze(); Sensors.Freeze(); ActionProviders.Freeze(); ActionFilters.Freeze();
        Drives.Freeze(); Descriptors.Freeze(); Signals.Freeze();
    }
    public RuntimeSettings Settings { get; } = new(manifest.Settings);
}

/// <summary>Parse only host-owned primitives; reflection serializer caches must never see collectible plugin types.</summary>
public sealed class RuntimeSettings(JsonElement data)
{
    private JsonElement? Find(string key) => data.ValueKind == JsonValueKind.Object
        ? data.EnumerateObject().Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault() : null;
    public double Number(string key, double fallback) => Find(key)?.GetDouble() ?? fallback;
    public int Integer(string key, int fallback) => Find(key)?.GetInt32() ?? fallback;
    public bool Boolean(string key, bool fallback = false) => Find(key)?.GetBoolean() ?? fallback;
    public string Text(string key, string fallback) => Find(key)?.GetString() ?? fallback;
}

public sealed record ControllerLimits(int ObservationTypes, int ActionTypes, int FeatureWidth)
{
    public int MinimumObservations { get; init; }
}
public sealed record ControllerStatus(long Steps, long Updates, double Epsilon, double Loss, int ReplayCount, int EligibleReplayCount);
public sealed record AgentTransition(ObservationToken[] State, ActionCandidate Action, double Reward,
    ObservationToken[] NextState, ActionCandidate[] NextActions, bool Terminal, long Revision);
public interface IAgentController : IDisposable
{
    ControllerLimits Limits { get; }
    ControllerStatus Status { get; }
    JsonElement InferenceState => JsonSerializer.SerializeToElement(new { });
    ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning);
    void Receive(AgentTransition transition);
    /// <summary>Nonthrowing notification of the committed append-only vocabulary; no learned rows may be remapped.</summary>
    void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions) { }
    /// <summary>Nonthrowing notification after validated preparation; retain learned parameters and optimizer state.</summary>
    void ConditionsChanged(long revision);
    void Export(string weightsPath);
    void Import(string weightsPath);
    void ImportInferenceState(JsonElement state) { }
    /// <summary>Fork independent inference memory/RNG. Read-only parameters may be shared while training is suspended.</summary>
    IAgentController CreateInferenceController() => throw new NotSupportedException("This controller does not implement isolated evaluation.");
}

/// <summary>Presentation preparation must not mutate simulation or expose a live world reference.</summary>
public interface IPreparedPresentation : IDisposable { void Commit(); }
public sealed record PresentationPackage(ModuleManifest Manifest, string Directory);
public interface IRuntimePresentationHost
{
    IPreparedPresentation Prepare(IReadOnlyList<PresentationPackage> packages, RuntimeFrame frame);
}
