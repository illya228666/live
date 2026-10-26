using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

public sealed record EcologyAuditProtocol
{
    public int[] Seeds { get; init; } = [111, 211, 307];
    public int BlockSteps { get; init; } = 1000;
    public string[] Policies { get; init; } = ["stay", "random", "park-0", "park-1", "park-2", "park-3", "park-4", "park-5", "park-6", "park-7", "park-8", "patrol", "forage", "avoid", "thermal", "joint", "joint-no-food", "joint-no-safety", "joint-no-thermal"];
    public double MaximumHungerMean { get; init; } = 0.35;
    public double MaximumHungerAbove { get; init; } = 0.1;
    public double MaximumProximity { get; init; } = 0.12;
    public double MaximumContact { get; init; } = 0.01;
    public double MaximumThermalOutside { get; init; } = 0.35;
    public double MaximumThermalMae { get; init; } = 1.2;
    public double MinimumPassiveHungerMean { get; init; } = 0.6;
    public double MinimumPassiveProximity { get; init; } = 0.15;
    public double MinimumSafetyImprovement { get; init; } = 0.5;
}
public sealed record EcologyControlResult(int Condition, int Seed, string Policy, int Steps,
    Dictionary<string, BehaviorMetric> Metrics, Dictionary<string, double?>[] Blocks, string ActionHash, string StateHash);
public sealed record EcologyAuditReport(int SchemaVersion, bool Passed, string Fingerprint, string[] Factors,
    EcologyAuditProtocol Protocol, EcologyControlResult[] Controls, string[] Failures, string[] Limitations);

/// <summary>Scenario controls, never a learner or reward-based policy. Uses the existing live runtime and evaluators.</summary>
public static class EcologyScenarioAudit
{
    public static readonly string[] PolicyNames = new EcologyAuditProtocol().Policies;
    public static EcologyControlResult Evaluate(RuntimeOptions options, BehaviorProfile profile, int condition, int seed,
        string policy, int steps, int blockSteps)
    {
        if (!PolicyNames.Contains(policy) || steps < 1 || blockSteps < 1) throw new ArgumentException("Invalid control/budget.");
        using var controller = new EcologyControl(policy, options, seed);
        using var runtime = new RuntimeSession(options with { WorldSeed = seed }, controller);
        using var actionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stateHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        runtime.TrajectorySink = transition => actionHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transition.Action, ExperimentJson.Lines) + "\n"));
        var evaluators = BehaviorEvaluators.Create(profile);
        var blocks = new List<Dictionary<string, double?>>();
        var blockEvaluators = BehaviorEvaluators.Create(profile);
        for (int step = 0; step < steps; step++)
        {
            if (policy.StartsWith("park-", StringComparison.Ordinal) || policy == "patrol") controller.Frame = runtime.Snapshot();
            runtime.AdvanceOne(false);
            var frame = runtime.Snapshot(); var state = runtime.Report();
            foreach (var evaluator in evaluators) evaluator.Observe(frame, state);
            foreach (var evaluator in blockEvaluators) evaluator.Observe(frame, state);
            // Include the entire physical frame, including RNG and schedules. Reward is deliberately excluded.
            stateHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, ExperimentJson.Lines) + "\n"));
            if ((step + 1) % blockSteps == 0 || step == steps - 1)
            {
                blocks.Add(BehaviorEvaluators.Complete(blockEvaluators).ToDictionary(p => p.Key, p => p.Value.Value));
                blockEvaluators = BehaviorEvaluators.Create(profile);
            }
        }
        var metrics = BehaviorEvaluators.Complete(evaluators);
        return new(condition, seed, policy, steps, metrics, blocks.ToArray(), Convert.ToHexString(actionHash.GetHashAndReset()), Convert.ToHexString(stateHash.GetHashAndReset()));
    }

    public static EcologyAuditReport Run(ExperimentDefinition definition, EcologyAuditProtocol protocol, Action<EcologyControlResult>? completed = null)
    {
        if (protocol.Seeds.Length < 3 || protocol.Seeds.Distinct().Count() != protocol.Seeds.Length || protocol.BlockSteps < 1 ||
            protocol.Policies.Length == 0 || protocol.Policies.Distinct().Count() != protocol.Policies.Length || protocol.Policies.Any(p => !PolicyNames.Contains(p)) ||
            new[] { protocol.MaximumHungerMean, protocol.MaximumHungerAbove, protocol.MaximumProximity, protocol.MaximumContact,
                protocol.MaximumThermalOutside, protocol.MinimumPassiveHungerMean, protocol.MinimumPassiveProximity, protocol.MinimumSafetyImprovement }
                .Any(v => !double.IsFinite(v) || v < 0 || v > 1) || protocol.MinimumSafetyImprovement <= 0 || !double.IsFinite(protocol.MaximumThermalMae) || protocol.MaximumThermalMae <= 0 ||
            protocol.MinimumPassiveProximity <= protocol.MaximumProximity || protocol.MinimumPassiveHungerMean <= protocol.MaximumHungerMean)
            throw new InvalidDataException("Invalid ecology audit protocol.");
        if (!definition.Behavior.Evaluators.Any(e => e.Id == "physical") || !definition.Behavior.Evaluators.Any(e => e.Id == "ecology-v1"))
            throw new InvalidDataException("Ecology audit requires independent physical and ecology-v1 evaluators.");
        var description = ScenarioSuitability.Describe(definition);
        var controls = new List<EcologyControlResult>(); var failures = new List<string>();
        for (int condition = 0; condition < description.Conditions.Length; condition++)
        foreach (int seed in protocol.Seeds)
        foreach (string policy in protocol.Policies)
        {
            var result = Evaluate(description.Conditions[condition], definition.Behavior, condition, seed, policy, definition.EvaluationSteps, protocol.BlockSteps);
            controls.Add(result); completed?.Invoke(result);
        }
        foreach (int condition in Enumerable.Range(0, description.Conditions.Length))
        {
            using var inspection = new RuntimeSession(description.Conditions[condition], new EcologyControl("stay", description.Conditions[condition], 0));
            var factors = inspection.Report().Drives.Keys.ToArray();
            var rows = controls.Where(c => c.Condition == condition).ToArray();
            double Value(EcologyControlResult r, string key) => r.Metrics.TryGetValue(key, out var m) && m.Value is double v && double.IsFinite(v) ? v : double.NaN;
            if (!factors.All(f => f is "temperature" or "hunger" or "safety") || factors.Length == 0) failures.Add("Audit does not cover all active factors.");
            if (definition.EvaluationSteps < 10000) failures.Add("Admission requires at least 10000 continuous steps per control.");
            if (!PolicyNames.All(protocol.Policies.Contains)) failures.Add("Admission requires the full set of controls and ablations.");
            foreach (var r in rows.Where(r => r.Policy == "joint"))
            {
                if (factors.Contains("hunger") && (!(Value(r, "hunger.mean") <= protocol.MaximumHungerMean) || !(Value(r, "hunger.above") <= protocol.MaximumHungerAbove))) failures.Add($"{condition}/{r.Seed}: hunger feasibility failed.");
                if (factors.Contains("safety") && (!(Value(r, "wildlife.proximity") <= protocol.MaximumProximity) || !(Value(r, "wildlife.contact-proxy") <= protocol.MaximumContact))) failures.Add($"{condition}/{r.Seed}: safety feasibility failed.");
                if (factors.Contains("temperature") && (!(Value(r, "temperature.outside") <= protocol.MaximumThermalOutside) || !(Value(r, "temperature.mae") <= protocol.MaximumThermalMae))) failures.Add($"{condition}/{r.Seed}: thermal feasibility failed.");
            }
            if (factors.Contains("hunger"))
            {
                var settings = description.Conditions[condition].Settings;
                if (!settings.TryGetValue("hunger", out var h) || !settings.TryGetValue("food", out var f)) failures.Add("Audit requires explicit hunger/food parameters.");
                else
                {
                    var hunger = new RuntimeSettings(h); var food = new RuntimeSettings(f);
                    double growth = hunger.Number("growthPerSecond", 0.001), eatRadius = hunger.Number("eatRadius", 2);
                    if (growth <= 0 || definition.EvaluationSteps * description.Conditions[condition].World.TimeStep * growth < 10 ||
                        food.Number("minSpawnDistanceFromAgent", 10) <= eatRadius) failures.Add("Insufficient starvation horizon or food can spawn within stationary eating reach.");
                }
                if (rows.Where(r => r.Policy == "stay" || r.Policy.StartsWith("park-", StringComparison.Ordinal)).Any(r => !(Value(r, "hunger.mean") >= protocol.MinimumPassiveHungerMean))) failures.Add($"{condition}: passive hunger too low.");
                if (!(rows.Where(r => r.Policy == "joint-no-food").Select(r => Value(r, "hunger.mean")).DefaultIfEmpty(double.NaN).Average() >= protocol.MaximumHungerMean + 0.1)) failures.Add($"{condition}: foraging ablation has no meaningful hunger cost.");
            }
            if (factors.Contains("safety"))
            {
                foreach (var r in rows.Where(r => r.Policy == "stay" || r.Policy.StartsWith("park-", StringComparison.Ordinal) || r.Policy == "patrol"))
                    if (!(Value(r, "wildlife.proximity") >= protocol.MinimumPassiveProximity)) failures.Add($"{condition}/{r.Seed}: fixed strategy {r.Policy} is too safe.");
                double blind = rows.Where(r => r.Policy == "joint-no-safety").Select(r => Value(r, "wildlife.proximity")).DefaultIfEmpty(double.NaN).Average();
                double adaptive = rows.Where(r => r.Policy == "joint").Select(r => Value(r, "wildlife.proximity")).DefaultIfEmpty(double.NaN).Average();
                if (!(adaptive <= blind * (1 - protocol.MinimumSafetyImprovement))) failures.Add($"{condition}: avoidance ablation has insufficient effect.");
            }
            if (factors.Contains("temperature"))
            {
                if (!(rows.Where(r => r.Policy == "joint-no-thermal").Select(r => Value(r, "temperature.outside")).DefaultIfEmpty(double.NaN).Average() >= protocol.MaximumThermalOutside + 0.1)) failures.Add($"{condition}: thermal ablation has no meaningful cost.");
                var t = description.Conditions[condition].Settings["temperature"].Deserialize<TemperatureOptions>(PackageCatalog.Json)!;
                if (TemperatureScenarioAudit.Analyze(t, description.Conditions[condition].World, 1).MinimumUnavoidablePeakError <= 1) failures.Add("Fixed-position thermal proof failed.");
            }
        }
        return new(1, failures.Count == 0, description.Fingerprint, description.Factors, protocol, controls.ToArray(), failures.Distinct().ToArray(),
            ["Hand-written observation-based controls establish feasibility, not learnability or superiority of motivation/learning methods.",
             "Safety measures proximity/contact proxies. The simulation has no injury, mortality or survival mechanics.",
             "Finite seeds and a fixed strategy family do not prove that every possible open-loop strategy fails.",
             "Full-world vision is retained; results do not validate limited-range/occluded sensing."]);
    }

    public static int Command(string[] args)
    {
        var values = RuntimeCommands.Arguments(args);
        if (values.Keys.Any(k => k is not ("--config" or "--protocol" or "--output"))) throw new ArgumentException("Unknown ecology audit option.");
        string output = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(output)) throw new IOException("Audit output must be new; preserve earlier evidence.");
        var definition = ExperimentDefinition.Load(values["--config"]);
        var protocol = ExperimentJson.Read<EcologyAuditProtocol>(values["--protocol"]);
        Directory.CreateDirectory(output);
        // Existing preparation freezes configuration and physical packages, with no training.
        ExperimentRunner.Prepare(["--config", values["--config"], "--output", Path.Combine(output, "frozen")]);
        _ = ExperimentRunner.Provenance(output, 1);
        File.Copy(values["--config"], Path.Combine(output, "source.json"));
        definition = ExperimentDefinition.Load(Path.Combine(output, "frozen", "config.json"));
        ExperimentJson.Atomic(Path.Combine(output, "configuration.json"), definition);
        ExperimentJson.Atomic(Path.Combine(output, "protocol.json"), protocol);
        using var progress = new StreamWriter(Path.Combine(output, "controls.jsonl")) { AutoFlush = true };
        var report = Run(definition, protocol, r =>
        {
            progress.WriteLine(JsonSerializer.Serialize(r, ExperimentJson.Lines));
            Console.WriteLine($"CONTROL condition={r.Condition} seed={r.Seed} policy={r.Policy} hunger={r.Metrics["hunger.mean"].Value:F3} proximity={r.Metrics["wildlife.proximity"].Value:F3} thermalOutside={r.Metrics["temperature.outside"].Value:F3}");
        });
        string reportPath = Path.Combine(output, "report.json"); ExperimentJson.Atomic(reportPath, report);
        var review = new ScenarioReview(1, report.Fingerprint, report.Passed, report.Factors.Select(f => new FactorSuitability(f,
            report.Passed ? "requires-adaptation" : "repair-required", "Stay, nine fixed positions, blind patrol, random and factor ablations; per-seed long-horizon metrics in evidence.",
            "Observation-only reactive control under identical physics; thresholds fixed in the audit protocol.", "Repair common scenario parameters/mechanics and rerun controls if trivial or infeasible; never rank learners on a broken factor.")).ToArray(), reportPath)
        { EvidenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reportPath))) };
        ExperimentJson.Atomic(Path.Combine(output, "review.json"), review);
        Console.WriteLine($"ECOLOGY_AUDIT passed={report.Passed} controls={report.Controls.Length} fingerprint={report.Fingerprint}");
        foreach (string failure in report.Failures) Console.WriteLine(failure);
        return report.Passed ? 0 : 2;
    }
}

internal sealed class EcologyControl(string policy, RuntimeOptions options, int seed) : IAgentController
{
    private readonly Random random = new(seed);
    private string[] keys = [];
    private int waypoint;
    private bool eating;
    public RuntimeFrame? Frame { get; set; } // Used only by fixed-position/patrol controls; adaptive controls never read it.
    public ControllerLimits Limits => new(int.MaxValue, int.MaxValue, int.MaxValue);
    public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
    public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions) => keys = observations.ToArray();
    public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
    {
        if (learning) throw new InvalidOperationException("Audit controls never train.");
        if (policy == "random") return actions[random.Next(actions.Length)];
        if (policy == "stay") return actions.Single(a => a.Parameters.All(p => p == 0));
        if (policy.StartsWith("park-", StringComparison.Ordinal) || policy == "patrol")
        {
            int index = policy == "patrol" ? new[] { 0, 2, 8, 6 }[waypoint % 4] : int.Parse(policy[5..], System.Globalization.CultureInfo.InvariantCulture);
            var goal = new Position(options.World.Width * (0.01 + 0.49 * (index % 3)), options.World.Height * (0.01 + 0.49 * (index / 3)));
            if (policy == "patrol" && Frame!.Agent.Position.DistanceTo(goal) < 2) waypoint++;
            return actions.MinBy(a => Destination(Frame!.Agent.Position, a).DistanceTo(goal))!;
        }
        ObservationToken? Token(string key) => state.FirstOrDefault(t => keys[t.Type] == key);
        var apples = state.Where(t => keys[t.Type] == "vision.appearance.diamond.v1").Select(Relative).ToArray();
        var boars = state.Where(t => keys[t.Type] == "vision.appearance.boar.v1").Select(Relative).ToArray();
        double hunger = Token("hunger.body.v1")?.Features[0] ?? 0;
        if (hunger > 0.35) eating = true;
        if (hunger < 0.12) eating = false;
        bool useFood = policy is "forage" or "joint" or "joint-no-safety" or "joint-no-thermal";
        bool useSafety = policy is "avoid" or "joint" or "joint-no-food" or "joint-no-thermal";
        bool useThermal = policy is "thermal" or "joint" or "joint-no-food" or "joint-no-safety";
        var body = Token("temperature.body.v1");
        var sensors = state.Where(t => keys[t.Type] == "temperature.sensor.v1").ToArray();
        var thermal = options.Settings.TryGetValue("temperature", out var settings) ? settings.Deserialize<TemperatureOptions>(PackageCatalog.Json)! : new TemperatureOptions();
        double Field(float x, float y) => sensors.Single(t => t.Features[1] == x && t.Features[2] == y).Features[0] * 40 + 30;
        double environment = sensors.Length == 5 ? Field(0, 0) : 0;
        double gx = sensors.Length == 5 ? (Field(1, 0) - Field(-1, 0)) / (2 * thermal.SensorDistance) : 0;
        double gy = sensors.Length == 5 ? (Field(0, 1) - Field(0, -1)) / (2 * thermal.SensorDistance) : 0;
        double hxx = sensors.Length == 5 ? (Field(1, 0) + Field(-1, 0) - 2 * environment) / (thermal.SensorDistance * thermal.SensorDistance) : 0;
        double hyy = sensors.Length == 5 ? (Field(0, 1) + Field(0, -1) - 2 * environment) / (thermal.SensorDistance * thermal.SensorDistance) : 0;
        double aBody = Math.Exp(-thermal.HeatTransfer * options.World.TimeStep);
        return actions.MinBy(action =>
        {
            double dx = action.Parameters[0] * options.World.MovementSpeed * options.World.TimeStep, dy = action.Parameters[1] * options.World.MovementSpeed * options.World.TimeStep;
            double cost = action.Parameters.All(p => p == 0) ? 0 : 1e-6;
            if (useThermal && body is not null)
            {
                double local = environment + gx * dx + gy * dy + 0.5 * (hxx * dx * dx + hyy * dy * dy);
                double predicted = aBody * (body.Features[0] * 20 + thermal.Target) + (1 - aBody) * (local + thermal.HeatProduction / thermal.HeatTransfer);
                cost += (policy == "thermal" ? 1 : 1.5) * Math.Abs(predicted - thermal.Target);
            }
            if (useFood && apples.Length > 0 && (eating || policy == "forage"))
            {
                double distance = apples.Min(p => Math.Sqrt((p.X - dx) * (p.X - dx) + (p.Y - dy) * (p.Y - dy)));
                cost += (policy == "forage" ? 1 : 0.2 + Math.Max(0, hunger - 0.35) * 4) * distance;
            }
            if (useSafety)
                foreach (var p in boars)
                {
                    double distance = Math.Sqrt((p.X - dx) * (p.X - dx) + (p.Y - dy) * (p.Y - dy));
                    cost += 0.2 * Math.Pow(Math.Max(0, 7.5 - distance), 2) + 4 * Math.Pow(Math.Max(0, 4 - distance), 2);
                }
            return cost;
        })!;
    }
    private Position Destination(Position position, ActionCandidate action) => new(position.X + action.Parameters[0] * options.World.MovementSpeed * options.World.TimeStep, position.Y + action.Parameters[1] * options.World.MovementSpeed * options.World.TimeStep);
    private static Position Relative(ObservationToken token)
    {
        double distance = token.Features[2] / Math.Max(1e-9, 1.0 - token.Features[2]);
        return new(token.Features[0] * distance, token.Features[1] * distance);
    }
    public void Receive(AgentTransition transition) => throw new NotSupportedException();
    public void ConditionsChanged(long revision) { }
    public void Export(string path) => throw new NotSupportedException();
    public void Import(string path) => throw new NotSupportedException();
    public void Dispose() { }
}
