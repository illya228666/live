using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class ExpandingLifeTests
{
    [Fact]
    public void InstallingAndGivingMeaningToAnInteractionPreservesPhysicsAndController()
    {
        var options = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs/research-expanding-life.json")).Runtime;
        using var a = new RuntimeSession(options, new GenericActorCriticController(new() { Features = 8 }, 11));
        using var b = new RuntimeSession(options, new GenericActorCriticController(new() { Features = 8 }, 11));
        for (int i = 0; i < 10; i++) { a.AdvanceOne(); b.AdvanceOne(); }
        var original = a.Snapshot();
        Assert.True(a.Activate(new("install-a", [new("workbench", "1.0.0")])).Accepted);
        Assert.True(b.Activate(new("install-b", [new("workbench", "1.0.0")])).Accepted);
        Assert.Equal(original.Age, a.Snapshot().Age);
        Assert.Equal(original.Agent.Position, a.Snapshot().Agent.Position);
        Assert.Equal(10, a.Controller.Status.Updates);
        Assert.DoesNotContain("pressure", a.Report().Drives.Keys);
        Assert.True(a.Activate(new("meaning", [new("workbench", "2.0.0")])).Accepted);
        Assert.Contains("pressure", a.Report().Drives.Keys);
        Assert.Equal(1, a.Report().Drives["pressure"].Weight);
        var state = a.Observe(); var candidates = a.LegalActions();
        var before = ((GenericActorCriticController)a.Controller).ActionProbabilities(state, candidates);
        Assert.Equal(before, ((GenericActorCriticController)b.Controller).ActionProbabilities(state, candidates));
        // A need changes motivational interpretation; identical commanded histories retain identical physics.
        for (int i = 0; i < 40; i++)
        {
            a.AdvanceOne(false); b.AdvanceOne(false);
            Assert.Equal(a.Snapshot().ModuleState["workbench"].Read<double>("pressure"), b.Snapshot().ModuleState["workbench"].Read<double>("pressure"));
            Assert.Equal(ExperimentJson.Hash(a.Snapshot().Agent), ExperimentJson.Hash(b.Snapshot().Agent));
        }
    }

    [Fact]
    public void AShortRealExperimentRecordsLiveWindowsAndHasAnIdenticalFrozenPrefix()
    {
        string root = Path.Combine(Path.GetTempPath(), "live-expansion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var prototype = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs/research-expanding-life.json"));
            var definition = prototype with
            {
                TrainingSeeds = [11], EvaluationSeeds = [1001], EvaluationSteps = 4, EvaluateEvery = 100,
                LifeMetricWindowSteps = 5,
                Learners = [Learner("learning", -1), Learner("frozen", 8)],
                Phases = [new() { Id = "old", Steps = 8 }, new() { Id = "explore", Steps = 12, Activate = [new("workbench", "1.0.0")] },
                    new() { Id = "need", Steps = 14, Activate = [new("workbench", "2.0.0")] }]
            };
            var runs = definition.Jobs().Select(j => ExperimentRunner.Execute(j, Path.Combine(root, j.Variant))).ToArray();
            Assert.All(runs, r => Assert.Equal("completed", r.Status));
            Assert.All(runs, r => Assert.Equal(34, r.TrainingSteps));
            Assert.All(runs, r => Assert.Equal(34, r.LifeMetrics.Sum(p => p.WindowSteps)));
            Assert.All(runs, r => Assert.Null(r.LifeMetrics.First().Metrics["interaction.pressure-error"].Value));
            Assert.Equal(34, runs[0].Learning.Last().Controller.Updates);
            Assert.Equal(8, runs[1].Learning.Last().Controller.Updates);
            Assert.Equal(runs[0].Learning.Single(p => p.Phase == "old" && p.Step == 8).WorldHash,
                runs[1].Learning.Single(p => p.Phase == "old" && p.Step == 8).WorldHash);
            Assert.Equal(runs[0].Learning.Single(p => p.Phase == "old" && p.Step == 8).ActionPrefixHash,
                runs[1].Learning.Single(p => p.Phase == "old" && p.Step == 8).ActionPrefixHash);
            foreach (var run in runs)
            {
                using var loaded = RuntimeCheckpoint.Open(Path.Combine(root, run.Variant, "policies", "need-final"));
                Assert.Contains("pressure", loaded.Report().Drives.Keys);
                loaded.AdvanceOne(false);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static LearningVariant Learner(string id, long freeze) => new()
    {
        Id = id, Controller = new() { Algorithm = "generic-actor-critic", Parameters = JsonSerializer.SerializeToElement(new
        { features = 8, exploration = "coverage", freezeAfterSteps = freeze }) }
    };

    [Fact]
    public void ScheduleReviewRejectsChangedExposureWithoutCouplingToEvaluationController()
    {
        var definition = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs/research-expanding-life.json"));
        var original = ScenarioSuitability.Describe(definition);
        var changed = ScenarioSuitability.Describe(definition with
        { Phases = definition.Phases.Select((p, i) => i == 0 ? p with { Steps = p.Steps + 1 } : p).ToArray() });
        Assert.Equal(original.Fingerprint, changed.Fingerprint);
        Assert.NotEqual(original.ScheduleFingerprint, changed.ScheduleFingerprint);
        var review = new ScenarioReview(1, original.Fingerprint, true,
            original.Factors.Select(f => new FactorSuitability(f, "requires-adaptation", "passive", "active", "repair")).ToArray(), "fixture")
        { ScheduleFingerprint = original.ScheduleFingerprint };
        ScenarioSuitability.Validate(original, review);
        Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(changed, review));
        var evaluation = ScenarioSuitability.Describe(definition with
        { Phases = definition.Phases.Select((p, i) => i == 0 ? p with { EvaluationRuntime = definition.Runtime with { ControllerSeed = 987 } } : p).ToArray() });
        Assert.Equal(original.Fingerprint, evaluation.Fingerprint);
        Assert.Equal(original.ScheduleFingerprint, evaluation.ScheduleFingerprint);
    }

    [Fact]
    public void UncontrollableNoiseChangesObservationsButCannotBeControlledByActions()
    {
        var options = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs/research-expanding-life.json")).Runtime;
        options = options with { WorldSeed = 2003, Packages = [.. options.Packages, new("workbench", "3.0.0")] };
        using var a = new RuntimeSession(options, new EdgeController(false));
        using var b = new RuntimeSession(options, new EdgeController(true));
        var samples = new HashSet<double>();
        for (int i = 0; i < 60; i++)
        {
            a.AdvanceOne(false); b.AdvanceOne(false);
            double first = a.Snapshot().ModuleState["workbench"].Read<double>("noise");
            double second = b.Snapshot().ModuleState["workbench"].Read<double>("noise");
            Assert.Equal(first, second); samples.Add(first);
        }
        Assert.True(samples.Count > 50);
        Assert.True(Math.Abs(b.Snapshot().ModuleState["workbench"].Read<double>("pressure")) > 0.9);
    }

    [Fact]
    public void LegacyProtocolIdentitySurvivesOptionalLiveMeasurements()
    {
        string root = RuntimeDefaults.RepositoryRoot();
        var definition = ExperimentDefinition.Load(Path.Combine(root, "configs/research-balanced-confirmation.json"));
        using var registration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "research/campaigns/local/studies/balanced-confirmation-01.json")));
        string expected = registration.RootElement.GetProperty("plan").GetProperty("Protocols")[0].GetString()!;
        Assert.Equal(expected, ExperimentRunner.ProtocolHash(definition.Jobs().First()));
    }

    private sealed class EdgeController(bool last) : IAgentController
    {
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => last ? actions[^1] : actions[0];
        public void ConditionsChanged(long revision) { }
        public void Receive(AgentTransition transition) => throw new NotSupportedException();
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
