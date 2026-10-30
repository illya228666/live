using System.Text.Json;
using ArtificialLife.Application;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class EcologyScenarioTests
{
    private static ExperimentDefinition Definition(string factor = "ecology") => ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs", $"research-{factor}-v2.json"));

    [Fact]
    public void PreservedThermalEvidenceStillPassesItsActualRegisteredChecksum()
    {
        string root = RuntimeDefaults.RepositoryRoot();
        ValidateArchivedReview(Path.Combine(root, "configs", "research-baseline.json"), Path.Combine(root, "research", "scenarios", "temperature-v2-review.json"));
    }

    [Fact]
    public void AuditControlsNeverTrainAndAreIndependentOfRewardAndControllerSelection()
    {
        var definition = Definition();
        var a = EcologyScenarioAudit.Evaluate(definition.Runtime, definition.Behavior, 0, 111, "joint", 1000, 250);
        var settings = definition.Runtime.Settings.ToDictionary(p => p.Key, p => p.Value);
        settings["homeostasis"] = JsonSerializer.SerializeToElement(new { improvementWeight = 1 });
        var options = definition.Runtime with { Settings = settings, Controller = new() { Algorithm = "unregistered-never-used" } };
        var b = EcologyScenarioAudit.Evaluate(options, definition.Behavior, 0, 111, "joint", 1000, 250);
        Assert.Equal(a.ActionHash, b.ActionHash); Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(JsonSerializer.Serialize(a.Metrics), JsonSerializer.Serialize(b.Metrics));
        Assert.Equal(JsonSerializer.Serialize(a.Blocks), JsonSerializer.Serialize(b.Blocks));
        Assert.True(a.Metrics["food.consumed"].Value > 0);
        var otherSeed = EcologyScenarioAudit.Evaluate(definition.Runtime, definition.Behavior, 0, 211, "joint", 1000, 250);
        Assert.NotEqual(a.StateHash, otherSeed.StateHash);
    }

    [Fact]
    public void MissingFactorsAreUnavailableAndSafetyIsExplicitlyAProxy()
    {
        var hunger = Definition("hunger");
        var result = EcologyScenarioAudit.Evaluate(hunger.Runtime, hunger.Behavior, 0, 111, "forage", 2000, 1000);
        Assert.Null(result.Metrics["wildlife.contact-proxy"].Value);
        Assert.Equal(0, result.Metrics["wildlife.contact-proxy"].Samples);
        Assert.Null(result.Metrics["temperature.mae"].Value);
        Assert.True(result.Metrics["wildlife.contact-proxy"].Proxy);
        Assert.InRange(result.Metrics["hunger.mean"].Value!.Value, 0, 0.15);
        Assert.True(result.Metrics["food.consumed"].Value > 5);
        var stationary = EcologyScenarioAudit.Evaluate(hunger.Runtime, hunger.Behavior, 0, 111, "stay", 2000, 1000);
        Assert.Equal(0, stationary.Metrics["food.consumed"].Value);
        Assert.True(stationary.Blocks[1]["hunger.mean"] > 0.99);
    }

    [Fact]
    public void IncompleteOrShortAuditCannotAdmitAScenarioAndInvalidThresholdsAreRejected()
    {
        var definition = Definition("hunger") with { EvaluationSteps = 30 };
        var protocol = new EcologyAuditProtocol { Policies = ["joint"] };
        var report = EcologyScenarioAudit.Run(definition, protocol);
        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Contains("full set", StringComparison.Ordinal));
        Assert.Contains(report.Failures, f => f.Contains("10000", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => EcologyScenarioAudit.Run(definition, protocol with { MaximumProximity = double.NaN }));
        Assert.Throws<InvalidDataException>(() => EcologyScenarioAudit.Run(definition, protocol with { MinimumPassiveProximity = 0.01 }));
    }

    [Fact]
    public void IndividualAndJointConfigurationsRetainIdenticalCommonPhysics()
    {
        var joint = Definition(); var hunger = Definition("hunger"); var safety = Definition("safety");
        var thermal = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs", "research-baseline.json"));
        Assert.Equal(thermal.Runtime.World, joint.Runtime.World);
        Assert.Equal(ExperimentJson.Hash(thermal.Runtime.Settings["temperature"]), ExperimentJson.Hash(joint.Runtime.Settings["temperature"]));
        foreach (string key in new[] { "hunger", "food" }) Assert.Equal(ExperimentJson.Hash(joint.Runtime.Settings[key]), ExperimentJson.Hash(hunger.Runtime.Settings[key]));
        Assert.Equal(ExperimentJson.Hash(joint.Runtime.Settings["wildlife"]), ExperimentJson.Hash(safety.Runtime.Settings["wildlife"]));
        Assert.Contains(joint.Runtime.Packages, p => p.Id == "wildlife" && p.Version == "1.1.0");
        Assert.Empty(joint.EvaluationSeeds.Intersect(new[] { 61001, 62003, 63011, 64009, 65003 }));
    }

    [Theory]
    [InlineData("hunger")] [InlineData("safety")] [InlineData("ecology")]
    public void RegisteredEvidenceMatchesTheCurrentRuntimeAndAReplayedHeldOutControl(string factor)
    {
        string root = RuntimeDefaults.RepositoryRoot(); var definition = Definition(factor);
        string review = Path.Combine(root, "research", "scenarios", $"{factor}-v2-review.json");
        ValidateArchivedReview(Path.Combine(root, "configs", $"research-{factor}-v2.json"), review);
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "research", "scenarios", $"{factor}-v2-results.json")));
        var expected = evidence.RootElement.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("seed").GetInt32() == 61001 && c.GetProperty("policy").GetString() == "joint");
        var actual = EcologyScenarioAudit.Evaluate(definition.Runtime, definition.Behavior, 0, 61001, "joint", 12000, 1000);
        Assert.Equal(expected.GetProperty("actionHash").GetString(), actual.ActionHash);
        Assert.Equal(expected.GetProperty("stateHash").GetString(), actual.StateHash);
        foreach (var metric in expected.GetProperty("metrics").EnumerateObject())
            if (metric.Value.ValueKind == JsonValueKind.Null) Assert.Null(actual.Metrics[metric.Name].Value);
            else Assert.InRange(Math.Abs(actual.Metrics[metric.Name].Value!.Value - metric.Value.GetDouble()), 0, 1e-9);
    }
    private static void ValidateArchivedReview(string config, string reviewPath)
    {
        var review = ExperimentJson.Read<ScenarioReview>(reviewPath);
        string evidence = Path.GetFullPath(review.Evidence, Path.GetDirectoryName(reviewPath)!);
        Assert.Equal(review.EvidenceSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evidence))));
        var current = ScenarioSuitability.Describe(ExperimentDefinition.Load(config));
        if (current.Fingerprint == review.Fingerprint) Assert.Equal(0, ScenarioSuitability.Command(["--config", config, "--review", reviewPath], true));
        else Assert.Throws<InvalidDataException>(() => ScenarioSuitability.Validate(current, review));
    }

}
