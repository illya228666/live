using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Physical event/occupancy statistics with no reward, drive target or controller access.</summary>
internal sealed class EcologyBehaviorEvaluator : IBehaviorEvaluator
{
    private readonly double contactRadius, proximityRadius;
    private HashSet<long> foodIds = [];
    private Position? lastPosition;
    private int steps, foodSamples, wildlifeSamples, consumed, contact, encounters, moved;
    private bool wasClose;
    public EcologyBehaviorEvaluator(JsonElement parameters)
    {
        var settings = new RuntimeSettings(parameters);
        contactRadius = settings.Number("contactRadius", 3);
        proximityRadius = settings.Number("boarRadius", 8);
        if (!double.IsFinite(contactRadius) || !double.IsFinite(proximityRadius) || contactRadius <= 0 || proximityRadius < contactRadius)
            throw new InvalidDataException("Invalid ecology evaluation radii.");
    }
    public void Observe(RuntimeFrame frame, HomeostasisReport state)
    {
        steps++;
        if (lastPosition is Position previous && previous.DistanceTo(frame.Agent.Position) > 1e-9) moved++;
        lastPosition = frame.Agent.Position;
        if (frame.ModuleState.ContainsKey("food"))
        {
            foodSamples++;
            var current = frame.Entities.Where(e => e.Components.ContainsKey("food.apple")).Select(e => e.Id).ToHashSet();
            consumed += foodIds.Count(id => !current.Contains(id)); foodIds = current;
        }
        var boars = frame.Entities.Where(e => e.Components.ContainsKey("wildlife.boar")).ToArray();
        if (boars.Length > 0)
        {
            wildlifeSamples++;
            double distance = boars.Min(e => e.Position.DistanceTo(frame.Agent.Position));
            bool close = distance < proximityRadius;
            if (close && !wasClose) encounters++;
            wasClose = close;
            if (distance < contactRadius) contact++;
        }
    }
    public IReadOnlyDictionary<string, BehaviorMetric> Complete() => new Dictionary<string, BehaviorMetric>
    {
        ["food.consumed"] = new(foodSamples == 0 ? null : consumed, foodSamples, "apples", true, "Total observed apple disappearances; valid for this scenario's consumption-only removal, not absorbed nutrition."),
        ["wildlife.contact-proxy"] = new(wildlifeSamples == 0 ? null : (double)contact / wildlifeSamples, wildlifeSamples, "fraction", false, "Sampled nearest distance below the independent contact radius; proxy, not actual damage or survival.", true),
        ["wildlife.encounters"] = new(wildlifeSamples == 0 ? null : encounters, wildlifeSamples, "entries", false, "Entries into the evaluation proximity zone; descriptive frequency, not accumulated harm.", true),
        ["movement.fraction"] = new(steps < 2 ? null : (double)moved / (steps - 1), Math.Max(0, steps - 1), "fraction", false, "Fraction of observed transitions with displacement; descriptive, not a success criterion.")
    };
}
