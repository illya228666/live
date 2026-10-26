using ArtificialLife.Core;

namespace ArtificialLife.Brain;

/// <summary>Capped zeta(2) action repeats, with a stationary occupancy correction.</summary>
public sealed class PersistentExploration
{
    private readonly Random random;
    private readonly double[] cdf;
    private ActionCandidate? held;
    private int remaining;
    public double MeanDuration { get; }
    public long Choices { get; private set; }
    public long ExploratoryActions { get; private set; }
    public long Bursts { get; private set; }
    public long IllegalTerminations { get; private set; }

    public PersistentExploration(Random random, int maximumDuration)
    {
        if (maximumDuration is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        this.random = random;
        cdf = new double[maximumDuration];
        double mass = 0, firstMoment = 0;
        for (int n = 1; n <= maximumDuration; n++) { mass += 1.0 / (n * n); firstMoment += 1.0 / n; cdf[n - 1] = mass; }
        for (int i = 0; i < cdf.Length; i++) cdf[i] /= mass;
        cdf[^1] = 1;
        MeanDuration = firstMoment / mass;
    }

    public ActionCandidate Choose(ActionCandidate[] actions, double epsilon, Func<ActionCandidate> greedy)
    {
        if (actions.Length == 0) throw new ArgumentException("At least one legal action is required.");
        if (!double.IsFinite(epsilon) || epsilon is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(epsilon));
        Choices++;
        if (remaining > 0 && held is not null)
        {
            var legal = actions.FirstOrDefault(a => a.Type == held.Type && a.Parameters.SequenceEqual(held.Parameters));
            if (legal is not null) { remaining--; ExploratoryActions++; return legal; }
            IllegalTerminations++;
            Reset();
        }
        // With constant epsilon and no forced interruption, this gives fraction epsilon
        // of primitive steps in exploration, rather than increasing it with burst length.
        double startProbability = epsilon / (MeanDuration - epsilon * (MeanDuration - 1));
        if (random.NextDouble() >= startProbability) return greedy();
        var selected = actions[random.Next(actions.Length)];
        int duration = 1;
        if (cdf.Length > 1)
        {
            double sample = random.NextDouble();
            while (sample > cdf[duration - 1]) duration++;
        }
        held = selected with { Parameters = (float[])selected.Parameters.Clone() };
        remaining = duration - 1;
        Bursts++; ExploratoryActions++;
        return selected;
    }

    public void Reset() { held = null; remaining = 0; }
}
