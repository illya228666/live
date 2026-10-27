using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record BehaviorMetric(double? Value, int Samples, string Unit, bool HigherIsBetter, string Description, bool Proxy = false);
public sealed record EvaluatorSelection
{
    public string Id { get; init; } = "drives";
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
}
public sealed record BehaviorCriterion(string Scenario, string Metric, double? Minimum = null, double? Maximum = null);
public sealed record BehaviorProfile
{
    public EvaluatorSelection[] Evaluators { get; init; } = [new(), new() { Id = "physical" }];
    public BehaviorCriterion[] Criteria { get; init; } = [];
}

/// <summary>Behavioral observation deliberately has no reward or controller input.</summary>
public interface IBehaviorEvaluator
{
    void Observe(RuntimeFrame frame, HomeostasisReport state);
    IReadOnlyDictionary<string, BehaviorMetric> Complete();
}

public static class BehaviorEvaluators
{
    private static readonly Dictionary<string, Func<JsonElement, IBehaviorEvaluator>> factories = new(StringComparer.Ordinal)
    {
        ["drives"] = _ => new DriveEvaluator(),
        ["physical"] = settings => new PhysicalEvaluator(settings),
        ["stability-v1"] = settings => new StabilityEvaluator(settings),
        ["ecology-v1"] = settings => new EcologyBehaviorEvaluator(settings),
        ["interaction-v1"] = _ => new InteractionBehaviorEvaluator(),
        ["signal-diagnostic-v1"] = _ => new SignalDiagnosticEvaluator()
    };
    public static void Register(string id, Func<JsonElement, IBehaviorEvaluator> factory)
    {
        if (string.IsNullOrWhiteSpace(id) || !factories.TryAdd(id, factory)) throw new ArgumentException("Evaluator identity must be unique.");
    }
    public static IBehaviorEvaluator[] Create(BehaviorProfile profile) => profile.Evaluators.Select(selection =>
        factories.TryGetValue(selection.Id, out var factory) ? factory(selection.Parameters)
            : throw new InvalidDataException($"Unknown behavioral evaluator: {selection.Id}.")).ToArray();

    public static Dictionary<string, BehaviorMetric> Complete(IEnumerable<IBehaviorEvaluator> evaluators) =>
        evaluators.SelectMany(e => e.Complete()).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    public static string Assess(BehaviorCriterion[] criteria, Func<string, string, double?> metric)
    {
        if (criteria.Length == 0) return "not-assessed";
        return criteria.All(c => metric(c.Scenario, c.Metric) is double value && double.IsFinite(value) &&
            (c.Minimum is null || value >= c.Minimum) && (c.Maximum is null || value <= c.Maximum)) ? "passed" : "failed";
    }

    private sealed class Sum(string unit, bool higher, string description, bool proxy = false)
    {
        private double total;
        private int count;
        public void Add(double value)
        {
            if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite behavioral measurement.");
            total += value; count++;
        }
        public BehaviorMetric Metric() => new(count == 0 ? null : total / count, count, unit, higher, description, proxy);
    }

    private sealed class DriveEvaluator : IBehaviorEvaluator
    {
        private readonly Dictionary<string, Sum> sums = new(StringComparer.Ordinal);
        public void Observe(RuntimeFrame frame, HomeostasisReport state)
        {
            Add("diagnostic.balance", state.Balance, true, "Weighted drive satisfaction; diagnostic, not a universal success score.");
            foreach (var drive in state.Drives.Values)
            {
                Add($"drive.{drive.Key}.error", drive.NormalizedError, false, "Mean normalized drive error; uses the physical module's target/scale.");
                Add($"drive.{drive.Key}.satisfaction", drive.Satisfaction, true, "Mean drive satisfaction, independent of reward.");
                Add($"drive.{drive.Key}.current", drive.Current, false, "Mean raw drive value; direction is descriptive only.", drive.Unit);
            }
        }
        private void Add(string key, double value, bool higher, string description, string unit = "ratio")
        {
            if (!sums.TryGetValue(key, out var sum)) sums.Add(key, sum = new(unit, higher, description, key.StartsWith("drive.safety.", StringComparison.Ordinal)));
            sum.Add(value);
        }
        public IReadOnlyDictionary<string, BehaviorMetric> Complete() => sums.ToDictionary(p => p.Key, p => p.Value.Metric(), StringComparer.Ordinal);
    }

    private sealed class PhysicalEvaluator : IBehaviorEvaluator
    {
        private readonly double target, tolerance, hungerThreshold, radius;
        private readonly Dictionary<string, Sum> sums = new(StringComparer.Ordinal)
        {
            ["temperature.mae"] = new("°C", false, "Mean absolute body-temperature error against the evaluation target."),
            ["temperature.outside"] = new("fraction", false, "Fraction of measured steps outside the evaluation temperature band."),
            ["hunger.mean"] = new("ratio", false, "Mean physiological hunger level."),
            ["hunger.above"] = new("fraction", false, "Fraction of measured steps above the evaluation hunger threshold."),
            ["wildlife.distance"] = new("world-units", true, "Nearest boar distance; proximity proxy, not actual harm or survival.", true),
            ["wildlife.proximity"] = new("fraction", false, "Fraction of measured steps within the evaluation boar radius; proximity proxy.", true)
        };
        public PhysicalEvaluator(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Evaluator settings must be an object.");
            var settings = new RuntimeSettings(parameters);
            target = settings.Number("temperatureTarget", 36.6);
            tolerance = settings.Number("temperatureTolerance", 1);
            hungerThreshold = settings.Number("hungerThreshold", 0.8);
            radius = settings.Number("boarRadius", 10);
            if (!double.IsFinite(target) || !double.IsFinite(tolerance) || tolerance <= 0 || !double.IsFinite(hungerThreshold) ||
                hungerThreshold is < 0 or > 1 || !double.IsFinite(radius) || radius <= 0)
                throw new InvalidDataException("Invalid physical evaluation profile.");
        }
        public void Observe(RuntimeFrame frame, HomeostasisReport state)
        {
            if (frame.Agent.Components.TryGetValue("temperature.body", out var body))
            {
                double error = Math.Abs(body.Read<double>("temperature") - target);
                sums["temperature.mae"].Add(error);
                sums["temperature.outside"].Add(error > tolerance ? 1 : 0);
            }
            if (frame.Agent.Components.TryGetValue("hunger.body", out var hunger))
            {
                double level = hunger.Read<double>("level");
                sums["hunger.mean"].Add(level);
                sums["hunger.above"].Add(level > hungerThreshold ? 1 : 0);
            }
            var boars = frame.Entities.Where(e => e.Components.ContainsKey("wildlife.boar")).ToArray();
            if (boars.Length > 0)
            {
                double distance = boars.Min(e => e.Position.DistanceTo(frame.Agent.Position));
                sums["wildlife.distance"].Add(distance);
                sums["wildlife.proximity"].Add(distance < radius ? 1 : 0);
            }
        }
        public IReadOnlyDictionary<string, BehaviorMetric> Complete() => sums.ToDictionary(p => p.Key, p => p.Value.Metric(), StringComparer.Ordinal);
    }
}
