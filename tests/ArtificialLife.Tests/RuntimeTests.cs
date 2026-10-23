using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class RuntimeTests
{
    private static RuntimeOptions Options(bool hunger = false) => RuntimeDefaults.FromLegacy(new()
    {
        World = new() { EpisodeSteps = 10 },
        FoodSpawner = new() { SpawnIntervalTicks = 5 },
        Learning = new() { WarmupSteps = 64, BatchSize = 16, ReplayCapacity = 512 }
    }, hunger);
    private static RuntimeSession Create(RuntimeOptions options, PackageCatalog? catalog = null, IRuntimePresentationHost? host = null) =>
        new(options, new DqnController(options.Network, options.Learning), catalog, presentationHost: host);

    [Fact]
    public void HeadlessRuntimeDoesNotLoadGodotAssemblies()
    {
        using var runtime = Create(Options(true));
        runtime.AdvanceOne();
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name!.StartsWith("Godot", StringComparison.Ordinal));
    }

    [Fact]
    public void AControllerCannotFabricateLegalityByMutatingItsCandidateArray()
    {
        var controller = new MutatingController();
        using var runtime = new RuntimeSession(Options(), controller);
        var before = runtime.Snapshot();
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceOne());
        Assert.Equal(before, runtime.Snapshot());
        Assert.False(controller.Received);
    }

    [Fact]
    public void AddingHungerPreservesLifeWeightsOptimizerAndIsolatesReplay()
    {
        using var runtime = Create(Options());
        for (int i = 0; i < 100; i++) runtime.AdvanceOne();
        var before = runtime.Snapshot();
        var status = runtime.Controller.Status;
        float[] scores = ((DqnController)runtime.Controller).Brain.Scores(runtime.Observe(), runtime.LegalActions());
        var outcome = runtime.Activate(new("hunger", [new("hunger", "1.0.0"), new("food", "1.0.0")]));
        Assert.True(outcome.Accepted, outcome.Message);
        var after = runtime.Snapshot();
        Assert.Equal(before.Age, after.Age); Assert.Equal(before.Time, after.Time);
        Assert.Equal(before.Agent.Id, after.Agent.Id); Assert.Equal(before.Agent.Position, after.Agent.Position);
        Assert.Equal(before.Agent.Components["temperature.body"].Read<double>("temperature"), after.Agent.Components["temperature.body"].Read<double>("temperature"));
        Assert.Equal(status.Updates, runtime.Controller.Status.Updates);
        Assert.Equal(status.Steps, runtime.Controller.Status.Steps);
        Assert.Equal(0, runtime.Controller.Status.EligibleReplayCount);
        Assert.Equal(status.ReplayCount, runtime.Controller.Status.ReplayCount);
        // The network parameters themselves are unchanged: score exactly the old observation set.
        var oldState = runtime.Observe().Where(t => runtime.ObservationKeys[t.Type] != "hunger.body.v1").ToArray();
        Assert.Equal(scores, ((DqnController)runtime.Controller).Brain.Scores(oldState, runtime.LegalActions()));
        runtime.AdvanceOne();
        Assert.Equal(status.Updates, runtime.Controller.Status.Updates);
        Assert.Equal(1, runtime.Controller.Status.EligibleReplayCount);
        Assert.All(((DqnController)runtime.Controller).Brain.Replay.Sample(20, new(1), runtime.Revision), e => Assert.Equal(runtime.Revision, e.Revision));
        Assert.Equal(before.Age + 1, runtime.Snapshot().Age);
        Assert.False(runtime.AdvanceOne().Terminal);
    }

    [Fact]
    public void AddingAnActionUsesTheExistingScorerWithoutGrowingTheNetwork()
    {
        using var fixture = new FixtureCatalog(Options()); fixture.Publish("fixture", "1", settings: new { AddAction = true });
        using var runtime = Create(Options(), fixture.Catalog);
        var keys = runtime.ActionKeys.ToArray();
        Assert.True(runtime.Activate(new("action", [new("fixture", "1")])).Accepted);
        Assert.Equal(keys, runtime.ActionKeys.Take(keys.Length));
        Assert.Equal(10, runtime.LegalActions().Length);
        Assert.Equal(10, ((DqnController)runtime.Controller).Brain.Scores(runtime.Observe(), runtime.LegalActions()).Length);
        Assert.Equal(256, runtime.Controller.Limits.ActionTypes);
        runtime.AdvanceOne();
    }

    [Fact]
    public void AnAddedModuleCanFilterExistingActionsWithoutReplacingMovement()
    {
        using var fixture = new FixtureCatalog(Options()); fixture.Publish("fixture", "1", settings: new { OnlyStay = true });
        using var runtime = Create(Options(), fixture.Catalog);
        var before = runtime.Snapshot();
        Assert.True(runtime.Activate(new("filter", [new("fixture", "1")])).Accepted);
        var action = Assert.Single(runtime.LegalActions());
        Assert.All(action.Parameters, value => Assert.Equal(0, value));
        runtime.AdvanceOne();
        Assert.Equal(before.Agent.Position, runtime.Snapshot().Agent.Position);
        Assert.Equal(before.Age + 1, runtime.Snapshot().Age);
    }

    [Fact]
    public void ComponentStorageRejectsPluginObjects()
    {
        var data = new ComponentData();
        Assert.Throws<ArgumentException>(() => data.Write("object", new FixtureModule()));
        Assert.Throws<ArgumentException>(() => data.Read<FixtureModule>("object"));
        data.Write("array", new[] { 1.0, 2.0 });
        Assert.Equal(new[] { 1.0, 2.0 }, data.Read<double[]>("array"));
    }

    [Fact]
    public void PrioritizedReplayFavorsPredictionErrorsAndNeverSamplesOldConditions()
    {
        var replay = new ReplayBuffer(8, 0.6);
        Experience Entry(long revision) => new([new(0, [0])], new(0, [0]), 0, [new(0, [0])], [new(0, [0])], false, revision);
        for (int i = 0; i < 8; i++) replay.Add(Entry(1));
        for (int i = 0; i < 8; i++) replay.UpdatePriority(i, i == 3 ? 100 : 0.01);
        var samples = replay.SamplePrioritized(1000, new(1), 1, 0.4);
        Assert.True(samples.Count(s => s.Slot == 3) > 900);
        Assert.All(samples, s => Assert.InRange(s.Weight, 0, 1));
        replay.Add(Entry(2)); replay.Add(Entry(2));
        Assert.All(replay.SamplePrioritized(100, new(1), 2, 0.4), s => Assert.Equal(2, s.Experience.Revision));
        Assert.Equal(2, replay.CountFor(2)); Assert.Equal(8, replay.Count);
    }

    [Fact]
    public void NewObservationIntroductionPreservesInitialScoresAndExportsFrozenMasks()
    {
        using var runtime = Create(Options());
        for (int i = 0; i < 100; i++) runtime.AdvanceOne();
        var scorer = ((DqnController)runtime.Controller).Brain;
        var scores = scorer.Scores(runtime.Observe(), runtime.LegalActions());
        Assert.True(runtime.Activate(new("hunger", [new("hunger", "1.0.0"), new("food", "1.0.0")])).Accepted);
        var after = scorer.Scores(runtime.Observe(), runtime.LegalActions());
        for (int i = 0; i < scores.Length; i++) Assert.Equal(scores[i], after[i], 6);
        Assert.Equal(0, scorer.ObservationWeights[runtime.ObservationKeys.ToList().IndexOf("hunger.body.v1")]);
        string path = Path.Combine(Path.GetTempPath(), "ArtificialLife-masks-" + Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeCheckpoint.Save(path, runtime);
            using var imported = RuntimeCheckpoint.Open(path);
            Assert.Equal(runtime.Controller.InferenceState.GetRawText(), imported.Controller.InferenceState.GetRawText());
            for (int i = 0; i < 10; i++) imported.AdvanceOne(false);
            Assert.Equal(runtime.Controller.InferenceState.GetRawText(), imported.Controller.InferenceState.GetRawText());
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void CompatibleV1PolicyImportsWithTheSameLogicalSlotsAndScores()
    {
        var options = new ExperimentOptions();
        var original = new SimulationSession(options);
        using var brain = new DqnBrain(options.Network, options.Learning);
        string path = Path.Combine(Path.GetTempPath(), "ArtificialLife-runtime-v1-" + Guid.NewGuid().ToString("N"));
        try
        {
            Checkpoint.Save(path, original, brain, 123);
            using var runtime = RuntimeCheckpoint.Open(path);
            Assert.Equal(original.Observations.Keys, runtime.ObservationKeys);
            Assert.Equal(original.Actions.Keys, runtime.ActionKeys);
            Assert.Equal(brain.Scores(original.Simulation.Observe(), original.Simulation.LegalActions()),
                ((DqnController)runtime.Controller).Brain.Scores(original.Simulation.Observe(), original.Simulation.LegalActions()));
            Assert.Equal(0, runtime.Snapshot().Age);
            Assert.Equal(0, runtime.Controller.Status.Steps);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ExportArchivesPackagesAndRejectsCorruptedWeights()
    {
        string path = Path.Combine(Path.GetTempPath(), "ArtificialLife-runtime-v2-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var runtime = Create(Options())) { runtime.AdvanceOne(); RuntimeCheckpoint.Save(path, runtime); }
            var manifest = RuntimeCheckpoint.Read(path);
            Assert.StartsWith(Path.GetFullPath(path), manifest.Options.PackageRoot, StringComparison.OrdinalIgnoreCase);
            using (var imported = RuntimeCheckpoint.Open(path)) imported.AdvanceOne(false);
            string weights = Path.Combine(RuntimeCheckpoint.Resolve(path), "weights.bin");
            File.AppendAllText(weights, "corrupt");
            Assert.Throws<InvalidDataException>(() => RuntimeCheckpoint.Open(path));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void AtomicPolicyPointerAlsoProtectsInferenceMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), "ArtificialLife-policy-metadata-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var runtime = Create(Options())) RuntimeCheckpoint.Save(path, runtime);
            string manifest = Path.Combine(RuntimeCheckpoint.Resolve(path), "manifest.json");
            File.AppendAllText(manifest, "\n "); // still valid JSON, but no longer the committed metadata
            Assert.Throws<InvalidDataException>(() => RuntimeCheckpoint.Read(path));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void AcceptanceCannotPassAnIncompleteOrBrokenLife()
    {
        var config = new ContinualExperimentOptions { BeforeSteps = 10, AfterSteps = 20, EvaluationSteps = 4, TrainingSeeds = [1, 2], EvaluationSeeds = [1001] };
        EvaluationMetrics Metrics(double hunger, double temperature) => new(4, 0.5, 0, new(new Dictionary<string, DriveMetrics>
        {
            ["hunger"] = new(4, hunger, 0, hunger, 1 - hunger, 1),
            ["temperature"] = new(4, temperature, 0, temperature, 1 - temperature, 1)
        }));
        ContinualSeedResult Result(int seed) => new(seed, Metrics(0, 0.01), Metrics(0.8, 0.01), Metrics(1, 0.5), Metrics(0.6, 0.04), 10, 30, 1, 2);
        Assert.False(ContinualExperiment.Summarize(false, 0, config, [Result(1)]).Passed);
        Assert.False(ContinualExperiment.Summarize(false, 0, config, [Result(1), Result(2) with { AgeAfter = 29 }]).Passed);
        Assert.True(ContinualExperiment.Summarize(false, 0, config, [Result(1), Result(2)]).Passed);
        Assert.False(ContinualExperiment.Summarize(false, 0, config, [Result(1), Result(2) with { Adapted = Metrics(0.8, 0.04) }]).Passed);
    }

    [Fact]
    public void CapacityFailureDoesNotChangeWorldRegistriesOrController()
    {
        using var fixture = new FixtureCatalog(Options());
        fixture.Publish("fixture", "1", settings: new { ExtraTypes = 300 });
        using var runtime = Create(Options(), fixture.Catalog);
        runtime.AdvanceOne();
        var before = runtime.Snapshot(); var keys = runtime.ObservationKeys.ToArray(); var status = runtime.Controller.Status;
        Assert.False(runtime.Activate(new("too-large", [new("fixture", "1")])).Accepted);
        Assert.Equal(before, runtime.Snapshot()); Assert.Equal(keys, runtime.ObservationKeys); Assert.Equal(status, runtime.Controller.Status);
        Assert.Null(runtime.Fault);
        runtime.AdvanceOne();
    }

    [Fact]
    public void AddingAPackageCannotSilentlyReloadAnEditedActiveVersion()
    {
        using var fixture = new FixtureCatalog(Options()); fixture.Publish("fixture", "1");
        using var runtime = Create(Options(), fixture.Catalog);
        var before = runtime.Snapshot();
        File.AppendAllText(Path.Combine(fixture.Catalog.DirectoryFor(new("temperature", "1.0.0")), "module.json"), "\n ");
        var result = runtime.Activate(new("install", [new("fixture", "1")]));
        Assert.False(result.Accepted);
        Assert.Contains("changed in place", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, runtime.Snapshot());
        runtime.AdvanceOne();
    }

    [Fact]
    public void FailedMigrationDiscardsCandidateAndSuccessfulUpgradeMigratesState()
    {
        using var fixture = new FixtureCatalog(Options());
        fixture.Publish("fixture", "1");
        fixture.Publish("fixture", "2", schema: 2, settings: new { FailMigrate = true });
        fixture.Publish("fixture", "3", schema: 2);
        using var runtime = Create(Options(), fixture.Catalog);
        Assert.True(runtime.Activate(new("install", [new("fixture", "1")])).Accepted);
        for (int i = 0; i < 12; i++) runtime.AdvanceOne();
        var before = runtime.Snapshot();
        Assert.False(runtime.Activate(new("bad", [new("fixture", "2")])).Accepted);
        Assert.Equal(before, runtime.Snapshot());
        Assert.True(runtime.Activate(new("upgrade", [new("fixture", "3")])).Accepted);
        var after = runtime.Snapshot();
        Assert.Equal(before.Age, after.Age); Assert.Equal(before.Agent.Position, after.Agent.Position);
        Assert.Equal(2, after.Agent.Components["fixture.marker"].Version);
        Assert.Equal(2, after.Agent.Components["fixture.marker"].Read<int>("value"));
        Assert.Equal(1, before.Agent.Components["fixture.marker"].Version);
    }

    [Fact]
    public void NewComponentRuleAlsoInitializesFutureEntities()
    {
        using var fixture = new FixtureCatalog(Options(true)); fixture.Publish("fixture", "1");
        using var runtime = Create(Options(true), fixture.Catalog);
        Assert.True(runtime.Activate(new("install", [new("fixture", "1")])).Accepted);
        for (int i = 0; i < 5; i++) runtime.AdvanceOne();
        var apple = Assert.Single(runtime.Snapshot().Entities, e => e.Components.ContainsKey("food.apple"));
        Assert.Equal(1, apple.Components["fixture.marker"].Read<int>("value"));
    }

    [Fact]
    public void PresentationOnlyUpdatePreservesRevisionAndFailedPrepareRejectsWholeUpdate()
    {
        using var fixture = new FixtureCatalog(Options());
        fixture.Publish("fixture", "1"); fixture.Publish("fixture", "2");
        var host = new FakePresentationHost();
        using var runtime = Create(Options(), fixture.Catalog, host);
        Assert.True(runtime.Activate(new("install", [new("fixture", "1")])).Accepted);
        runtime.AdvanceOne();
        var before = runtime.Snapshot(); var status = runtime.Controller.Status;
        host.Fail = true;
        Assert.False(runtime.Activate(new("rejected-view", [new("fixture", "2")])).Accepted);
        Assert.Equal(before, runtime.Snapshot()); Assert.Equal(status, runtime.Controller.Status);
        host.Fail = false;
        Assert.True(runtime.Activate(new("view", [new("fixture", "2")])).Accepted);
        Assert.Equal(before, runtime.Snapshot()); Assert.Equal(status, runtime.Controller.Status);
        Assert.Equal(3, host.Commits);
    }

    [Fact]
    public void MotivationCanBeReplacedInTheSameLife()
    {
        using var fixture = new FixtureCatalog(Options());
        fixture.Publish("homeostasis", "2", entry: typeof(FixtureMotivation).FullName!);
        using var runtime = Create(Options(), fixture.Catalog);
        runtime.AdvanceOne(); var before = runtime.Snapshot();
        Assert.True(runtime.Activate(new("motivation", [new("homeostasis", "2")])).Accepted);
        Assert.Equal(before.Age, runtime.Snapshot().Age);
        Assert.Equal(before.Revision + 1, runtime.Revision);
        Assert.Equal(0.125, runtime.AdvanceOne().Reward);
    }

    [Fact]
    public void LogicFailurePausesWithoutAppendingAnIncompleteTransition()
    {
        using var fixture = new FixtureCatalog(Options()); fixture.Publish("fixture", "1", settings: new { FailStep = true });
        using var runtime = Create(Options(), fixture.Catalog);
        Assert.True(runtime.Activate(new("install", [new("fixture", "1")])).Accepted);
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceOne());
        Assert.NotNull(runtime.Fault); Assert.Equal(0, runtime.Controller.Status.ReplayCount);
        var frame = runtime.Snapshot();
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceOne());
        Assert.Equal(frame, runtime.Snapshot());
    }

    [Fact]
    public void DependencyCycleAndWrongFeatureWidthAreRejectedBeforeCommit()
    {
        using var fixture = new FixtureCatalog(Options());
        fixture.Publish("one", "1", dependencies: [new("two", "1")]);
        fixture.Publish("two", "1", dependencies: [new("one", "1")]);
        fixture.Publish("fixture", "1", settings: new { Width = 5 });
        using var runtime = Create(Options(), fixture.Catalog); var before = runtime.Snapshot();
        Assert.False(runtime.Activate(new("cycle", [new("one", "1"), new("two", "1")])).Accepted);
        Assert.False(runtime.Activate(new("width", [new("fixture", "1")])).Accepted);
        Assert.Equal(before, runtime.Snapshot());
    }

    [Fact]
    public void InboxIgnoresIncompleteRequestsAndReportsRejectedPackages()
    {
        using var fixture = new FixtureCatalog(Options());
        string inboxPath = Path.Combine(fixture.Root, "inbox"); Directory.CreateDirectory(inboxPath);
        File.WriteAllText(Path.Combine(inboxPath, "pending.tmp"), "{");
        using var runtime = Create(Options(), fixture.Catalog); var before = runtime.Snapshot();
        runtime.Poll(new(inboxPath)); Assert.Equal(before, runtime.Snapshot());
        File.WriteAllText(Path.Combine(inboxPath, "request.ready.json"), JsonSerializer.Serialize(new ActivationRequest("missing", [new("absent", "1")])));
        runtime.Poll(new(inboxPath));
        Assert.False(runtime.LastActivation!.Accepted); Assert.Equal(before, runtime.Snapshot());
        Assert.True(File.Exists(Path.Combine(inboxPath, "request.result.json")));
    }

    [Fact]
    public void RepeatedUpdatesReleaseOldAssemblyContexts()
    {
        using var fixture = new FixtureCatalog(Options());
        fixture.Publish("fixture", "1"); fixture.Publish("fixture", "2");
        using var runtime = Create(Options(), fixture.Catalog);
        var retired = UpdateRepeatedly(runtime);
        for (int i = 0; i < 10 && retired.Any(r => r.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(retired, reference => Assert.False(reference.IsAlive));
        runtime.AdvanceOne();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] UpdateRepeatedly(RuntimeSession runtime)
    {
        for (int i = 0; i < 8; i++) Assert.True(runtime.Activate(new("update-" + i, [new("fixture", i % 2 == 0 ? "1" : "2")])).Accepted);
        return runtime.RetiredContexts.ToArray();
    }

    private sealed class FakePresentationHost : IRuntimePresentationHost
    {
        public bool Fail; public int Commits;
        public IPreparedPresentation Prepare(IReadOnlyList<PresentationPackage> packages, RuntimeFrame frame) => Fail ? throw new InvalidOperationException("Bad renderer") : new Prepared(this);
        private sealed class Prepared(FakePresentationHost host) : IPreparedPresentation
        {
            public void Commit() => host.Commits++;
            public void Dispose() { }
        }
    }
    private sealed class MutatingController : IAgentController
    {
        public bool Received;
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
        {
            actions[0].Parameters[0] = 0.123f;
            return actions[0];
        }
        public void Receive(AgentTransition transition) => Received = true;
        public void ConditionsChanged(long revision) { }
        public void Export(string weightsPath) => throw new NotSupportedException();
        public void Import(string weightsPath) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class FixtureCatalog : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ArtificialLife-runtime", Guid.NewGuid().ToString("N"));
        public PackageCatalog Catalog { get; }
        public FixtureCatalog(RuntimeOptions options)
        {
            Directory.CreateDirectory(Root); Catalog = new(Root);
            foreach (string id in new[] { "movement", "vision", "temperature", "homeostasis", "hunger", "food" })
            {
                string source = new PackageCatalog(options.PackageRoot).DirectoryFor(new(id, "1.0.0"));
                string destination = Catalog.DirectoryFor(new(id, "1.0.0")); Directory.CreateDirectory(destination);
                foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }
        public void Publish(string id, string version, int schema = 1, object? settings = null, ModuleDependency[]? dependencies = null, string? entry = null)
        {
            string directory = Catalog.DirectoryFor(new(id, version)); Directory.CreateDirectory(directory);
            string source = typeof(FixtureModule).Assembly.Location, file = Path.GetFileName(source);
            File.Copy(source, Path.Combine(directory, file), true);
            var manifest = new ModuleManifest(id, version, 1, file, entry ?? typeof(FixtureModule).FullName!)
            {
                Schemas = entry is null ? new() { ["fixture.marker"] = schema } : new(),
                Settings = JsonSerializer.SerializeToElement(settings ?? new { }), Dependencies = dependencies ?? [],
                LogicHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))
            };
            File.WriteAllText(Path.Combine(directory, "module.json"), JsonSerializer.Serialize(manifest, PackageCatalog.Json));
        }
        public void Dispose()
        {
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            try { Directory.Delete(Root, true); } catch (IOException) { /* collectible contexts may finish after the test frame returns */ }
        }
    }
}

// Loaded as real DLL plugins in a separate ALC; no test-framework objects cross the host boundary.
public sealed class FixtureModule : IRuntimeModule
{
    private ModuleBuilder builder = null!;
    private FixtureSettings settings = new();
    public void Register(ModuleBuilder builder)
    {
        this.builder = builder; settings = new(builder.Settings.Boolean("failMigrate"), builder.Settings.Boolean("failStep"), builder.Settings.Integer("extraTypes", 0), builder.Settings.Integer("width", 0));
        int version = builder.Manifest.Schemas["fixture.marker"];
        builder.Components.Add(new("fixture.marker", version, (w, e) => w.Agent.Id == e.Id || e.Components.ContainsKey("food.apple"), (_, _, d) => d.Write("value", 1)));
        for (int i = 0; i < settings.ExtraTypes; i++) builder.Observations.Register("fixture.extra." + i);
        if (settings.Width > 0)
        {
            int type = builder.Observations.Register("fixture.wide");
            builder.Sensors.Add(_ => [new(type, new float[settings.Width])]);
        }
        builder.Systems.Add(new("fixture.update", _ => { if (settings.FailStep) throw new InvalidOperationException("Fixture failed step."); }, []));
        if (builder.Settings.Boolean("addAction"))
        {
            int action = builder.Actions.Register("fixture.noop.v1");
            builder.ActionProviders.Add(new(action, _ => [new(action, [])], (_, _) => { }));
        }
        if (builder.Settings.Boolean("onlyStay")) builder.ActionFilters.Add((_, action) => action.Parameters.All(p => p == 0));
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous)
    {
        if (previous is not null)
        foreach (var entity in candidate.AllEntities)
            if (entity.Components.TryGetValue("fixture.marker", out var data)) { data.Version = builder.Manifest.Schemas["fixture.marker"]; data.Write("value", data.Read<int>("value") + 1); }
        if (settings.FailMigrate) { candidate.Agent.Position = new(0, 0); throw new InvalidOperationException("Fixture migration failed."); }
    }
    public sealed record FixtureSettings(bool FailMigrate = false, bool FailStep = false, int ExtraTypes = 0, int Width = 0);
}
public sealed class FixtureMotivation : IRuntimeModule, IMotivationPolicy
{
    public void Register(ModuleBuilder builder) => builder.Motivation = this;
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
    public double Evaluate(MotivationInput before, MotivationInput after) => 0.125;
}
