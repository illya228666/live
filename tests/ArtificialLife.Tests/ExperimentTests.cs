using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class ExperimentTests
{
    static ExperimentTests() => ControllerRegistry.Register(new CounterFactory());
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, PackageCatalog.Json);
    private static RuntimeOptions Options() => RuntimeDefaults.FromLegacy(new ExperimentOptions
    {
        Learning = new() { Seed = 11, BatchSize = 4, WarmupSteps = 8, ReplayCapacity = 128, TrainEvery = 4 }
    }, hunger: false);
    private static ControllerSelection Counter(int choice = 0) => new() { Algorithm = "test-counter", Parameters = Json(new { choice }) };
    private static ExperimentDefinition Definition(RuntimeOptions? options = null) => new()
    {
        Runtime = options ?? Options(), TrainingSeeds = [11], EvaluationSeeds = [1001], EvaluateEvery = 4, EvaluationSteps = 5,
        Learners = [new() { Id = "counter", Controller = Counter() }],
        Phases = [new() { Id = "old", Steps = 8 }, new() { Id = "new", Steps = 12, Activate = [new("hunger", "1.0.0"), new("food", "1.0.0")] }]
    };

    [Fact]
    public void ExplicitDqnParametersOverrideLegacyAndWorldSeedIsIndependent()
    {
        var old = Options();
        var resolved = ControllerRegistry.Resolve(old with
        {
            WorldSeed = 77, ControllerSeed = 99,
            Controller = new() { Parameters = Json(new { learning = new { learningRate = 0.002, batchSize = 8, warmupSteps = 16 }, network = new { hiddenSize = 12 } }) }
        });
        Assert.Equal(12, resolved.Network.HiddenSize);
        Assert.Equal(0.002, resolved.Learning.LearningRate);
        Assert.Equal(99, resolved.Learning.Seed);
        Assert.Equal(77, resolved.WorldSeed);
        Assert.Equal(old.Learning, ControllerRegistry.Resolve(old).Learning);
        using var a = new RuntimeSession(resolved, new CounterController(0));
        using var b = new RuntimeSession(resolved with { ControllerSeed = 1234 }, new CounterController(0));
        Assert.Equal(a.Snapshot(), b.Snapshot());
        using var c = new RuntimeSession(resolved with { WorldSeed = 78 }, new CounterController(0));
        Assert.NotEqual(a.Snapshot(), c.Snapshot());
        Assert.Throws<InvalidDataException>(() => ControllerRegistry.Resolve(old with { Controller = new() { Algorithm = "missing" } }));
    }

    [Fact]
    public void BehavioralMetricsAreIdenticalForDifferentRewardsOnTheSameActions()
    {
        using var files = new TemporaryFiles();
        var options = Options();
        string replacement = Path.Combine(files.Root, "packages", "homeostasis", "2.0.0");
        Directory.CreateDirectory(replacement);
        string assembly = typeof(FixtureMotivation).Assembly.Location;
        File.Copy(assembly, Path.Combine(replacement, Path.GetFileName(assembly)));
        var manifest = new ModuleManifest("homeostasis", "2.0.0", 1, Path.GetFileName(assembly), typeof(FixtureMotivation).FullName!)
        { LogicHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))) };
        ExperimentJson.Atomic(Path.Combine(replacement, "module.json"), manifest);
        var replacementCatalog = new PackageCatalog(Path.Combine(files.Root, "packages"));
        using var a = new RuntimeSession(options, new CounterController(0));
        foreach (var reference in options.Packages)
        {
            string source = new PackageCatalog(options.PackageRoot).DirectoryFor(reference), target = replacementCatalog.DirectoryFor(reference);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            { string destination = Path.Combine(target, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination); }
        }
        using var changed = new RuntimeSession(options with
        {
            PackageRoot = replacementCatalog.Root, Packages = options.Packages.Select(p => p.Id == "homeostasis" ? new PackageReference("homeostasis", "2.0.0") : p).ToArray()
        }, new CounterController(0));
        var first = BehaviorEvaluators.Create(new()); var second = BehaviorEvaluators.Create(new());
        double firstReward = 0, secondReward = 0;
        for (int i = 0; i < 20; i++)
        {
            var x = a.AdvanceOne(); var y = changed.AdvanceOne();
            firstReward += x.Reward; secondReward += y.Reward;
            foreach (var e in first) e.Observe(a.Snapshot(), x.Homeostasis);
            foreach (var e in second) e.Observe(changed.Snapshot(), y.Homeostasis);
        }
        Assert.NotEqual(firstReward, secondReward);
        Assert.Equal(BehaviorEvaluators.Complete(first).OrderBy(p => p.Key), BehaviorEvaluators.Complete(second).OrderBy(p => p.Key));
        Assert.Null(BehaviorEvaluators.Complete(first)["hunger.mean"].Value);
        Assert.Equal(0, BehaviorEvaluators.Complete(first)["hunger.mean"].Samples);
    }

    [Fact]
    public void ExperimentEvaluationDoesNotChangeSubsequentDqnLearning()
    {
        var options = ControllerRegistry.Resolve(Options());
        RuntimeFrame expected; ControllerStatus status; float[] scores;
        using (var baseline = new RuntimeSession(options, ControllerRegistry.Create(options)))
        {
            for (int i = 0; i < 80; i++) baseline.AdvanceOne();
            expected = baseline.Snapshot(); status = baseline.Controller.Status;
            scores = ((DqnController)baseline.Controller).Brain.Scores(baseline.Observe(), baseline.LegalActions());
        }
        using var runtime = new RuntimeSession(options, ControllerRegistry.Create(options));
        for (int i = 0; i < 40; i++) runtime.AdvanceOne();
        var before = runtime.Snapshot(); var beforeStatus = runtime.Controller.Status;
        var episode = ExperimentRunner.EvaluateEpisode(runtime, runtime.Controller, options, new(), 12, 40, "test", "test", "policy", 1001, 0, 1001);
        Assert.Equal(12, episode.Steps);
        Assert.Equal(before, runtime.Snapshot()); Assert.Equal(beforeStatus, runtime.Controller.Status);
        for (int i = 0; i < 40; i++) runtime.AdvanceOne();
        Assert.Equal(expected, runtime.Snapshot()); Assert.Equal(status, runtime.Controller.Status);
        Assert.Equal(scores, ((DqnController)runtime.Controller).Brain.Scores(runtime.Observe(), runtime.LegalActions()));
    }

    [Fact]
    public void NonDqnTrialPreservesLifeEvaluatesOldScenariosAndReproduces()
    {
        using var files = new TemporaryFiles();
        var definition = Definition(); definition.Validate();
        var job = definition.Jobs().Single() with { ConfigurationHash = "same", ProtocolHash = "protocol", ExecutionHash = "build" };
        var a = ExperimentRunner.Execute(job, Path.Combine(files.Root, "a"));
        var b = ExperimentRunner.Execute(job, Path.Combine(files.Root, "b"));
        Assert.Null(a.Error); Assert.Equal("completed", a.Status); Assert.Equal(20, a.TrainingSteps);
        Assert.Equal("not-assessed", a.BehaviorOutcome);
        Assert.Null(ExperimentComparison.ReproductionDifference(a, b));
        Assert.All(a.Learning, point => Assert.Equal(point.Step, point.Age));
        Assert.Contains(a.Learning, point => point.Phase == "new" && point.Step == 8 && point.Revision == 2);
        Assert.Contains(a.Evaluations, e => e.Kind == "policy" && e.Scenario == "old" && e.Phase == "new" && e.AtStep == 20);
        var frozen = Assert.Single(a.Evaluations, e => e.Kind == "frozen");
        Assert.Equal(8, frozen.AtStep); Assert.Equal("new", frozen.Scenario);
        Assert.Null(frozen.Metrics["wildlife.proximity"].Value);
        Assert.True(frozen.Metrics["wildlife.proximity"].Proxy);
        using var restored = RuntimeCheckpoint.Open(Path.Combine(files.Root, "a", "policies", "new-final"));
        Assert.IsType<CounterController>(restored.Controller);
        Assert.Equal(0, restored.Snapshot().Age); restored.AdvanceOne(false);
        var report = ExperimentComparison.Summarize([a, b]);
        Assert.Single(report.Reproductions); Assert.True(report.Reproductions[0].Matches);
        Assert.All(report.Metrics.Where(m => m.N > 0), m => Assert.Equal(1, m.N));
        Assert.Contains(report.Metrics, m => m.Measure == "learning-auc" && m.Scenario == "old");
    }

    [Fact]
    public void MissingMetricsAndLargeRewardCannotPassBehavioralCriteria()
    {
        var criteria = new[] { new BehaviorCriterion("new", "hunger.mean", Maximum: 0.2) };
        Assert.Equal("not-assessed", BehaviorEvaluators.Assess([], (_, _) => 100000));
        Assert.Equal("failed", BehaviorEvaluators.Assess(criteria, (_, _) => null));
        Assert.Equal("failed", BehaviorEvaluators.Assess(criteria, (_, _) => 0.8));
        Assert.Equal("passed", BehaviorEvaluators.Assess(criteria, (_, _) => 0.1));
        using var files = new TemporaryFiles();
        var job = Definition().Jobs().Single();
        var broken = job with { Definition = job.Definition with { Phases = [new() { Id = "bad", Steps = 1, Activate = [new("missing", "1")] }] } };
        var result = ExperimentRunner.Execute(broken, files.Root);
        Assert.Equal("failed", result.Status); Assert.Equal("not-assessed", result.BehaviorOutcome);
        Assert.NotNull(result.Error); Assert.True(File.Exists(Path.Combine(files.Root, "result.json")));
    }

    [Fact]
    public void ProtocolGroupsExcludeStudyFactorsButIncludePhysicalConditionsAndEvaluationRules()
    {
        var definition = Definition() with
        {
            Motivations = [new() { Id = "level", Settings = Json(new { improvementWeight = 0 }) }, new() { Id = "improved", Settings = Json(new { improvementWeight = 0.2 }) }],
            Learners = [new() { Id = "first", Controller = Counter(0) }, new() { Id = "second", Controller = Counter(1) }]
        };
        var jobs = definition.Jobs().ToArray();
        Assert.Equal(4, jobs.Length);
        Assert.Single(jobs.Select(ExperimentRunner.ProtocolHash).Distinct());
        var changed = definition with { EvaluationSteps = 99 };
        Assert.NotEqual(ExperimentRunner.ProtocolHash(jobs[0]), ExperimentRunner.ProtocolHash(changed.Jobs().First()));
        var climate = definition with { Runtime = definition.Runtime with { World = definition.Runtime.World with { Width = 45 } } };
        Assert.NotEqual(ExperimentRunner.ProtocolHash(jobs[0]), ExperimentRunner.ProtocolHash(climate.Jobs().First()));
        Assert.Throws<InvalidDataException>(() => (definition with { EvaluationSeeds = [11] }).Validate());
        Assert.Throws<InvalidDataException>(() => (definition with { Phases = [new() { Id = "../escape" }] }).Validate());
    }

    [Fact]
    public void V2PoliciesStillLoadAndArchitectureChecksApplyToV3()
    {
        using var files = new TemporaryFiles();
        var options = ControllerRegistry.Resolve(Options());
        using (var runtime = new RuntimeSession(options, ControllerRegistry.Create(options))) RuntimeCheckpoint.Save(files.Root, runtime);
        string generation = RuntimeCheckpoint.Resolve(files.Root);
        var manifest = ExperimentJson.Read<PolicyManifest>(Path.Combine(generation, "manifest.json"));
        Assert.Equal(3, manifest.FormatVersion);
        ExperimentJson.Atomic(Path.Combine(generation, "manifest.json"), manifest with { FormatVersion = 2, Options = manifest.Options with { Controller = null } });
        using (var v2 = RuntimeCheckpoint.Open(generation)) v2.AdvanceOne(false);
        ExperimentJson.Atomic(Path.Combine(generation, "manifest.json"), manifest with { Architecture = "wrong" });
        Assert.Throws<InvalidDataException>(() => RuntimeCheckpoint.Read(generation));
    }

    [Fact]
    public void CliContinuesAfterFailedTrialsAndSkipsMatchingCompletedTrials()
    {
        using var files = new TemporaryFiles();
        var definition = Definition() with
        {
            Motivations = [new() { Id = "invalid", Settings = Json(new { improvementWeight = 5 }) }, new() { Id = "valid" }],
            Learners = [new() { Id = "dqn", Controller = new() { Parameters = Json(new
            {
                network = new { hiddenSize = 8, stateSize = 8, typeEmbeddingSize = 2 },
                learning = new { warmupSteps = 8, batchSize = 4, replayCapacity = 32 }
            }) } }]
        };
        string config = Path.Combine(files.Root, "config.json"), output = Path.Combine(files.Root, "suite");
        ExperimentJson.Atomic(config, definition);
        string executable = Path.Combine(RuntimeDefaults.RepositoryRoot(), "src", "ArtificialLife.Cli", "bin", "Release", "net10.0", "ArtificialLife.Cli.exe");
        (int Exit, string Output) Launch()
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "experiment", "--config", config, "--output", output }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60000));
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        var first = Launch(); Assert.Equal(1, first.Exit);
        var suite = ExperimentJson.Read<ExperimentSuiteResult>(Path.Combine(output, "suite.json"));
        Assert.Equal("failed", suite.Status); Assert.Equal(2, suite.Runs.Length); Assert.Single(suite.Errors);
        var results = suite.Runs.Select(r => ExperimentJson.Read<ExperimentRunResult>(Path.Combine(output, r, "result.json"))).ToArray();
        Assert.Equal("failed", results[0].Status); Assert.Equal("completed", results[1].Status);
        var second = Launch(); Assert.Equal(1, second.Exit); Assert.Contains("SKIP valid--dqn", second.Output);
        var comparison = ExperimentComparison.Summarize(results);
        Assert.Single(comparison.Trials); Assert.NotEmpty(comparison.Warnings);
        var incompatible = results[1] with { ProtocolHash = "other-world" };
        var groups = ExperimentComparison.Summarize([results[1], incompatible]);
        Assert.Equal(2, groups.Metrics.Select(m => m.Protocol).Distinct().Count()); Assert.Empty(groups.Pairs);
    }

    private sealed class TemporaryFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ArtificialLife-experiments-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class CounterFactory : IAgentControllerFactory
    {
        public string Algorithm => "test-counter";
        public string Architecture => "test-counter.v1";
        public JsonElement ResolveParameters(JsonElement parameters) => parameters.Clone();
        public IAgentController Create(JsonElement parameters, int seed) => new CounterController(new RuntimeSettings(parameters).Integer("choice", 0));
        public bool Supports(IAgentController controller) => controller is CounterController;
    }
    private sealed class CounterController(int choice) : IAgentController
    {
        private long steps;
        private int inferenceMemory;
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(steps, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) { inferenceMemory++; return actions[choice % actions.Length]; }
        public void Receive(AgentTransition transition) => steps++;
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new[] { choice, inferenceMemory }));
        public void Import(string path) => inferenceMemory = JsonSerializer.Deserialize<int[]>(File.ReadAllText(path))![1];
        public IAgentController CreateInferenceController() => new CounterController(choice) { inferenceMemory = inferenceMemory };
        public void Dispose() { }
    }
}
