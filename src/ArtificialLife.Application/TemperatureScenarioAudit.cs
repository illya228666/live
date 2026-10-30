using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

public sealed record ThermalAuditProtocol
{
    public int[] Seeds { get; init; } = [81001, 82003, 83009, 84011, 85021];
    public double MaximumStationaryComfortFraction { get; init; } = 0.65;
    public double MinimumActiveComfortFraction { get; init; } = 0.90;
    public double MaximumActiveMae { get; init; } = 0.5;
    public int MinimumPeriods { get; init; } = 8;
}
public sealed record StationaryThermalAnalysis(double FarEquilibrium, double TargetGap, double FilteredRelativeAmplitude,
    double MinimumUnavoidablePeakError, double BestComfortRadius, double BestComfortFraction, double BestMaeRadius, double BestMae,
    double MinimumComfortRadius, double MaximumComfortRadius, double MaximumRequiredRadialSpeed);
public sealed record ThermalControlResult(int Condition, int Seed, string Policy, int Steps, double Mae, double ComfortFraction,
    double TailComfortFraction, int LongestComfortRun, double MovingFraction, double TailRadiusRange)
{
    public string ActionHash { get; init; } = "";
}
public sealed record ThermalAuditReport(int SchemaVersion, bool Passed, string Fingerprint, ThermalAuditProtocol Protocol,
    StationaryThermalAnalysis[] Analysis, ThermalControlResult[] Controls, string[] Failures, string[] Limitations);

/// <summary>Diagnostic baselines over the unchanged live physics. Does not train any algorithm.</summary>
public static class TemperatureScenarioAudit
{
    public static StationaryThermalAnalysis Analyze(TemperatureOptions t, WorldOptions world, double tolerance)
    {
        t.Validate(); world.Validate();
        if (tolerance <= 0 || !double.IsFinite(tolerance)) throw new ArgumentException("Invalid evaluation tolerance.");
        double far = t.Ambient + t.HeatProduction / t.HeatTransfer;
        double gap = t.Target - far, omega = Math.Tau / t.OscillationPeriod;
        double a = Math.Exp(-t.HeatTransfer * world.TimeStep);
        double beta = t.OscillationAmplitude * (1 - a) / Math.Sqrt(1 + a * a - 2 * a * Math.Cos(omega * world.TimeStep));
        double maxRadius = Math.Sqrt(world.Width * world.Width + world.Height * world.Height) / 2;
        double bestComfort = -1, bestMae = double.PositiveInfinity, comfortRadius = 0, maeRadius = 0;
        for (int i = 0; i <= 10000; i++)
        {
            double r = maxRadius * i / 10000;
            double h = t.FireStrength * Math.Exp(-r * r / (2 * t.FireRadius * t.FireRadius));
            double mean = h - gap, amplitude = beta * h;
            double fraction, mae;
            if (amplitude < 1e-12) { fraction = Math.Abs(mean) <= tolerance ? 1 : 0; mae = Math.Abs(mean); }
            else
            {
                fraction = (Math.Asin(Math.Clamp((tolerance - mean) / amplitude, -1, 1)) - Math.Asin(Math.Clamp((-tolerance - mean) / amplitude, -1, 1))) / Math.PI;
                mae = Math.Abs(mean) >= amplitude ? Math.Abs(mean) : 2 / Math.PI * (Math.Sqrt(amplitude * amplitude - mean * mean) + mean * Math.Asin(mean / amplitude));
            }
            if (fraction > bestComfort) { bestComfort = fraction; comfortRadius = r; }
            if (mae < bestMae) { bestMae = mae; maeRadius = r; }
        }
        // Exact discrete-time filter; nearest sample to a sinusoidal peak is at most dt/2 away.
        double unavoidable = gap > 0 ? gap * Math.Min(1, beta * Math.Cos(omega * world.TimeStep / 2)) : 0;
        double rMin = 0, rMax = 0, requiredSpeed = 0;
        if (gap > 0 && t.FireStrength * (1 - t.OscillationAmplitude) > gap)
        {
            rMin = t.FireRadius * Math.Sqrt(2 * Math.Log(t.FireStrength * (1 - t.OscillationAmplitude) / gap));
            rMax = t.FireRadius * Math.Sqrt(2 * Math.Log(t.FireStrength * (1 + t.OscillationAmplitude) / gap));
            for (int i = 0; i < 10000; i++)
            {
                double phase = Math.Tau * i / 10000, multiplier = 1 + t.OscillationAmplitude * Math.Sin(phase);
                double r = t.FireRadius * Math.Sqrt(2 * Math.Log(t.FireStrength * multiplier / gap));
                requiredSpeed = Math.Max(requiredSpeed, Math.Abs(t.FireRadius * t.FireRadius * t.OscillationAmplitude * omega * Math.Cos(phase) / (r * multiplier)));
            }
        }
        return new(far, gap, beta, unavoidable, comfortRadius, bestComfort, maeRadius, bestMae, rMin, rMax, requiredSpeed);
    }

    public static ThermalAuditReport Run(ExperimentDefinition definition, ThermalAuditProtocol protocol)
    {
        if (protocol.Seeds.Length < 3 || protocol.Seeds.Distinct().Count() != protocol.Seeds.Length || protocol.MinimumPeriods < 2 ||
            protocol.MaximumStationaryComfortFraction is <= 0 or >= 1 || protocol.MinimumActiveComfortFraction is <= 0 or > 1 ||
            protocol.MaximumActiveMae <= 0 || !double.IsFinite(protocol.MaximumActiveMae) || !double.IsFinite(protocol.MaximumStationaryComfortFraction) ||
            !double.IsFinite(protocol.MinimumActiveComfortFraction)) throw new InvalidDataException("Invalid audit protocol.");
        var description = ScenarioSuitability.Describe(definition);
        var analyses = new List<StationaryThermalAnalysis>(); var controls = new List<ThermalControlResult>(); var failures = new List<string>();
        var evaluator = definition.Behavior.Evaluators.SingleOrDefault(e => e.Id == "physical") ?? throw new InvalidDataException("Audit requires independent physical evaluator settings.");
        var profile = new RuntimeSettings(evaluator.Parameters);
        double target = profile.Number("temperatureTarget", 36.6), tolerance = profile.Number("temperatureTolerance", 1);
        for (int condition = 0; condition < description.Conditions.Length; condition++)
        {
            var options = description.Conditions[condition];
            using var inspect = RuntimeSessionFactory.Create(options, new AuditController("stay", options.World, new(), 0, 0));
            var temperature = inspect.Packages.Single(m => m.Id == "temperature");
            var emitters = inspect.Snapshot().Entities.Where(e => e.Components.ContainsKey("temperature.emitter")).ToArray();
            if (temperature.EntryType != "ArtificialLife.Modules.Temperature.RuntimeTemperature" || emitters.Length != 1 ||
                emitters[0].Position.DistanceTo(new(options.World.Width / 2, options.World.Height / 2)) > 1e-12)
                throw new InvalidDataException("This audit's all-position proof requires the existing single central Gaussian temperature module. New physical tasks need a matching audit.");
            var t = temperature.Settings.Deserialize<TemperatureOptions>(PackageCatalog.Json)!;
            var analysis = Analyze(t, options.World, tolerance); analyses.Add(analysis);
            if (Math.Abs(target - t.Target) > 1e-12) failures.Add($"Condition {condition}: physical/evaluation targets differ.");
            if (definition.EvaluationSteps * options.World.TimeStep < protocol.MinimumPeriods * t.OscillationPeriod || options.World.TimeStep > t.OscillationPeriod / 4)
                failures.Add($"Condition {condition}: horizon/sampling insufficient to test persistent comfort.");
            if (analysis.MinimumUnavoidablePeakError <= tolerance || analysis.BestComfortFraction > protocol.MaximumStationaryComfortFraction)
                failures.Add($"Condition {condition}: a fixed position is too comfortable; repair the scenario.");
            if (analysis.MinimumComfortRadius <= 0 || analysis.MaximumComfortRadius >= Math.Min(options.World.Width, options.World.Height) / 2 ||
                analysis.MaximumRequiredRadialSpeed >= options.World.MovementSpeed)
                failures.Add($"Condition {condition}: moving comfort contour lacks a feasible geometric/speed margin.");
            foreach (int seed in protocol.Seeds)
            foreach (string policy in new[] { "stay", "park-best", "random", "local-feedback" })
            {
                var result = Evaluate(options, t, target, tolerance, condition, seed, policy, analysis.BestComfortRadius, definition.EvaluationSteps);
                controls.Add(result);
                if (policy == "local-feedback" && (result.ComfortFraction < protocol.MinimumActiveComfortFraction || result.Mae > protocol.MaximumActiveMae))
                    failures.Add($"Condition {condition}, seed {seed}: active sensor feedback did not meet the preregistered feasibility thresholds.");
                if (policy is "stay" or "park-best" && result.TailComfortFraction > protocol.MaximumStationaryComfortFraction)
                    failures.Add($"Condition {condition}, seed {seed}: passive runtime control stayed too comfortable.");
            }
        }
        if (!description.Factors.SequenceEqual(new[] { "temperature" })) failures.Add("Thermal audit covers only temperature. Other active factors need their own passive/active validation and repairs before scientific comparison.");
        return new(1, failures.Count == 0, description.Fingerprint, protocol, analyses.ToArray(), controls.ToArray(), failures.ToArray(),
            ["Active control is a hand-written feasibility witness, not evidence that DQN/any learner has learned the task.",
             "Grid best-position occupancy/MAE are numerical approximations; the no-permanent-comfort bound is analytical over every fixed position.",
             "Only temperature is validated; additional factors must be audited independently."]);
    }

    private static ThermalControlResult Evaluate(RuntimeOptions options, TemperatureOptions t, double target, double tolerance,
        int condition, int seed, string policy, double radius, int steps)
    {
        using var controller = new AuditController(policy, options.World, t, seed, radius);
        using var runtime = RuntimeSessionFactory.Create(options with { WorldSeed = seed }, controller);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        runtime.TrajectorySink = transition => hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transition.Action, ExperimentJson.Lines) + "\n"));
        int inside = 0, tailInside = 0, tailCount = 0, streak = 0, longest = 0, moved = 0;
        double mae = 0, minRadius = double.PositiveInfinity, maxRadius = 0;
        int settling = (int)Math.Ceiling(2 * t.OscillationPeriod / options.World.TimeStep);
        for (int i = 0; i < steps; i++)
        {
            var before = runtime.Snapshot(); controller.Frame = before;
            runtime.AdvanceOne(false);
            var frame = runtime.Snapshot();
            double error = Math.Abs(frame.Agent.Components["temperature.body"].Read<double>("temperature") - target);
            mae += error;
            if (error <= tolerance) { inside++; streak++; longest = Math.Max(streak, longest); } else streak = 0;
            if (frame.Agent.Position.DistanceTo(before.Agent.Position) > 1e-9) moved++;
            if (i >= settling)
            {
                tailCount++; if (error <= tolerance) tailInside++;
                double distance = frame.Agent.Position.DistanceTo(new(options.World.Width / 2, options.World.Height / 2));
                minRadius = Math.Min(minRadius, distance); maxRadius = Math.Max(maxRadius, distance);
            }
        }
        return new(condition, seed, policy, steps, mae / steps, (double)inside / steps, tailCount == 0 ? 0 : (double)tailInside / tailCount,
            longest, (double)moved / steps, tailCount == 0 ? 0 : maxRadius - minRadius)
        { ActionHash = Convert.ToHexString(hash.GetHashAndReset()) };
    }

    public static int Command(string[] args)
    {
        var values = RuntimeCommands.Arguments(args);
        if (values.Keys.Any(k => k is not ("--config" or "--protocol" or "--output"))) throw new ArgumentException("Unknown thermal audit option.");
        string output = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(output)) throw new IOException("Audit output must be a new directory; preserve unsuccessful checks.");
        var definition = ExperimentDefinition.Load(values["--config"]);
        var protocol = ExperimentJson.Read<ThermalAuditProtocol>(values["--protocol"]);
        Directory.CreateDirectory(output);
        ExperimentJson.Atomic(Path.Combine(output, "configuration.json"), definition);
        ExperimentJson.Atomic(Path.Combine(output, "protocol.json"), protocol);
        var report = Run(definition, protocol);
        ExperimentJson.Atomic(Path.Combine(output, "report.json"), report);
        var review = new ScenarioReview(1, report.Fingerprint, report.Passed,
            [new("temperature", report.Passed ? "requires-adaptation" : "repair-required", "Analytical all-position bound, best-static grid and stationary runtime controls.",
                "Local thermal sensors + body-temperature feedback, no training; see per-seed controls.", "If passivity is sufficient or active feasibility fails, repair common scenario parameters and rerun this audit before comparing methods.")],
            Path.Combine(output, "report.json"))
        { EvidenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "report.json")))) };
        ExperimentJson.Atomic(Path.Combine(output, "review.json"), review);
        Console.WriteLine($"THERMAL_AUDIT passed={report.Passed} conditions={report.Analysis.Length} controls={report.Controls.Length} fingerprint={report.Fingerprint}");
        foreach (string failure in report.Failures) Console.WriteLine(failure);
        return report.Passed ? 0 : 2;
    }

    private sealed class AuditController(string policy, WorldOptions world, TemperatureOptions thermal, int seed, double bestRadius) : IAgentController
    {
        private readonly Random random = new(seed);
        private int bodyType = -1, sensorType = -1;
        public RuntimeFrame? Frame { get; set; }
        public ControllerLimits Limits => new(int.MaxValue, int.MaxValue, int.MaxValue);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions)
        { bodyType = observations.ToList().IndexOf("temperature.body.v1"); sensorType = observations.ToList().IndexOf("temperature.sensor.v1"); }
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
        {
            if (policy == "random") return actions[random.Next(actions.Length)];
            if (policy == "stay") return actions.Single(a => a.Parameters.All(p => p == 0));
            if (policy == "park-best")
            {
                var frame = Frame!;
                double diagonal = Math.Sqrt(world.Width * world.Width + world.Height * world.Height) / 2;
                var goal = new Position(world.Width / 2 + bestRadius * world.Width / (2 * diagonal), world.Height / 2 + bestRadius * world.Height / (2 * diagonal));
                return actions.MinBy(a => new Position(frame.Agent.Position.X + a.Parameters[0] * world.MovementSpeed * world.TimeStep,
                    frame.Agent.Position.Y + a.Parameters[1] * world.MovementSpeed * world.TimeStep).DistanceTo(goal))!;
            }
            // Positive control uses only the existing body and five local field observations.
            double body = state.Single(t => t.Type == bodyType).Features[0] * 20 + thermal.Target;
            var sensors = state.Where(t => t.Type == sensorType).ToArray();
            double Field(float x, float y) => sensors.Single(t => t.Features[1] == x && t.Features[2] == y).Features[0] * 40 + 30;
            double environment = Field(0, 0), gx = (Field(1, 0) - Field(-1, 0)) / (2 * thermal.SensorDistance), gy = (Field(0, 1) - Field(0, -1)) / (2 * thermal.SensorDistance);
            double hxx = (Field(1, 0) + Field(-1, 0) - 2 * environment) / (thermal.SensorDistance * thermal.SensorDistance);
            double hyy = (Field(0, 1) + Field(0, -1) - 2 * environment) / (thermal.SensorDistance * thermal.SensorDistance);
            double aBody = Math.Exp(-thermal.HeatTransfer * world.TimeStep);
            if (Math.Abs(gx) + Math.Abs(gy) + Math.Abs(hxx) + Math.Abs(hyy) < 1e-10 && Math.Abs(body - thermal.Target) > 0.5)
                return actions.First(a => a.Parameters.Any(p => p != 0)); // escape flat/clipped observations rather than camping indefinitely
            return actions.MinBy(action =>
            {
                double dx = action.Parameters[0] * world.MovementSpeed * world.TimeStep, dy = action.Parameters[1] * world.MovementSpeed * world.TimeStep;
                double local = environment + gx * dx + gy * dy + 0.5 * (hxx * dx * dx + hyy * dy * dy);
                double predicted = aBody * body + (1 - aBody) * (local + thermal.HeatProduction / thermal.HeatTransfer);
                return Math.Abs(predicted - thermal.Target) + (action.Parameters.All(p => p == 0) ? 0 : 1e-6);
            })!;
        }
        public void Receive(AgentTransition transition) => throw new NotSupportedException("Audit never learns.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
