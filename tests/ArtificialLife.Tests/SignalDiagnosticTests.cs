using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class SignalDiagnosticTests
{
    private static RuntimeOptions Options(string version) => new()
    {
        WorldSeed = 1741, PackageRoot = Path.Combine(RuntimeDefaults.RepositoryRoot(), "artifacts/packages"),
        Packages = [new("homeostasis", "1.0.0"), new("signal-diagnostic", version)],
        Settings = new() { ["homeostasis"] = JsonSerializer.SerializeToElement(new { improvementWeight = 0 }) }
    };
    [Fact]
    public void FreshAndExperiencedAgentsFaceTheSamePostChangeExogenousSignals()
    {
        using var late = new RuntimeSession(Options("1.0.0"), new EdgeController(false));
        for (int i = 0; i < 50; i++) late.AdvanceOne(false);
        Assert.True(late.Activate(new("changed", [new("signal-diagnostic", "6.0.0")])).Accepted);
        using var fresh = new RuntimeSession(Options("6.0.0"), new EdgeController(true));
        for (int i = 0; i < 100; i++)
        {
            var a = late.Snapshot().ModuleState["signal-diagnostic"];
            var b = fresh.Snapshot().ModuleState["signal-diagnostic"];
            Assert.Equal(a.Read<double>("cue"), b.Read<double>("cue"));
            Assert.Equal(a.Read<double[]>("extras"), b.Read<double[]>("extras"));
            late.AdvanceOne(false); fresh.AdvanceOne(false);
        }
        Assert.Equal(150, late.Snapshot().Age); Assert.Equal(100, fresh.Snapshot().Age);
    }
    [Fact]
    public void NeutralInteractionStillHasObservableEffectsWhileRewardIsUninformative()
    {
        using var runtime = new RuntimeSession(Options("1.1.0"), new EdgeController(false));
        var effects = new HashSet<double>();
        for (int i = 0; i < 100; i++)
        {
            var sample = runtime.AdvanceOne(false);
            Assert.Empty(sample.Homeostasis.Drives);
            Assert.Equal(1, sample.Reward);
            effects.Add(runtime.Snapshot().ModuleState["signal-diagnostic"].Read<double>("effect"));
        }
        Assert.Equal(2, effects.Count);
    }
    private sealed class EdgeController(bool last) : IAgentController
    {
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => last ? actions[^1] : actions[0];
        public void Receive(AgentTransition transition) => throw new NotSupportedException();
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
