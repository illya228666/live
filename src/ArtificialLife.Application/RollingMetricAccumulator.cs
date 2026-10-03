namespace ArtificialLife.Application;

/// <summary>Bounded rolling homeostasis statistics, sharing evaluation definitions.</summary>
public sealed class RollingMetricAccumulator
{
    private readonly Queue<(HomeostasisReport Report, double Reward)> samples = new();
    private readonly MetricAccumulator metrics = new();
    private readonly int capacity;

    public RollingMetricAccumulator(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public void Add(HomeostasisReport report, double reward)
    {
        samples.Enqueue((report, reward));
        metrics.Add(report, reward);
        if (samples.Count > capacity)
        {
            var old = samples.Dequeue();
            metrics.Remove(old.Report, old.Reward);
        }
    }

    public EvaluationMetrics Metrics() => metrics.Metrics();
}
