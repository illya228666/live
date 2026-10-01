using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtificialLife.Application;

[JsonConverter(typeof(JsonStringEnumConverter<TrainingSpeed>))]
public enum TrainingSpeed
{
    Normal,
    TenTimes,
    HundredTimes,
    Max
}

/// <summary>Observation/UI settings, separate from physics and DQN configuration.</summary>
public sealed record TrainingVisualizationOptions
{
    public int TrainingStepsPerVisualTick { get; init; } = 2;
    public int VisualTicksPerSecond { get; init; } = 30;
    public int HistoryLength { get; init; } = 240;
    public int TrailLength { get; init; } = 180;
    public int HistorySampleInterval { get; init; } = 2;
    public int RollingMetricWindow { get; init; } = 1000;
    public TrainingSpeed DefaultTrainingSpeed { get; init; } = TrainingSpeed.TenTimes;
    public int MaxBatchSize { get; init; } = 256;
    public double FrameBudgetMilliseconds { get; init; } = 8;

    public static TrainingVisualizationOptions Load(string path)
    {
        var options = JsonSerializer.Deserialize<TrainingVisualizationOptions>(File.ReadAllText(path), ExperimentOptions.Json)
            ?? throw new InvalidDataException("Live visualization configuration is empty.");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (TrainingStepsPerVisualTick < 1 || VisualTicksPerSecond is < 1 or > 120 || HistoryLength is < 2 or > 10000 ||
            TrailLength is < 2 or > 10000 || HistorySampleInterval < 1 || RollingMetricWindow is < 1 or > 100000 ||
            MaxBatchSize is < 1 or > 10000 || !double.IsFinite(FrameBudgetMilliseconds) ||
            FrameBudgetMilliseconds is <= 0 or > 33 || !Enum.IsDefined(DefaultTrainingSpeed))
        {
            throw new ArgumentException("Invalid live visualization settings.");
        }
    }

    public double StepsPerSecond(TrainingSpeed speed) => speed switch
    {
        TrainingSpeed.Normal => (double)TrainingStepsPerVisualTick * VisualTicksPerSecond,
        TrainingSpeed.TenTimes => (double)TrainingStepsPerVisualTick * VisualTicksPerSecond * 10,
        TrainingSpeed.HundredTimes => (double)TrainingStepsPerVisualTick * VisualTicksPerSecond * 100,
        TrainingSpeed.Max => double.PositiveInfinity,
        _ => throw new ArgumentOutOfRangeException(nameof(speed))
    };
}
