using ArtificialLife.Core;
using ArtificialLife.Brain;
namespace ArtificialLife.Application;
public static class Evaluator
{
    public static EvaluationMetrics Run(SimulationSession session, DqnBrain? brain, int[] seeds)
    {
        var metrics = new MetricAccumulator();
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
                    metrics.Add(HomeostasisReport.Capture(session.Simulation), result.Reward);
                }
            }
        }
        return metrics.Metrics();
    }
}
