using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record PolicyEvaluation(EvaluationMetrics Aggregate, IReadOnlyDictionary<int, EvaluationMetrics> Seeds);

/// <summary>Fixed-policy, independent-seed evaluation through the common runtime step.</summary>
public static class RuntimeEvaluator
{
    public static PolicyEvaluation Run(RuntimeSession source, bool random = false)
    {
        var evaluation = source.Options.Evaluation;
        if (evaluation.Seeds.Length == 0 || evaluation.EpisodesPerSeed < 1 || evaluation.Seeds.Distinct().Count() != evaluation.Seeds.Length)
            throw new ArgumentException("Evaluation needs distinct seeds and a positive episode count.");
        var packages = source.Packages;
        var options = source.Options with
        {
            Mode = RuntimeMode.Evaluation,
            Packages = packages.Select(p => new PackageReference(p.Id, p.Version)).ToArray(),
            Settings = packages.ToDictionary(p => p.Id, p => p.Settings.Clone())
        };
        var total = new MetricAccumulator();
        var seeds = new Dictionary<int, EvaluationMetrics>();
        foreach (int seed in evaluation.Seeds)
        {
            var metrics = new MetricAccumulator();
            for (int episode = 0; episode < evaluation.EpisodesPerSeed; episode++)
            {
                int worldSeed = checked(seed + episode * 10000);
                IAgentController controller = random ? new RandomPolicy(source.Controller.Limits, worldSeed) : source.Controller.CreateInferenceController();
                using var runtime = RuntimeSessionFactory.Create(options with { WorldSeed = worldSeed }, controller,
                    observationKeys: source.ObservationKeys, actionKeys: source.ActionKeys, parentSessionId: source.ObservationSessionId);
                for (int step = 0; step < options.World.EpisodeSteps; step++)
                {
                    var sample = runtime.Step(false);
                    metrics.Add(sample.Homeostasis, sample.Reward);
                    total.Add(sample.Homeostasis, sample.Reward);
                }
            }
            seeds.Add(seed, metrics.Metrics());
        }
        return new(total.Metrics(), seeds);
    }
    private sealed class RandomPolicy(ControllerLimits limits, int seed) : IAgentController
    {
        private readonly Random random = new(seed);
        public ControllerLimits Limits => limits;
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => actions[random.Next(actions.Length)];
        public void Receive(AgentTransition transition) => throw new InvalidOperationException("Evaluation cannot learn.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
