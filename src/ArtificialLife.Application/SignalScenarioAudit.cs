using System.Security.Cryptography;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Open contextual-signal diagnostic: checks opportunity, not ecological generalization.</summary>
public static class SignalScenarioAudit
{
    public sealed record Control(int Condition, int Seed, string Policy, double Success, double Reward);
    public static int Command(string[] args)
    {
        var values = RuntimeCommands.Arguments(args);
        if (values.Keys.Any(k => k is not ("--config" or "--output"))) throw new ArgumentException("Unknown signal audit option.");
        string output = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(output)) throw new IOException("Preserve audits; select a new output directory.");
        var definition = ExperimentDefinition.Load(values["--config"]);
        var description = ScenarioSuitability.Describe(definition);
        var controls = new List<Control>(); var failures = new List<string>();
        int[] seeds = [41011, 42013, 43019, 44021, 45007];
        for (int condition = 0; condition < description.Conditions.Length; condition++)
        foreach (int seed in seeds)
        foreach (string policy in new[] { "constant-first", "constant-last", "random", "oracle" })
        {
            using var controller = new ReferenceController(policy, seed);
            using var runtime = RuntimeSessionFactory.Create(description.Conditions[condition] with { WorldSeed = seed }, controller);
            var evaluators = BehaviorEvaluators.Create(definition.Behavior);
            double reward = 0;
            for (int i = 0; i < definition.EvaluationSteps; i++)
            {
                var sample = runtime.AdvanceOne(false); reward += sample.Reward;
                foreach (var evaluator in evaluators) evaluator.Observe(runtime.Snapshot(), sample.Homeostasis);
            }
            double success = BehaviorEvaluators.Complete(evaluators)["signal.success"].Value ?? throw new InvalidDataException("Missing signal metric.");
            controls.Add(new(condition, seed, policy, success, reward / definition.EvaluationSteps));
            if (policy == "oracle" && success < 0.999) failures.Add($"Condition{condition}/seed{seed}: observable active control failed.");
            if (policy != "oracle" && success > 0.55) failures.Add($"Condition{condition}/seed{seed}/{policy}: nonadaptive success is excessive.");
        }
        Directory.CreateDirectory(output);
        string evidence = Path.Combine(output, "report.json");
        ExperimentJson.Atomic(evidence, new
        {
            SchemaVersion = 1, Passed = failures.Count == 0, description.Fingerprint, description.ScheduleFingerprint,
            Steps = definition.EvaluationSteps, Controls = controls, Failures = failures,
            Analytical = "Independent symmetric cues give at most1/2 success to any constant action; observable cue-conditioned choice attains1. Actions do not alter future stimuli.",
            Limitation = "This is an open diagnostic of representation/updates and exogenous opportunity, not a new ecological benchmark or a method ranking."
        });
        var factors = description.Factors.Append("signal-effect-control").Distinct().Select(f => new FactorSuitability(f,
            failures.Count == 0 ? "requires-adaptation" : "repair-required", "Constant-first/last and random controls plus symmetric-cue analytical bound.",
            "Oracle uses only offered candidates and visible cue tokens; never passed to the learner.",
            "Repair common diagnostic stimuli/observability and re-audit before interpretation.")).ToArray();
        ExperimentJson.Atomic(Path.Combine(output, "review.json"), new ScenarioReview(1, description.Fingerprint, failures.Count == 0, factors, "report.json")
        { EvidenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidence))), ScheduleFingerprint = description.ScheduleFingerprint });
        Console.WriteLine($"SIGNAL_AUDIT passed={failures.Count == 0} controls={controls.Count}");
        foreach (string failure in failures) Console.WriteLine(failure);
        return failures.Count == 0 ? 0 : 2;
    }

    private sealed class ReferenceController(string policy, int seed) : IAgentController
    {
        private readonly Random random = new(seed);
        private int cue, alternate = -1, added = -1;
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions)
        {
            cue = observations.ToList().IndexOf("signal-diagnostic.cue.v1");
            alternate = observations.ToList().IndexOf("signal-diagnostic.alternate.v1");
            added = actions.ToList().IndexOf("signal-diagnostic.added-choice.v1");
        }
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
        {
            if (policy == "constant-first") return actions[0];
            if (policy == "constant-last") return actions[^1];
            if (policy == "random") return actions[random.Next(actions.Length)];
            double relevant = state.Single(t => t.Type == (alternate >= 0 ? alternate : cue)).Features[0];
            return added >= 0 && relevant > 0 ? actions.Single(a => a.Type == added) :
                actions.First(a => a.Type != added && Math.Sign(a.Parameters[0]) == Math.Sign(relevant));
        }
        public void Receive(AgentTransition transition) => throw new InvalidOperationException("Audit does not learn.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
