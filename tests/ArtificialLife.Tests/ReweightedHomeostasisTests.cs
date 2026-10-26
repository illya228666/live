using System.Text.Json;
using ArtificialLife.Core;
using ArtificialLife.Modules.DriveMotivation;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class ReweightedHomeostasisTests
{
    private static RuntimeReweightedHomeostasis Policy(Dictionary<string, double> weights)
    {
        var policy = new RuntimeReweightedHomeostasis();
        policy.Register(new(new("reweighted-homeostasis", "1.0.0", 1, "test.dll", "test")
        { Settings = JsonSerializer.SerializeToElement(new { driveWeights = weights }) }, new(256), new(256)));
        return policy;
    }
    [Fact]
    public void IdentityWeightsPreserveOriginalRewardAndSafetyWeightChangesOnlyScalarization()
    {
        var before = new MotivationInput([new("hunger", .6, 0, 1, 1), new("safety", .3, 0, 1, 1)], new Dictionary<string, double>());
        var after = before with { Drives = [new("hunger", .6, 0, 1, 1), new("safety", .1, 0, 1, 1)] };
        Assert.Equal(new RuntimePowerHomeostasis().Evaluate(before, after), Policy([]).Evaluate(before, after), 12);
        Assert.Equal((1 - 2 * (.6 + 2 * .1) / 3) + .2 * (2 * .2 / 3), Policy(new() { ["safety"] = 2 }).Evaluate(before, after), 12);
        Assert.Equal(.1, after.Drives[1].Current);
        Assert.Equal(1, after.Drives[1].Weight);
        Assert.Throws<InvalidDataException>(() => Policy(new() { ["safety"] = 0 }));
    }
}
