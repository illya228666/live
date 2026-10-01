using System.Diagnostics;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

public sealed record TrainingProgress(int Step, double Epsilon, double Loss, int ReplaySize, EvaluationMetrics Metrics, double Seconds);

public static class Trainer
{
    public static double Run(SimulationSession session, DqnBrain brain, Action<TrainingProgress>? progress = null)
    {
        LearningOptions options = session.Options.Learning;
        Simulation simulation = session.Simulation;
        int episode = 0;
        simulation.Reset(options.Seed);
        var stopwatch = Stopwatch.StartNew();
        var metrics = new MetricAccumulator(session.Options.Temperature);
        ObservationToken[] state = simulation.Observe();
        ActionCandidate[] actions = simulation.LegalActions();
        for (int step = 1; step <= options.TrainingSteps; step++)
        {
            ActionCandidate action = brain.Choose(state, actions, brain.Epsilon(step));
            StepResult result = simulation.Step(action);
            brain.Replay.Add(new Experience(state, action, (float)result.Reward, result.Observations, result.Actions, result.Terminal));
            brain.Learn(step);
            metrics.Add(simulation.Agent.Get<ThermalBody>().Temperature, result.Reward);
            state = result.Observations;
            actions = result.Actions;
            if (result.Terminal)
            {
                // Reset только после сохранения терминального перехода; наблюдения следующего эпизода не смешиваются.
                episode++;
                simulation.Reset(options.Seed + episode);
                state = simulation.Observe();
                actions = simulation.LegalActions();
            }
            if (step % options.LogEvery == 0 || step == options.TrainingSteps)
            {
                progress?.Invoke(new TrainingProgress(step, brain.Epsilon(step), brain.LastLoss, brain.Replay.Count, metrics.Metrics(), stopwatch.Elapsed.TotalSeconds));
                metrics = new MetricAccumulator(session.Options.Temperature);
            }
        }
        return stopwatch.Elapsed.TotalSeconds;
    }
}
