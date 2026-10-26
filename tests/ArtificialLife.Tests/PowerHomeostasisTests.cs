using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Core;
using ArtificialLife.Modules.DriveMotivation;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class PowerHomeostasisTests
{
    private static RuntimePowerHomeostasis Policy(double exponent)
    {
        var policy = new RuntimePowerHomeostasis();
        policy.Register(new(new("power-homeostasis", "1.0.0", 1, "test.dll", "test")
        { Settings = JsonSerializer.SerializeToElement(new { exponent }) }, new(256), new(256)));
        return policy;
    }
    private static MotivationInput Input(double a, double b) => new(
        [new("a", a, 0, 1, 1), new("b", b, 0, 1, 1)], new Dictionary<string, double>());

    [Fact]
    public void NonlinearityPrefersRepairingTheLargerDeficitAtEqualTotalReduction()
    {
        var before = Input(0.8, 0.2);
        var repairLarge = Input(0.7, 0.2);
        var repairSmall = Input(0.8, 0.1);
        Assert.Equal(Policy(1).Evaluate(before, repairLarge), Policy(1).Evaluate(before, repairSmall), 12);
        Assert.True(Policy(4).Evaluate(before, repairLarge) > Policy(4).Evaluate(before, repairSmall));
        Assert.Equal(0.2, Policy(4).Evaluate(Input(0.4, 0.4), Input(0.4, 0.4)), 12);
        Assert.Equal(1, Policy(4).Evaluate(Input(0, 0), Input(0, 0)), 12);
        Assert.Throws<InvalidDataException>(() => Policy(0));
        Assert.Throws<InvalidDataException>(() => Policy(17));
    }

    [Fact]
    public void PowerOneMatchesLegacyRewardsAndFixedActionsKeepEcologyPhysicsIdentical()
    {
        var definition = ExperimentDefinition.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs", "research-ecology-v2.json"));
        var baseline = definition.Jobs().First().Runtime;
        RuntimeOptions Power(double exponent) => baseline with
        {
            Packages = baseline.Packages.Select(p => p.Id == "homeostasis" ? new PackageReference("power-homeostasis", "1.0.0") : p).ToArray(),
            Settings = baseline.Settings.Where(p => p.Key != "homeostasis").Append(new KeyValuePair<string, JsonElement>(
                "power-homeostasis", JsonSerializer.SerializeToElement(new { exponent }))).ToDictionary(p => p.Key, p => p.Value)
        };
        using var legacy = new RuntimeSession(baseline, new FixedController());
        using var linear = new RuntimeSession(Power(1), new FixedController());
        using var nonlinear = new RuntimeSession(Power(4), new FixedController());
        static RuntimeFrame Physical(RuntimeFrame frame) => frame with
        {
            ModuleState = frame.ModuleState.Where(p => !new[] { "homeostasis", "rng:homeostasis", "power-homeostasis", "rng:power-homeostasis" }.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value)
        };
        bool rewardsDiffer = false;
        for (int i = 0; i < 600; i++)
        {
            var a = legacy.AdvanceOne(false);
            var b = linear.AdvanceOne(false);
            var c = nonlinear.AdvanceOne(false);
            Assert.Equal(a.Reward, b.Reward, 12);
            Assert.Equal(Physical(legacy.Snapshot()), Physical(linear.Snapshot()));
            Assert.Equal(Physical(legacy.Snapshot()), Physical(nonlinear.Snapshot()));
            Assert.Equal(a.Homeostasis, c.Homeostasis);
            rewardsDiffer |= Math.Abs(a.Reward - c.Reward) > 1e-6;
        }
        Assert.True(rewardsDiffer);
    }

    private sealed class FixedController : IAgentController
    {
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => actions[0];
        public void Receive(AgentTransition transition) => throw new InvalidOperationException();
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
