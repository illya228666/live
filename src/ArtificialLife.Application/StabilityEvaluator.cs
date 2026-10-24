using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Temporal physical measurements only. No reward, actions or controller state.</summary>
internal sealed class StabilityEvaluator : IBehaviorEvaluator
{
    private readonly double target, tolerance, hungerThreshold, radius;
    private readonly Moments temperature = new(), hunger = new();
    private readonly Run temperatureOutside = new(), hungerAbove = new(), proximity = new();
    private Position? previousPosition, previousDisplacement;
    private int reversalPairs, reversals;

    public StabilityEvaluator(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Evaluator settings must be an object.");
        var settings = new RuntimeSettings(parameters);
        target = settings.Number("temperatureTarget", 36.6);
        tolerance = settings.Number("temperatureTolerance", 1);
        hungerThreshold = settings.Number("hungerThreshold", 0.8);
        radius = settings.Number("boarRadius", 10);
        if (!double.IsFinite(target) || !double.IsFinite(tolerance) || tolerance <= 0 ||
            !double.IsFinite(hungerThreshold) || hungerThreshold is < 0 or > 1 || !double.IsFinite(radius) || radius <= 0)
            throw new InvalidDataException("Invalid stability evaluation profile.");
    }

    public void Observe(RuntimeFrame frame, HomeostasisReport state)
    {
        if (frame.Agent.Components.TryGetValue("temperature.body", out var body))
        {
            double value = body.Read<double>("temperature");
            temperature.Add(value); temperatureOutside.Add(Math.Abs(value - target) > tolerance);
        }
        else temperatureOutside.Break();
        if (frame.Agent.Components.TryGetValue("hunger.body", out var data))
        {
            double value = data.Read<double>("level");
            hunger.Add(value); hungerAbove.Add(value > hungerThreshold);
        }
        else hungerAbove.Break();
        var boars = frame.Entities.Where(e => e.Components.ContainsKey("wildlife.boar")).ToArray();
        if (boars.Length > 0) proximity.Add(boars.Min(e => e.Position.DistanceTo(frame.Agent.Position)) < radius);
        else proximity.Break();

        if (previousPosition is Position previous)
        {
            var displacement = new Position(frame.Agent.Position.X - previous.X, frame.Agent.Position.Y - previous.Y);
            bool moving = displacement.X != 0 || displacement.Y != 0;
            if (moving && previousDisplacement is Position last)
            {
                reversalPairs++;
                if (displacement.X * last.X + displacement.Y * last.Y < 0) reversals++;
            }
            // Stationary steps break adjacency; do not bridge pauses.
            previousDisplacement = moving ? displacement : null;
        }
        previousPosition = frame.Agent.Position;
    }

    public IReadOnlyDictionary<string, BehaviorMetric> Complete() => new Dictionary<string, BehaviorMetric>
    {
        ["temperature.temporal-sd"] = temperature.Metric("°C", "Population SD of body temperature within this evaluation episode."),
        ["hunger.temporal-sd"] = hunger.Metric("ratio", "Population SD of hunger within this evaluation episode; interpret with its mean."),
        ["temperature.outside-longest"] = temperatureOutside.Metric("Longest consecutive measured run outside the temperature band."),
        ["hunger.above-longest"] = hungerAbove.Metric("Longest consecutive measured run above the hunger threshold."),
        ["wildlife.proximity-longest"] = proximity.Metric("Longest consecutive measured run inside the boar radius; proximity proxy.", true),
        ["movement.reversal"] = new(reversalPairs == 0 ? null : (double)reversals / reversalPairs, reversalPairs,
            "fraction", false, "Turns over 90 degrees among adjacent nonzero observed displacements; descriptive oscillation proxy, not a success criterion.", true)
    };

    private sealed class Moments
    {
        private int count;
        private double mean, m2;
        public void Add(double value)
        {
            if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite stability measurement.");
            count++; double delta = value - mean; mean += delta / count; m2 += delta * (value - mean);
        }
        public BehaviorMetric Metric(string unit, string description) => new(count == 0 ? null : Math.Sqrt(Math.Max(0, m2 / count)),
            count, unit, false, description);
    }
    private sealed class Run
    {
        private int count, current, longest;
        public void Add(bool condition) { count++; current = condition ? current + 1 : 0; longest = Math.Max(longest, current); }
        public void Break() => current = 0;
        public BehaviorMetric Metric(string description, bool proxy = false) => new(count == 0 ? null : longest, count, "steps", false, description, proxy);
    }
}
