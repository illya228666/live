using System.Diagnostics;
using ArtificialLife.Brain;

namespace ArtificialLife.Application;

public sealed record TrainingProgress(int Step, double Epsilon, double Loss, int ReplaySize, EvaluationMetrics Metrics, double Seconds);

public static class Trainer
{
    public static double Run(SimulationSession session, DqnBrain brain, Action<TrainingProgress>? progress = null)
    {
        LearningOptions options = session.Options.Learning;
        var engine = new TrainingEngine(session, brain);
        var stopwatch = Stopwatch.StartNew();
        var metrics = new MetricAccumulator(session.Options.Temperature);
        for (int step = 1; step <= options.TrainingSteps; step++)
        {
            TrainingSample sample = engine.AdvanceOne();
            metrics.Add(sample.BodyTemperature, sample.Reward);
            if (step % options.LogEvery == 0 || step == options.TrainingSteps)
            {
                progress?.Invoke(new TrainingProgress(step, brain.Epsilon(step), brain.LastLoss, brain.Replay.Count, metrics.Metrics(), stopwatch.Elapsed.TotalSeconds));
                metrics = new MetricAccumulator(session.Options.Temperature);
            }
        }
        return stopwatch.Elapsed.TotalSeconds;
    }
}
