using ArtificialLife.Core;

namespace ArtificialLife.Application;

internal sealed class SignalDiagnosticEvaluator : IBehaviorEvaluator
{
    private int samples, correct;
    public void Observe(RuntimeFrame frame, HomeostasisReport state)
    {
        if (!frame.ModuleState.TryGetValue("signal-diagnostic", out var data)) return;
        samples++; if (data.Read<bool>("correct")) correct++;
    }
    public IReadOnlyDictionary<string, BehaviorMetric> Complete() => new Dictionary<string, BehaviorMetric>
    {
        ["signal.success"] = new(samples == 0 ? null : (double)correct / samples, samples, "fraction", true,
            "Observed action effect matches the current cue; measured even without a need or informative reward.")
    };
}
