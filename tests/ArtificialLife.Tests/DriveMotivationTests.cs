using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Core;
using ArtificialLife.Modules.DriveMotivation;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class DriveMotivationTests
{
    private static MotivationInput Input(params DriveState[] drives) => new(drives, new Dictionary<string, double>());
    private static DriveState Need(double value, double weight = 1) => new("need", value, 0, 1, weight);
    private static RuntimeDriveLevel Level(double gamma = 0.97)
    {
        var policy = new RuntimeDriveLevel();
        policy.Register(new(new("drive-level", "1.0.0", 1, "test.dll", "test")
        { Settings = JsonSerializer.SerializeToElement(new { discountFactor = gamma }) }, new(256), new(256)));
        return policy;
    }

    [Fact]
    public void DistanceUsesPhysicalNormalizationUnweightedL2WithoutClippingOrDimensionAveraging()
    {
        DriveState[] drives = [new("a", 16, 10, 2, 0), new("b", -8, 0, 2, 100)];
        Assert.Equal(5, NormalizedDriveDistance.Measure(drives), 12);
        Assert.Equal(5, NormalizedDriveDistance.Measure(drives.Reverse().Select(d => d with { Weight = 7 })), 12);
        Assert.Equal(0, NormalizedDriveDistance.Measure([]));
        Assert.Equal(3, NormalizedDriveDistance.Measure([new("a", 4, 10, 2, 1)]), 12);
        Assert.Equal(1e200, NormalizedDriveDistance.Measure([Need(1e200)]));
    }

    [Theory]
    [InlineData(3, 1, 2, -0.03)]
    [InlineData(1, 3, -2, -0.09)]
    [InlineData(3, 3, 0, -0.09)]
    [InlineData(-3, -1, 2, -0.03)]
    [InlineData(0, 0, 0, 0)]
    public void RewardsMatchAnalyticalCases(double before, double after, double reduction, double level)
    {
        Assert.Equal(reduction, new RuntimeDriveReduction().Evaluate(Input(Need(before)), Input(Need(after))), 12);
        Assert.Equal(level, Level().Evaluate(Input(Need(before)), Input(Need(after))), 12);
    }

    [Fact]
    public void ConflictingNeedsUseOneDistanceAndReturnScalingHasTheExpectedIdentity()
    {
        var before = Input(new("a", 3, 0, 1, 99), new("b", 4, 0, 1, 0));
        var after = Input(new("a", 0, 0, 1, 99), new("b", 6, 0, 1, 0));
        Assert.Equal(-1, new RuntimeDriveReduction().Evaluate(before, after), 12);
        Assert.Equal(-0.18, Level().Evaluate(before, after), 12);
        double[] trajectory = [2, 1, 3, 0.5];
        const double gamma = 0.97;
        double reduction = 0, level = 0;
        for (int t = 0; t < trajectory.Length - 1; t++)
        {
            reduction += Math.Pow(gamma, t) * new RuntimeDriveReduction().Evaluate(Input(Need(trajectory[t])), Input(Need(trajectory[t + 1])));
            level += Math.Pow(gamma, t) * Level().Evaluate(Input(Need(trajectory[t])), Input(Need(trajectory[t + 1])));
        }
        // Finite trajectories retain this boundary term; evaluation horizons are not training terminals.
        Assert.Equal(trajectory[0] + level - Math.Pow(gamma, trajectory.Length - 1) * trajectory[^1], reduction, 12);
        Assert.Equal(0, new RuntimeDriveReduction().Evaluate(Input(), Input()));
        Assert.Equal(0, Level().Evaluate(Input(), Input()));
    }

    [Fact]
    public void InvalidPhysicalValuesAndDiscountFactorsAreRejected()
    {
        foreach (var drive in new[] { Need(double.NaN), Need(double.PositiveInfinity), Need(1) with { Scale = 0 },
            Need(1) with { Scale = -1 }, Need(1) with { Scale = double.NaN }, Need(1) with { Target = double.PositiveInfinity } })
        {
            Assert.Throws<InvalidDataException>(() => NormalizedDriveDistance.Measure([drive]));
            Assert.Throws<InvalidDataException>(() => Level().Evaluate(Input(drive), Input()));
        }
        Assert.Throws<InvalidDataException>(() => Level(0));
        Assert.Throws<InvalidDataException>(() => Level(1));
        Assert.Throws<InvalidDataException>(() => Level(-0.1));
    }

    [Fact]
    public void StudyConfigurationsLockTheSameLearnerWorldAndDiscountAndHoldOutEvaluationSeeds()
    {
        string root = RuntimeDefaults.RepositoryRoot();
        var full = ExperimentDefinition.Load(Path.Combine(root, "configs", "drive-motivation.json"));
        var pilot = ExperimentDefinition.Load(Path.Combine(root, "configs", "drive-motivation-pilot.json"));
        Assert.Equal(15, full.Jobs().Count()); Assert.Equal(3, pilot.Jobs().Count());
        Assert.Equal(300000, full.Phases.Sum(p => p.Steps));
        Assert.Equal(6000, pilot.Phases.Sum(p => p.Steps));
        Assert.Equal(ExperimentJson.Hash(full.Runtime), ExperimentJson.Hash(pilot.Runtime));
        Assert.Equal(ExperimentJson.Hash(full.Learners), ExperimentJson.Hash(pilot.Learners));
        Assert.Empty(full.EvaluationSeeds.Intersect(pilot.EvaluationSeeds));
        Assert.Equal(3, full.Motivations.Length);
        Assert.Single(full.Jobs().Select(ExperimentRunner.ProtocolHash).Distinct());
        foreach (var job in full.Jobs().Where(j => j.Variant.StartsWith("drive-level--", StringComparison.Ordinal)))
            Assert.Equal(job.Runtime.Learning.Gamma, new RuntimeSettings(job.Runtime.Settings["drive-level"]).Number("discountFactor", -1));
    }

    [Fact]
    public void FixedActionsKeepPhysicsAndAllBehaviorMetricsIdenticalAcrossRealPackages()
    {
        var definition = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs", "drive-motivation-pilot.json"));
        var jobs = definition.Jobs().ToArray();
        var worlds = jobs.Select(j => new RuntimeSession(j.Runtime, new FixedController())).ToArray();
        static RuntimeFrame PhysicalFrame(RuntimeFrame frame) => frame with
        {
            ModuleState = frame.ModuleState.Where(p => p.Key is not ("homeostasis" or "rng:homeostasis" or
                "drive-reduction" or "rng:drive-reduction" or "drive-level" or "rng:drive-level"))
                .ToDictionary(p => p.Key, p => p.Value)
        };
        static double Distance(HomeostasisReport report) => Math.Sqrt(report.Drives.Values.Sum(d => d.NormalizedError * d.NormalizedError));
        try
        {
            var evaluators = worlds.Select(_ => BehaviorEvaluators.Create(definition.Behavior)).ToArray();
            var rewards = new double[worlds.Length];
            for (int step = 0; step < 180; step++)
            {
                if (step == 60)
                {
                    foreach (var world in worlds)
                    {
                        long age = world.Snapshot().Age;
                        double distance = Distance(world.Report());
                        Assert.True(world.Activate(new("test-hunger", definition.Phases[1].Activate)).Accepted);
                        Assert.Equal(age, world.Snapshot().Age);
                        Assert.Equal(distance, Distance(world.Report()), 12); // a new zero-valued need introduces no jump
                    }
                }
                for (int i = 0; i < worlds.Length; i++)
                {
                    double before = Distance(worlds[i].Report());
                    var sample = worlds[i].AdvanceOne(false); rewards[i] += sample.Reward;
                    if (i == 1) Assert.Equal(before - Distance(sample.Homeostasis), sample.Reward, 12);
                    if (i == 2) Assert.Equal(-0.03 * Distance(sample.Homeostasis), sample.Reward, 12);
                    foreach (var evaluator in evaluators[i]) evaluator.Observe(worlds[i].Snapshot(), sample.Homeostasis);
                }
                foreach (var other in worlds.Skip(1))
                {
                    Assert.Equal(PhysicalFrame(worlds[0].Snapshot()), PhysicalFrame(other.Snapshot()));
                    Assert.Equal(worlds[0].Report(), other.Report());
                }
            }
            Assert.Equal(3, rewards.Distinct().Count());
            var expected = BehaviorEvaluators.Complete(evaluators[0]).OrderBy(p => p.Key).ToArray();
            foreach (var other in evaluators.Skip(1)) Assert.Equal(expected, BehaviorEvaluators.Complete(other).OrderBy(p => p.Key));
        }
        finally { foreach (var world in worlds) world.Dispose(); }
    }

    [Fact]
    public void StabilityMeasuresKnownSequencesAndDoesNotBridgeStationarySteps()
    {
        var evaluator = BehaviorEvaluators.Create(new() { Evaluators = [new() { Id = "stability-v1" }] }).Single();
        double[] temperatures = [36.6, 38.6, 38.6, 36.6, 34.6, 34.6];
        double[] hungers = [0.9, 0.9, 0.1, 0.9, 0.9, 0.9];
        double[] x = [0, 1, 0, 0, 1, 2];
        for (int i = 0; i < x.Length; i++) evaluator.Observe(Frame(i, x[i], temperatures[i], hungers[i], i < 3 ? 1 : 20), HomeostasisReport.Measure([]));
        var metrics = evaluator.Complete();
        Assert.Equal(Math.Sqrt(16.0 / 6), metrics["temperature.temporal-sd"].Value!.Value, 12);
        Assert.Equal(Math.Sqrt(0.64 * 5 / 36), metrics["hunger.temporal-sd"].Value!.Value, 12);
        Assert.Equal(2, metrics["temperature.outside-longest"].Value);
        Assert.Equal(3, metrics["hunger.above-longest"].Value);
        Assert.Equal(3, metrics["wildlife.proximity-longest"].Value);
        Assert.Equal(0.5, metrics["movement.reversal"].Value);
        Assert.Equal(2, metrics["movement.reversal"].Samples);
        Assert.True(metrics["wildlife.proximity-longest"].Proxy);
        Assert.True(metrics["movement.reversal"].Proxy);
        var absent = BehaviorEvaluators.Create(new() { Evaluators = [new() { Id = "stability-v1" }] }).Single();
        absent.Observe(Frame(0, 0, null, null, null), HomeostasisReport.Measure([]));
        Assert.All(absent.Complete().Values, m => { Assert.Null(m.Value); Assert.Equal(0, m.Samples); });
    }

    private static RuntimeFrame Frame(int age, double x, double? temperature, double? hunger, double? distance)
    {
        static ComponentSnapshot Component(string key, double value) => new(1, new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(value) });
        var components = new Dictionary<string, ComponentSnapshot>();
        if (temperature is double t) components["temperature.body"] = Component("temperature", t);
        if (hunger is double h) components["hunger.body"] = Component("level", h);
        EntitySnapshot[] entities = distance is double d ? [new(2, new(x + d, 0), new Dictionary<string, ComponentSnapshot> { ["wildlife.boar"] = new(1, new Dictionary<string, JsonElement>()) })] : [];
        return new(40, 40, age, age, 1, new(1, new(x, 0), components), entities, new Dictionary<string, ComponentSnapshot>());
    }
    private sealed class FixedController : IAgentController
    {
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => actions[0];
        public void Receive(AgentTransition transition) => throw new InvalidOperationException("Inference only.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
