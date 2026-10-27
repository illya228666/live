using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Challenge-specific measurement is outside the controller and has no reward input.</summary>
internal sealed class InteractionBehaviorEvaluator : IBehaviorEvaluator
{
    private int samples, acted, positive, negative, outside;
    private double error, effective;
    public void Observe(RuntimeFrame frame, HomeostasisReport state)
    {
        if (!frame.ModuleState.TryGetValue("workbench", out var data)) return;
        double pressure = Math.Abs(data.Read<double>("pressure"));
        error += pressure; samples++;
        if (pressure > 0.4) outside++;
        if (!data.Read<bool>("acted")) return;
        acted++;
        double effect = data.Read<double>("lastEffect");
        if (effect > 0) positive++;
        if (effect < 0) negative++;
        effective += Math.Abs(effect);
    }
    public IReadOnlyDictionary<string, BehaviorMetric> Complete() => new Dictionary<string, BehaviorMetric>
    {
        ["interaction.pressure-error"] = new(samples == 0 ? null : error / samples, samples, "ratio", false, "Observable pressure error, including before it becomes a need."),
        ["interaction.pressure-outside"] = new(samples == 0 ? null : (double)outside / samples, samples, "fraction", false, "Pressure magnitude above0.4; evaluation threshold, not reward."),
        ["interaction.command-fraction"] = new(samples == 0 ? null : (double)acted / samples, samples, "fraction", true, "Executed commands; activity alone is not competence."),
        ["interaction.effect-magnitude"] = new(acted == 0 ? null : effective / acted, acted, "ratio", true, "Mean observable physical change per command; not prediction or learning."),
        ["interaction.both-directions"] = new(samples == 0 ? null : positive > 0 && negative > 0 ? 1 : 0, samples, "boolean", true, "Both observable effect directions occurred; random behavior can also pass.")
    };
}
