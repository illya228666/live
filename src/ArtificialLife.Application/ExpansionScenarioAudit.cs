using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

/// <summary>Task-specific positive/passive controls. Never supplies examples or features to a learner.</summary>
public static class ExpansionScenarioAudit
{
    public sealed record Control(int Condition, int Seed, string Policy, Dictionary<string, BehaviorMetric> Metrics, string ActionHash);
    public static int Command(string[] args)
    {
        var values = RuntimeCommands.Arguments(args);
        if (values.Keys.Any(k => k is not ("--config" or "--output"))) throw new ArgumentException("Unknown expansion audit option.");
        string output = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(output)) throw new IOException("Use a new audit output directory.");
        var definition = ExperimentDefinition.Load(values["--config"]);
        var description = ScenarioSuitability.Describe(definition);
        var controls = new List<Control>(); var failures = new List<string>();
        int[] seeds = [81001, 82003, 83009, 84011, 85021];
        for (int condition = 0; condition < description.Conditions.Length; condition++)
        {
            var options = description.Conditions[condition];
            var thermal = options.Settings["temperature"].Deserialize<TemperatureOptions>(PackageCatalog.Json)!;
            var stationary = TemperatureScenarioAudit.Analyze(thermal, options.World, 1);
            if (stationary.MinimumUnavoidablePeakError <= 1 || stationary.BestComfortFraction > 0.65)
                failures.Add($"Condition{condition}: thermal passivity is sufficient.");
            if (definition.EvaluationSteps * options.World.TimeStep < 8 * Math.Max(thermal.OscillationPeriod, 300))
                failures.Add($"Condition{condition}: evaluation horizon is too short.");
            bool workbench = options.Packages.Any(p => p.Id == "workbench");
            foreach (int seed in seeds)
            foreach (string policy in new[] { "stay", "always-plus", "always-minus", "random", "thermal-only", "interaction-only", "joint" })
            {
                using var controller = new AuditController(policy, options.World, thermal, seed);
                using var runtime = RuntimeSessionFactory.Create(options with { WorldSeed = seed }, controller);
                var evaluators = BehaviorEvaluators.Create(definition.Behavior);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                runtime.TrajectorySink = t => ExperimentRunner.HashAction(hash, t.Action);
                for (int step = 0; step < definition.EvaluationSteps; step++)
                {
                    var sample = runtime.AdvanceOne(false);
                    foreach (var evaluator in evaluators) evaluator.Observe(runtime.Snapshot(), sample.Homeostasis);
                }
                var metrics = BehaviorEvaluators.Complete(evaluators);
                controls.Add(new(condition, seed, policy, metrics, Convert.ToHexString(hash.GetHashAndReset())));
                double? Metric(string key) => metrics.GetValueOrDefault(key)?.Value;
                if (policy == "joint" && (Metric("temperature.mae") is not double mae || mae > 0.8))
                    failures.Add($"Condition{condition}/seed{seed}: joint thermal MAE>0.8.");
                if (workbench && policy is "joint" or "interaction-only" &&
                    (Metric("interaction.pressure-error") is not double error || error > 0.25 || Metric("interaction.both-directions") != 1))
                    failures.Add($"Condition{condition}/seed{seed}/{policy}: active pressure control failed.");
                if (workbench && policy is "stay" or "always-plus" or "always-minus" or "random" or "thermal-only" &&
                    (Metric("interaction.pressure-error") is not double passive || passive < 0.45))
                    failures.Add($"Condition{condition}/seed{seed}/{policy}: pressure is passively adequate.");
            }
        }
        Directory.CreateDirectory(output);
        string evidence = Path.Combine(output, "report.json");
        ExperimentJson.Atomic(evidence, new
        {
            SchemaVersion = 1, Passed = failures.Count == 0, description.Fingerprint, description.ScheduleFingerprint,
            Seeds = seeds, Conditions = description.Conditions, Steps = definition.EvaluationSteps,
            Thresholds = new { JointThermalMae = 0.8, ActivePressureError = 0.25, MinimumPassivePressureError = 0.45 },
            Controls = controls, Failures = failures,
            Limitations = new[] { "Pressure is a remote interaction, not object navigation or tool composition.",
                "Audit demonstrates reachability, not learning. Noise is an uncontrollable distractor, not a solvable need.",
                "Non-drive interaction is explicitly reviewed; presence of a drive is not the criterion for admitting a factor." }
        });
        var factors = description.Factors.Append("workbench-interaction").Distinct().Select(f => new FactorSuitability(f,
            failures.Count == 0 ? "requires-adaptation" : "repair-required",
            "All-position thermal bound and stay/constant-command/random/thermal-only controls; see per-condition/per-seed evidence.",
            "Observed gauge+body+local fields; independent and joint feedback controllers; no training or learner access to controls.",
            "Repair the shared scenario and rerun controls before comparing learning/motivation.")).ToArray();
        ExperimentJson.Atomic(Path.Combine(output, "review.json"), new ScenarioReview(1, description.Fingerprint, failures.Count == 0, factors, "report.json")
        { EvidenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidence))), ScheduleFingerprint = description.ScheduleFingerprint });
        Console.WriteLine($"EXPANSION_AUDIT passed={failures.Count == 0} controls={controls.Count}");
        foreach (string failure in failures) Console.WriteLine(failure);
        return failures.Count == 0 ? 0 : 2;
    }

    private sealed class AuditController(string policy, WorldOptions world, TemperatureOptions thermal, int seed) : IAgentController
    {
        private readonly Random random = new(seed);
        private int bodyType, sensorType, gaugeType = -1, moveType, commandType = -1;
        public ControllerLimits Limits => new(256, 256, 4);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions)
        {
            bodyType = observations.ToList().IndexOf("temperature.body.v1"); sensorType = observations.ToList().IndexOf("temperature.sensor.v1");
            gaugeType = observations.ToList().IndexOf("workbench.reading.v1"); moveType = actions.ToList().IndexOf("core.move.v1");
            commandType = actions.ToList().IndexOf("workbench.adjust.v1");
        }
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
        {
            if (policy == "random") return actions[random.Next(actions.Length)];
            var moves = actions.Where(a => a.Type == moveType).ToArray();
            var stay = moves.Single(a => a.Parameters.All(p => p == 0));
            if (policy == "stay") return stay;
            if (commandType >= 0 && policy is "always-plus" or "always-minus")
                return actions.Single(a => a.Type == commandType && Math.Sign(a.Parameters[0]) == (policy == "always-plus" ? 1 : -1));
            if (gaugeType >= 0 && policy is "joint" or "interaction-only")
            {
                double gauge = state.Single(t => t.Type == gaugeType).Features[0];
                if (Math.Abs(gauge) > 0.12) return actions.Single(a => a.Type == commandType && Math.Sign(a.Parameters[0]) == -Math.Sign(gauge));
            }
            if (policy is "interaction-only" or "always-plus" or "always-minus") return stay;
            double body = state.Single(t => t.Type == bodyType).Features[0] * 20 + thermal.Target;
            var sensors = state.Where(t => t.Type == sensorType).ToArray();
            double Field(float x, float y) => sensors.Single(t => t.Features[1] == x && t.Features[2] == y).Features[0] * 40 + 30;
            double field = Field(0, 0), distance = thermal.SensorDistance;
            double gx = (Field(1, 0) - Field(-1, 0)) / (2 * distance), gy = (Field(0, 1) - Field(0, -1)) / (2 * distance);
            double hxx = (Field(1, 0) + Field(-1, 0) - 2 * field) / (distance * distance);
            double hyy = (Field(0, 1) + Field(0, -1) - 2 * field) / (distance * distance);
            if (Math.Abs(gx) + Math.Abs(gy) + Math.Abs(hxx) + Math.Abs(hyy) < 1e-10 && Math.Abs(body - thermal.Target) > 0.5)
                return moves.First(a => a.Parameters.Any(p => p != 0));
            double inertia = Math.Exp(-thermal.HeatTransfer * world.TimeStep);
            return moves.MinBy(a =>
            {
                double dx = a.Parameters[0] * world.MovementSpeed * world.TimeStep, dy = a.Parameters[1] * world.MovementSpeed * world.TimeStep;
                double local = field + gx * dx + gy * dy + 0.5 * (hxx * dx * dx + hyy * dy * dy);
                return Math.Abs(inertia * body + (1 - inertia) * (local + thermal.HeatProduction / thermal.HeatTransfer) - thermal.Target) +
                    (a.Parameters.All(p => p == 0) ? 0 : 1e-6);
            })!;
        }
        public void Receive(AgentTransition transition) => throw new InvalidOperationException("Audit never trains.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
