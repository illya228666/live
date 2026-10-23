using System.Collections.ObjectModel;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record RuntimeOptions
{
    public WorldOptions World { get; init; } = new();
    public ArtificialLife.Brain.NetworkOptions Network { get; init; } = new();
    public ArtificialLife.Brain.LearningOptions Learning { get; init; } = new();
    public PackageReference[] Packages { get; init; } = [];
    public string PackageRoot { get; init; } = "artifacts/packages";
    public string Inbox { get; init; } = "artifacts/activation";
    public Dictionary<string, JsonElement> Settings { get; init; } = new(StringComparer.Ordinal);
    public static RuntimeOptions Load(string path) => JsonSerializer.Deserialize<RuntimeOptions>(File.ReadAllText(path), PackageCatalog.Json)
        ?? throw new InvalidDataException("Empty runtime configuration.");
}

/// <summary>One synchronous owner. Activation is a transaction; life never resets at episode boundaries.</summary>
public sealed class RuntimeSession : IDisposable
{
    private List<LoadedRuntimeModule> modules = [];
    private RuntimeSystem[] systems = [];
    private HashSet<string> componentKeys = new(StringComparer.Ordinal);
    private TypeRegistry observations;
    private TypeRegistry actions;
    private RuntimeWorld world;
    private readonly PackageCatalog catalog;
    private readonly Queue<ActivationRequest> pending = new();
    private readonly List<WeakReference> retired = [];
    private bool disposed;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    public IAgentController Controller { get; }
    public IRuntimePresentationHost? PresentationHost { get; set; }
    public long Revision { get; private set; }
    public string? Fault { get; private set; }
    public double LastReward { get; private set; }
    public IReadOnlyList<string> ObservationKeys => observations.Keys;
    public IReadOnlyList<string> ActionKeys => actions.Keys;
    public IReadOnlyList<ModuleManifest> Packages => modules.Select(m => CopyManifest(m.Manifest)).ToArray();
    public IReadOnlyList<WeakReference> RetiredContexts => retired.AsReadOnly();
    public RuntimeOptions Options { get; }
    public ActivationOutcome? LastActivation { get; private set; }
    public Action<AgentTransition>? TrajectorySink { get; set; }

    public RuntimeSession(RuntimeOptions options, IAgentController controller, PackageCatalog? catalog = null,
        IEnumerable<string>? observationKeys = null, IEnumerable<string>? actionKeys = null, IRuntimePresentationHost? presentationHost = null)
    {
        Options = options;
        options.World.Validate();
        Controller = controller;
        PresentationHost = presentationHost;
        this.catalog = catalog ?? new(options.PackageRoot);
        observations = new(controller.Limits.ObservationTypes);
        actions = new(controller.Limits.ActionTypes);
        foreach (string key in observationKeys ?? []) observations.Register(key);
        foreach (string key in actionKeys ?? []) actions.Register(key);
        world = new() { Options = options.World };
        world.State("rng:initial").Write("state", unchecked((ulong)options.Learning.Seed));
        world.Agent.Position = new(world.Random("initial") * options.World.Width, world.Random("initial") * options.World.Height);
        world.Time = world.Random("initial") * 600;
        var outcome = Activate(new("initial", options.Packages));
        if (!outcome.Accepted) { Dispose(); throw new InvalidDataException(outcome.Message); }
    }

    public void Enqueue(ActivationRequest request) { CheckOwner(); ObjectDisposedException.ThrowIf(disposed, this); pending.Enqueue(request); }
    public void Poll(ActivationInbox inbox)
    {
        foreach (var item in inbox.ReadReady())
        {
            var outcome = item.Request is null ? new ActivationOutcome(Path.GetFileName(item.Path), false, item.Error!, Revision) : Activate(item.Request);
            ActivationInbox.Complete(item.Path, outcome);
        }
    }

    public ActivationOutcome Activate(ActivationRequest request)
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Fault is not null) return LastActivation = new(request.Id, false, "Runtime is faulted; no further changes are applied.", Revision);
        var loaded = new List<LoadedRuntimeModule>();
        List<LoadedRuntimeModule>? replaced = null;
        bool committed = false;
        IPreparedPresentation? prepared = null;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Id) || request.Packages.Length == 0 || request.Packages.Select(p => p.Id).Distinct().Count() != request.Packages.Length)
                throw new InvalidDataException("Activation identity is empty or packages are duplicated.");
            var desired = modules.ToDictionary(m => m.Manifest.Id, m => m.Manifest, StringComparer.Ordinal);
            foreach (var reference in request.Packages)
            {
                var manifest = catalog.Read(reference);
                var active = modules.FirstOrDefault(m => m.Manifest.Id == reference.Id)?.Manifest;
                if (active is null && Options.Settings.TryGetValue(reference.Id, out var settings)) manifest = manifest with { Settings = settings.Clone() };
                else if (active?.Version == reference.Version) manifest = manifest with { Settings = active.Settings.Clone() };
                desired[reference.Id] = manifest;
            }
            ValidateDependencies(desired);
            var nextObservations = CopyRegistry(observations);
            var nextActions = CopyRegistry(actions);
            RuntimeWorld candidate = world.Copy();
            bool changed = modules.Count == 0;
            foreach (var manifest in OrderPackages(desired))
            {
                var previous = modules.FirstOrDefault(m => m.Manifest.Id == manifest.Id);
                var old = previous?.Manifest;
                _ = catalog.Read(new(manifest.Id, manifest.Version)); // also revalidate unchanged dependencies before reloading them
                if (old?.Version == manifest.Version && previous!.SourceManifestHash != PackageCatalog.ManifestHash(catalog.DirectoryFor(new(manifest.Id, manifest.Version))))
                    throw new InvalidDataException($"Published package {manifest.Id}@{manifest.Version} changed in place; publish a new version.");
                var item = new LoadedRuntimeModule(catalog, manifest, nextObservations, nextActions);
                loaded.Add(item);
                bool logicChanged = old is null || !SameLogic(old, manifest);
                if (logicChanged)
                {
                    changed = true;
                    if (!candidate.ModuleState.ContainsKey("rng:" + manifest.Id))
                    {
                        ulong seed = unchecked((ulong)Options.Learning.Seed);
                        foreach (char c in manifest.Id) seed = unchecked(seed * 1099511628211UL ^ c);
                        candidate.State("rng:" + manifest.Id).Write("state", seed);
                    }
                    item.Module.Migrate(candidate, old);
                }
                if (manifest.StateSchemaVersion < 1 || candidate.State(manifest.Id).Version != manifest.StateSchemaVersion)
                    throw new InvalidDataException($"Unmigrated module state: {manifest.Id}.");
            }
            var registrations = loaded.SelectMany(m => m.Builder.Components).ToArray();
            foreach (var item in loaded) item.Builder.Freeze();
            nextObservations.Freeze();
            nextActions.Freeze();
            if (registrations.Select(c => c.Key).Distinct().Count() != registrations.Length) throw new InvalidDataException("Duplicate component registration.");
            foreach (var component in registrations)
            {
                if (string.IsNullOrWhiteSpace(component.Key) || component.Version < 1) throw new InvalidDataException("Component keys and versions must be valid.");
                var owner = loaded.Single(m => m.Builder.Components.Contains(component));
                if (!owner.Manifest.Schemas.TryGetValue(component.Key, out int version) || version != component.Version)
                    throw new InvalidDataException($"Manifest schema mismatch: {component.Key}.");
            }
            foreach (var item in loaded)
                if (item.Manifest.Schemas.Count != item.Builder.Components.Count) throw new InvalidDataException($"Unregistered schema in {item.Manifest.Id}.");
            EnsureComponents(candidate, registrations);
            var knownComponents = registrations.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            if (candidate.AllEntities.Any(e => e.Components.Keys.Any(key => !knownComponents.Contains(key))))
                throw new InvalidDataException("World contains an undeclared component.");
            if (candidate.Age != world.Age || candidate.Time != world.Time || candidate.Agent.Id != world.Agent.Id ||
                candidate.Agent.Position != world.Agent.Position || world.Entities.Any(old => !candidate.Entities.Any(e => e.Id == old.Id && e.Position == old.Position)))
                throw new InvalidDataException("Activation must preserve age, time, entity identities and existing positions.");
            var nextSystems = OrderSystems(loaded.SelectMany(m => m.Builder.Systems));
            if (loaded.Count(m => m.Builder.Motivation is not null) != 1) throw new InvalidDataException("Exactly one motivation policy is required.");
            var providers = loaded.SelectMany(m => m.Builder.ActionProviders).ToArray();
            if (providers.Select(a => a.Type).Distinct().Count() != providers.Length) throw new InvalidDataException("Duplicate action handler.");
            ValidateWorld(candidate);
            ValidateSignals(loaded, candidate);
            ValidateTokens(Observe(loaded, candidate), nextObservations.Keys.Count);
            ValidateCandidates(LegalActions(loaded, candidate), nextActions.Keys.Count);
            long revision = changed ? checked(Revision + 1) : Revision;
            prepared = PresentationHost?.Prepare(loaded.Select(m => new PresentationPackage(CopyManifest(m.Manifest), m.Directory)).ToArray(), Capture(candidate, revision));
            // Prepare/Commit contract: all fallible allocations happen in Prepare; Commit only attaches prepared resources.
            prepared?.Commit();
            replaced = modules;
            modules = loaded;
            loaded = [];
            world = candidate;
            systems = nextSystems;
            observations = nextObservations;
            actions = nextActions;
            componentKeys = knownComponents;
            Revision = revision;
            committed = true;
            Controller.SchemaChanged(observations.Keys, actions.Keys);
            if (changed) Controller.ConditionsChanged(Revision);
            foreach (var old in replaced) Retire(old);
            replaced = null;
            return LastActivation = new(request.Id, true, changed ? "Conditions activated." : "Presentation activated; learning conditions preserved.", Revision);
        }
        catch (Exception ex)
        {
            if (committed)
            {
                Fault = "Activation committed, but controller notification failed: " + ex.Message;
                return LastActivation = new(request.Id, true, Fault, Revision);
            }
            return LastActivation = new(request.Id, false, ex.Message, Revision);
        }
        finally
        {
            prepared?.Dispose();
            foreach (var item in loaded) Retire(item);
            if (replaced is not null) foreach (var item in replaced) Retire(item);
            if (retired.Count > 256) retired.RemoveAll(r => !r.IsAlive);
        }
    }

    public TrainingSample AdvanceOne(bool learning = true)
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Fault is not null) throw new InvalidOperationException(Fault);
        while (pending.TryDequeue(out var request)) Activate(request);
        if (Fault is not null) throw new InvalidOperationException(Fault);
        try
        {
            var state = Observe();
            var legal = LegalActions();
            var action = Controller.Choose(CloneTokens(state), CloneActions(legal), learning);
            if (!legal.Any(c => c.Type == action.Type && c.Parameters.SequenceEqual(action.Parameters))) throw new InvalidOperationException("Controller selected an illegal action.");
            var before = Motivation(modules, world);
            modules.SelectMany(m => m.Builder.ActionProviders).Single(p => p.Type == action.Type).Execute(world, action);
            world.Time += world.Options.TimeStep;
            world.Age = checked(world.Age + 1);
            foreach (var system in systems)
            {
                system.Update(world);
                EnsureComponents(world, modules.SelectMany(m => m.Builder.Components));
            }
            ValidateWorld(world);
            if (world.AllEntities.Any(e => e.Components.Keys.Any(key => !componentKeys.Contains(key))))
                throw new InvalidDataException("A system created an undeclared component.");
            foreach (var item in modules)
                if (!world.ModuleState.TryGetValue(item.Manifest.Id, out var data) || data.Version != item.Manifest.StateSchemaVersion)
                    throw new InvalidDataException($"Invalid module state schema: {item.Manifest.Id}.");
            var after = Motivation(modules, world);
            LastReward = modules.Select(m => m.Builder.Motivation).Single(m => m is not null)!.Evaluate(before, after);
            if (!double.IsFinite(LastReward)) throw new InvalidOperationException("Non-finite motivation signal.");
            var next = Observe();
            var candidates = LegalActions();
            var report = Report();
            var transition = new AgentTransition(state, action, LastReward, next, candidates, false, Revision);
            if (learning) Controller.Receive(transition);
            TrajectorySink?.Invoke(transition with
            {
                State = CloneTokens(state), NextState = CloneTokens(next),
                Action = action with { Parameters = (float[])action.Parameters.Clone() }, NextActions = CloneActions(candidates)
            });
            return new(world.Age, 1, world.Age, world.Agent.Position.X, world.Agent.Position.Y, report, LastReward, false);
        }
        catch (Exception ex) { Fault = $"Runtime paused at age {world.Age}: {ex.Message}"; throw new InvalidOperationException(Fault); }
    }

    public ObservationToken[] Observe()
    {
        var result = Observe(modules, world);
        ValidateTokens(result, observations.Keys.Count);
        return result;
    }
    public ActionCandidate[] LegalActions()
    {
        var result = LegalActions(modules, world);
        ValidateCandidates(result, actions.Keys.Count);
        return result;
    }
    public HomeostasisReport Report() => HomeostasisReport.Measure(modules.SelectMany(m => m.Builder.Drives).SelectMany(p => p(world)), modules.SelectMany(m => m.Builder.Descriptors));
    public RuntimeFrame Snapshot() => Capture(world, Revision);
    public IReadOnlyList<PresentationPackage> PresentationPackages => modules.Select(m => new PresentationPackage(CopyManifest(m.Manifest), m.Directory)).ToArray();
    public void PreparePresentation()
    {
        using var prepared = PresentationHost?.Prepare(PresentationPackages, Snapshot());
        prepared?.Commit();
    }
    private static RuntimeFrame Capture(RuntimeWorld state, long revision)
    {
        EntitySnapshot Entity(RuntimeEntity e) => new(e.Id, e.Position, new ReadOnlyDictionary<string, ComponentSnapshot>(
            e.Components.ToDictionary(p => p.Key, p => ComponentSnapshot.Capture(p.Value), StringComparer.Ordinal)));
        return new(state.Options.Width, state.Options.Height, state.Age, state.Time, revision, Entity(state.Agent),
            Array.AsReadOnly(state.Entities.Select(Entity).ToArray()), new ReadOnlyDictionary<string, ComponentSnapshot>(
                state.ModuleState.ToDictionary(p => p.Key, p => ComponentSnapshot.Capture(p.Value), StringComparer.Ordinal)));
    }
    private static ObservationToken[] Observe(IEnumerable<LoadedRuntimeModule> modules, RuntimeWorld state) => CloneTokens(modules.SelectMany(m => m.Builder.Sensors).SelectMany(p => p(state)).ToArray());
    private static ActionCandidate[] LegalActions(IEnumerable<LoadedRuntimeModule> modules, RuntimeWorld state)
    {
        var filters = modules.SelectMany(m => m.Builder.ActionFilters).ToArray();
        return CloneActions(modules.SelectMany(m => m.Builder.ActionProviders).SelectMany(p => p.Candidates(state)).Where(a => filters.All(filter => filter(state, a))).ToArray());
    }
    private static ObservationToken[] CloneTokens(ObservationToken[] tokens) => tokens.Select(t => t with { Features = (float[])t.Features.Clone() }).ToArray();
    private static ActionCandidate[] CloneActions(ActionCandidate[] candidates) => candidates.Select(a => a with { Parameters = (float[])a.Parameters.Clone() }).ToArray();
    private void ValidateTokens(ObservationToken[] tokens, int count)
    {
        if (tokens.Length < Controller.Limits.MinimumObservations || tokens.Any(t => t.Type < 0 || t.Type >= count || t.Features.Length > Controller.Limits.FeatureWidth || t.Features.Any(f => !float.IsFinite(f) || f is < -1 or > 1)))
            throw new InvalidDataException("Invalid observation token or controller feature capacity exceeded.");
    }
    private void ValidateCandidates(ActionCandidate[] candidates, int count)
    {
        if (candidates.Length == 0 || candidates.Any(t => t.Type < 0 || t.Type >= count || t.Parameters.Length > Controller.Limits.FeatureWidth || t.Parameters.Any(f => !float.IsFinite(f) || f is < -1 or > 1)))
            throw new InvalidDataException("No legal action or invalid action schema.");
    }
    private static MotivationInput Motivation(IEnumerable<LoadedRuntimeModule> modules, RuntimeWorld state) => new(
        modules.SelectMany(m => m.Builder.Drives).SelectMany(p => p(state)).ToArray(),
        new ReadOnlyDictionary<string, double>(modules.SelectMany(m => m.Builder.Signals).SelectMany(p => p(state)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)));
    private static void ValidateSignals(IEnumerable<LoadedRuntimeModule> modules, RuntimeWorld state)
    {
        var input = Motivation(modules, state);
        _ = HomeostasisReport.Measure(input.Drives, modules.SelectMany(m => m.Builder.Descriptors));
        if (input.Signals.Values.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid motivation signal.");
        if (!double.IsFinite(modules.Select(m => m.Builder.Motivation).Single(p => p is not null)!.Evaluate(input, input))) throw new InvalidDataException("Invalid motivation policy.");
    }
    private static void ValidateWorld(RuntimeWorld state)
    {
        if (!state.Contains(state.Agent.Position) || !double.IsFinite(state.Time) || state.Age < 0 ||
            state.AllEntities.Any(e => !state.Contains(e.Position) || e.Id < 1) || state.AllEntities.Select(e => e.Id).Distinct().Count() != state.Entities.Count + 1 ||
            state.NextEntityId <= state.AllEntities.Max(e => e.Id))
            throw new InvalidDataException("Invalid world state.");
    }
    private static void EnsureComponents(RuntimeWorld state, IEnumerable<ComponentRegistration> registrations)
    {
        var entities = state.AllEntities.ToArray();
        foreach (var registration in registrations)
        foreach (var entity in entities)
        {
            if (!entity.Components.TryGetValue(registration.Key, out var data))
            {
                if (!registration.Applies(state, entity)) continue;
                data = new() { Version = registration.Version };
                entity.Components.Add(registration.Key, data);
                registration.Initialize(state, entity, data);
            }
            if (data.Version != registration.Version) throw new InvalidDataException($"Unmigrated component {registration.Key} on {entity.Id}.");
        }
    }
    private static TypeRegistry CopyRegistry(TypeRegistry source)
    {
        var copy = new TypeRegistry(source.Capacity);
        foreach (string key in source.Keys) copy.Register(key);
        return copy;
    }
    internal static bool SameLogic(ModuleManifest a, ModuleManifest b) => a.LogicHash == b.LogicHash && a.EntryType == b.EntryType && a.Assembly == b.Assembly && a.StateSchemaVersion == b.StateSchemaVersion &&
        a.Settings.GetRawText() == b.Settings.GetRawText() && JsonSerializer.Serialize(a.Schemas) == JsonSerializer.Serialize(b.Schemas) &&
        a.Dependencies.SequenceEqual(b.Dependencies) && a.Provides.SequenceEqual(b.Provides) && a.Requires.SequenceEqual(b.Requires) &&
        a.LogicFiles.Count == b.LogicFiles.Count && a.LogicFiles.All(p => b.LogicFiles.TryGetValue(p.Key, out var hash) && hash == p.Value);
    private static void ValidateDependencies(Dictionary<string, ModuleManifest> modules)
    {
        var provided = modules.Values.SelectMany(m => m.Provides).ToHashSet(StringComparer.Ordinal);
        foreach (var module in modules.Values)
        {
            foreach (var dependency in module.Dependencies)
                if (!modules.TryGetValue(dependency.Id, out var target) || target.Version != dependency.Version) throw new InvalidDataException($"Missing exact dependency {dependency.Id}@{dependency.Version} for {module.Id}.");
            if (module.Requires.Any(r => !provided.Contains(r))) throw new InvalidDataException($"Missing capability for {module.Id}.");
        }
    }
    private static IEnumerable<ModuleManifest> OrderPackages(Dictionary<string, ModuleManifest> modules) =>
        Sort(modules.Keys, key => modules[key].Dependencies.Select(d => d.Id)).Select(key => modules[key]);
    private static RuntimeSystem[] OrderSystems(IEnumerable<RuntimeSystem> source)
    {
        var values = source.ToDictionary(s => s.Key, StringComparer.Ordinal);
        return Sort(values.Keys, key => values[key].After).Select(key => values[key]).ToArray();
    }
    private static string[] Sort(IEnumerable<string> keys, Func<string, IEnumerable<string>> dependencies)
    {
        var remaining = keys.ToHashSet(StringComparer.Ordinal);
        var done = new List<string>();
        while (remaining.Count > 0)
        {
            string? next = remaining.Order(StringComparer.Ordinal).FirstOrDefault(k => dependencies(k).All(done.Contains));
            if (next is null) throw new InvalidDataException("Missing dependency or dependency cycle.");
            remaining.Remove(next); done.Add(next);
        }
        return done.ToArray();
    }
    private void Retire(LoadedRuntimeModule module)
    {
        retired.Add(module.UnloadReference);
        try { module.Dispose(); } catch (Exception ex) { Fault ??= "Module cleanup failed: " + ex.Message; }
    }
    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Runtime mutations must run on their owner thread.");
    }
    private static ModuleManifest CopyManifest(ModuleManifest manifest) => manifest with
    {
        Settings = manifest.Settings.Clone(), Dependencies = (ModuleDependency[])manifest.Dependencies.Clone(),
        Provides = (string[])manifest.Provides.Clone(), Requires = (string[])manifest.Requires.Clone(),
        Schemas = new(manifest.Schemas), LogicFiles = new(manifest.LogicFiles), PresentationFiles = new(manifest.PresentationFiles)
    };
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        systems = [];
        foreach (var module in modules) Retire(module);
        modules.Clear();
        Controller.Dispose();
        TrajectorySink = null;
        PresentationHost = null;
    }
}
