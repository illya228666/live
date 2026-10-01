using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

public sealed record EvaluationMetrics(int Steps, double MeanAbsoluteError, double ComfortPercent, double MeanReward, double MeanBodyTemperature);
public sealed record SeedEvaluation(int Seed, EvaluationMetrics Random, EvaluationMetrics Untrained, EvaluationMetrics Trained);
public sealed record EvaluationReport(DateTimeOffset CreatedUtc, int TrainingSteps, double TrainingSeconds,
    EvaluationMetrics Random, EvaluationMetrics Untrained, EvaluationMetrics Trained, SeedEvaluation[] Seeds);

public sealed class MetricAccumulator(TemperatureOptions options)
{
    private int count;
    private double error;
    private double reward;
    private double bodySum;
    private int comfortable;

    public void Add(double body, double stepReward)
    {
        double deviation = Math.Abs(body - options.Target);
        count++;
        error += deviation;
        reward += stepReward;
        bodySum += body;
        if (deviation <= options.ComfortHalfWidth)
        {
            comfortable++;
        }
    }

    public EvaluationMetrics Metrics() => count == 0 ? new(0, 0, 0, 0, 0) : new(count, error / count, 100.0 * comfortable / count, reward / count, bodySum / count);
}

public static class Evaluator
{
    public static EvaluationMetrics Run(SimulationSession session, DqnBrain? brain, int[] seeds)
    {
        var metrics = new MetricAccumulator(session.Options.Temperature);
        foreach (int seed in seeds)
        {
            var random = new Random(seed);
            for (int episode = 0; episode < session.Options.Evaluation.EpisodesPerSeed; episode++)
            {
                session.Simulation.Reset(seed + episode * 10000);
                for (int step = 0; step < session.Options.World.EpisodeSteps; step++)
                {
                    ActionCandidate[] actions = session.Simulation.LegalActions();
                    ActionCandidate action = brain is null ? actions[random.Next(actions.Length)] : brain.Choose(session.Simulation.Observe(), actions);
                    StepResult result = session.Simulation.Step(action);
                    metrics.Add(session.Simulation.Agent.Get<ThermalBody>().Temperature, result.Reward);
                }
            }
        }
        return metrics.Metrics();
    }
}
