using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class ScenarioSuitabilityTests
{
    private static ExperimentDefinition Baseline() => ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs", "research-baseline.json"));
    private static TemperatureOptions Thermal(ExperimentDefinition definition) => definition.Runtime.Settings["temperature"].Deserialize<TemperatureOptions>(PackageCatalog.Json)!;
    private static ScenarioReview Review(ScenarioDescription description) => new(1, description.Fingerprint, true,
        [new("temperature", "requires-adaptation", "stationary bound and runtime", "local feedback and held-out seeds", "repair parameters and repeat audit")], "evidence.json");

    [Fact]
    public void NewParametersExcludePermanentComfortAtEveryFixedPositionAndAllowTracking()
    {
        var definition = Baseline();
        var analysis = TemperatureScenarioAudit.Analyze(Thermal(definition), definition.Runtime.World, 1);
        Assert.Equal(30.6, analysis.FarEquilibrium, 10);
        Assert.InRange(analysis.MinimumUnavoidablePeakError, 3.8, 3.9);
        Assert.InRange(analysis.BestComfortFraction, 0.40, 0.43);
        Assert.True(analysis.BestMae > 2);
        Assert.InRange(analysis.MinimumComfortRadius, 6, 8);
        Assert.InRange(analysis.MaximumComfortRadius, 12, 14);
        Assert.True(analysis.MaximumRequiredRadialSpeed < definition.Runtime.World.MovementSpeed / 10);
        var old = Thermal(definition) with { Ambient = 34.1, FireStrength = 40, FireRadius = 3, OscillationAmplitude = 0.15 };
        var trivial = TemperatureScenarioAudit.Analyze(old, definition.Runtime.World, 1);
        Assert.True(trivial.MinimumUnavoidablePeakError < 1);
        Assert.True(trivial.BestComfortFraction > 0.99);
        var constant = TemperatureScenarioAudit.Analyze(Thermal(definition) with { OscillationAmplitude = 0 }, definition.Runtime.World, 1);
        Assert.Equal(0, constant.MinimumUnavoidablePeakError);
        Assert.Equal(1, constant.BestComfortFraction);
    }

    [Fact]
    public void RealPhysicsDistinguishesStationaryRandomAndLocalFeedbackWithoutLearning()
    {
        var definition = Baseline() with { EvaluationSteps = 1920 };
        var protocol = new ThermalAuditProtocol { Seeds = [111, 211, 307] };
        var report = TemperatureScenarioAudit.Run(definition, protocol);
        Assert.True(report.Passed, string.Join("; ", report.Failures));
        Assert.Equal(12, report.Controls.Length);
        Assert.All(report.Controls.Where(c => c.Policy == "local-feedback"), c =>
        {
            Assert.True(c.ComfortFraction >= 0.90); Assert.True(c.Mae <= 0.5);
            Assert.True(c.TailRadiusRange > 4); Assert.True(c.MovingFraction > 0.1);
        });
        Assert.All(report.Controls.Where(c => c.Policy is "stay" or "park-best"), c => Assert.True(c.TailComfortFraction <= 0.65));
        var repeat = TemperatureScenarioAudit.Run(definition, protocol);
        Assert.Equal(report.Controls, repeat.Controls);
        Assert.All(report.Controls, c => Assert.Equal(64, c.ActionHash.Length));
    }

    [Fact]
    public void ReviewMatchesPhysicsNotMotivationLearningParametersOrTrainingBudget()
    {
        var definition = Baseline(); var description = ScenarioSuitability.Describe(definition);
        var revised = definition with
        {
            TrainingSeeds = [17, 23], Phases = [definition.Phases[0] with { Steps = 1000 }],
            Motivations = [definition.Motivations[0] with { Settings = JsonSerializer.SerializeToElement(new { improvementWeight = 0 }) }],
            Learners = [definition.Learners[0] with { Controller = new() { Parameters = JsonSerializer.SerializeToElement(new { network = new { hiddenSize = 16 } }) } }]
        };
        Assert.Equal(description.Fingerprint, ScenarioSuitability.Describe(revised).Fingerprint);
        var review = Review(description); ScenarioSuitability.Validate(description, review);
        var changed = definition with { Runtime = definition.Runtime with { World = definition.Runtime.World with { Width = 41 } } };
        Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(ScenarioSuitability.Describe(changed), review));
        Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(description, review with { Passed = false }));
        Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(description, review with
        { Factors = [review.Factors[0] with { Verdict = "trivial" }] }));
    }

    [Fact]
    public void EveryAdditionalFactorNeedsItsOwnPassingEvidenceRatherThanACaveat()
    {
        var definition = Baseline(); var review = Review(ScenarioSuitability.Describe(definition));
        var joint = definition with { Phases = [definition.Phases[0], new() { Id = "food", Steps = 10, Activate = [new("hunger", "1.0.0"), new("food", "1.0.0")] }] };
        var description = ScenarioSuitability.Describe(joint);
        Assert.Contains("hunger", description.Factors);
        review = review with { Fingerprint = description.Fingerprint };
        Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(description, review));
        var repaired = review with { Factors = [review.Factors[0], new("hunger", "requires-adaptation", "passive test", "active test", "repair and recheck")] };
        ScenarioSuitability.Validate(description, repaired);
        Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(description, repaired with
        { Factors = [review.Factors[0], repaired.Factors[1] with { Verdict = "documented-but-unresolved" }] }));
    }

    [Fact]
    public void EvidenceChecksumPreventsReusingChangedOrMissingResults()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArtificialLife-suitability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definition = Baseline();
            string config = Path.Combine(root, "config.json"), evidence = Path.Combine(root, "evidence.json"), path = Path.Combine(root, "review.json");
            ExperimentJson.Atomic(config, definition);
            File.WriteAllText(evidence, "{\"check\":\"passed\"}");
            var review = Review(ScenarioSuitability.Describe(definition)) with
            { EvidenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidence))) };
            ExperimentJson.Atomic(path, review);
            Assert.Equal(0, ScenarioSuitability.Command(["--config", config, "--review", path], true));
            File.AppendAllText(evidence, " ");
            Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Command(["--config", config, "--review", path], true));
        }
        finally { Directory.Delete(root, true); }
    }
}
