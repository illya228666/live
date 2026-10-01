using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

internal readonly record struct ThermalMetricSample(double Error, double Reward, double Body, bool Comfortable)
{
    internal static ThermalMetricSample Measure(TemperatureOptions options, double body, double reward)
    {
        double error = Math.Abs(body - options.Target);
        return new ThermalMetricSample(error, reward, body, error <= options.ComfortHalfWidth);
    }
}

/// <summary>O(1) rolling statistics with the same error/comfort definitions as evaluation.</summary>
public sealed class RollingMetricAccumulator
{
    private readonly TemperatureOptions options;
    private readonly Queue<ThermalMetricSample> samples = new();
    private readonly int capacity;
    private double error;
    private double reward;
    private double body;
    private int comfortable;

    public RollingMetricAccumulator(TemperatureOptions options, int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        this.options = options;
        this.capacity = capacity;
    }

    public void Add(double bodyTemperature, double stepReward)
    {
        ThermalMetricSample sample = ThermalMetricSample.Measure(options, bodyTemperature, stepReward);
        samples.Enqueue(sample);
        Accumulate(sample, 1);
        if (samples.Count > capacity)
        {
            Accumulate(samples.Dequeue(), -1);
        }
    }

    public EvaluationMetrics Metrics()
    {
        int count = samples.Count;
        return count == 0 ? new(0, 0, 0, 0, 0) : new(count, Math.Max(0, error / count),
            100.0 * comfortable / count, reward / count, body / count);
    }

    private void Accumulate(ThermalMetricSample sample, int sign)
    {
        error += sign * sample.Error;
        reward += sign * sample.Reward;
        body += sign * sample.Body;
        comfortable += sample.Comfortable ? sign : 0;
    }
}
