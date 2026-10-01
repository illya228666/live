namespace ArtificialLife.Core;

public sealed record WorldOptions
{
    public double Width { get; init; } = 100;
    public double Height { get; init; } = 100;
    public double TimeStep { get; init; } = 1;
    public double MovementSpeed { get; init; } = 1.5;
    public int EpisodeSteps { get; init; } = 400;

    public void Validate()
    {
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || !double.IsFinite(TimeStep) ||
            !double.IsFinite(MovementSpeed) || Width <= 0 || Height <= 0 || TimeStep <= 0 || MovementSpeed <= 0 || EpisodeSteps < 1)
        {
            throw new ArgumentException("World dimensions, timestep, speed and episode length must be positive and finite.");
        }
    }
}

public sealed record RewardOptions
{
    public double ImprovementWeight { get; init; } = 0.2;
    public double MaximumDriveWeight { get; init; } = 10;
}

/// <summary>Weighted normalized error, plus bounded improvement. Comfort retains a positive reward.</summary>
public sealed class RewardAggregator(RewardOptions options)
{
    public double Error(IEnumerable<DriveState> drives)
    {
        double total = 0;
        double weights = 0;
        foreach (DriveState drive in drives)
        {
            if (!double.IsFinite(drive.Current) || !double.IsFinite(drive.Target) || !double.IsFinite(drive.Scale) ||
                !double.IsFinite(drive.Weight) || drive.Scale <= 0 || drive.Weight < 0)
            {
                throw new InvalidOperationException($"Invalid drive: {drive.Key}");
            }
            double weight = Math.Min(drive.Weight, options.MaximumDriveWeight);
            total += Math.Min(Math.Abs(drive.Current - drive.Target) / drive.Scale, 4) * weight;
            weights += weight;
        }
        return weights == 0 ? 0 : total / weights;
    }

    public double Reward(double previousError, double error)
    {
        // Сигнал сохраняется у цели; улучшение не может заменить длительный комфорт.
        return Math.Clamp(1 - 2 * Math.Tanh(error) + options.ImprovementWeight * (previousError - error), -1, 1);
    }
}
