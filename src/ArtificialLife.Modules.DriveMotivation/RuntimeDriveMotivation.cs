using ArtificialLife.Core;

namespace ArtificialLife.Modules.DriveMotivation;

/// <summary>Unweighted L2 in the physical modules' normalized homeostatic space.</summary>
public static class NormalizedDriveDistance
{
    public static double Measure(IEnumerable<DriveState> drives)
    {
        double distance = 0;
        foreach (var drive in drives.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (!double.IsFinite(drive.Current) || !double.IsFinite(drive.Target) ||
                !double.IsFinite(drive.Scale) || drive.Scale <= 0)
                throw new InvalidDataException($"Invalid drive: {drive.Key}.");
            double deviation = Math.Abs((drive.Current - drive.Target) / drive.Scale);
            // Scaled accumulation avoids overflow from squaring otherwise finite deviations.
            double largest = Math.Max(distance, deviation), smallest = Math.Min(distance, deviation);
            distance = largest == 0 ? 0 : largest * Math.Sqrt(1 + Math.Pow(smallest / largest, 2));
            if (!double.IsFinite(distance)) throw new InvalidDataException($"Non-finite drive distance: {drive.Key}.");
        }
        return distance;
    }
}

public sealed class RuntimeDriveReduction : IRuntimeModule, IMotivationPolicy
{
    public void Register(ModuleBuilder builder) => builder.Motivation = this;
    public double Evaluate(MotivationInput before, MotivationInput after) =>
        NormalizedDriveDistance.Measure(before.Drives) - NormalizedDriveDistance.Measure(after.Drives);
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}

public sealed class RuntimeDriveLevel : IRuntimeModule, IMotivationPolicy
{
    private double discountFactor = 0.97;
    public void Register(ModuleBuilder builder)
    {
        discountFactor = builder.Settings.Number("discountFactor", 0.97);
        if (!double.IsFinite(discountFactor) || discountFactor is <= 0 or >= 1)
            throw new InvalidDataException("Drive Level requires a discount factor strictly between zero and one.");
        builder.Motivation = this;
    }
    public double Evaluate(MotivationInput before, MotivationInput after)
    {
        _ = NormalizedDriveDistance.Measure(before.Drives);
        return -(1 - discountFactor) * NormalizedDriveDistance.Measure(after.Drives);
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
