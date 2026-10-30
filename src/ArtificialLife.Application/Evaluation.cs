using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record DriveMetrics(int Samples, double MeanCurrent, double MeanTarget, double MeanNormalizedError,
    double MeanSatisfaction, double MeanWeight);
public sealed record EvaluationMetrics(int Steps, double MeanBalance, double MeanReward, ReportValues<DriveMetrics> Drives);
public sealed record SeedEvaluation(int Seed, EvaluationMetrics Random, EvaluationMetrics Untrained, EvaluationMetrics Trained);
public sealed record EvaluationReport(DateTimeOffset CreatedUtc, int TrainingSteps, double TrainingSeconds,
    EvaluationMetrics Random, EvaluationMetrics Untrained, EvaluationMetrics Trained, SeedEvaluation[] Seeds);

/// <summary>Per-key means use only samples where that drive exists; absent drives are not zeroes.</summary>
public sealed class MetricAccumulator
{
    private sealed class DriveSum
    {
        public int Count;
        public double Current, Target, Error, Satisfaction, Weight;
    }
    private readonly Dictionary<string, DriveSum> drives = new(StringComparer.Ordinal);
    private int count;
    private double balance, reward;

    public void Add(HomeostasisReport report, double stepReward) => Accumulate(report, stepReward, 1);
    internal void Remove(HomeostasisReport report, double stepReward) => Accumulate(report, stepReward, -1);

    private void Accumulate(HomeostasisReport report, double stepReward, int sign)
    {
        count += sign;
        balance += sign * report.Balance;
        reward += sign * stepReward;
        foreach (var drive in report.Drives.Values)
        {
            if (!drives.TryGetValue(drive.Key, out var sum)) drives.Add(drive.Key, sum = new());
            sum.Count += sign;
            sum.Current += sign * drive.Current;
            sum.Target += sign * drive.Target;
            sum.Error += sign * drive.NormalizedError;
            sum.Satisfaction += sign * drive.Satisfaction;
            sum.Weight += sign * drive.Weight;
            if (sum.Count == 0) drives.Remove(drive.Key);
        }
    }

    public EvaluationMetrics Metrics() => new(count, count == 0 ? 0 : Math.Clamp(balance / count, 0, 1),
        count == 0 ? 0 : reward / count, new(drives.Select(pair =>
        {
            var sum = pair.Value;
            return KeyValuePair.Create(pair.Key, new DriveMetrics(sum.Count, sum.Current / sum.Count,
                sum.Target / sum.Count, Math.Max(0, sum.Error / sum.Count), Math.Clamp(sum.Satisfaction / sum.Count, 0, 1), sum.Weight / sum.Count));
        })));
}
