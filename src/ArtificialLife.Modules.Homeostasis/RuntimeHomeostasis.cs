using ArtificialLife.Core;

namespace ArtificialLife.Modules.Homeostasis;

public sealed class RuntimeHomeostasis : IRuntimeModule, IMotivationPolicy
{
    private RewardOptions options = new();
    public void Register(ModuleBuilder builder)
    {
        options = new() { ImprovementWeight = builder.Settings.Number("improvementWeight", 0.2), MaximumDriveWeight = builder.Settings.Number("maximumDriveWeight", 10) };
        if (!double.IsFinite(options.MaximumDriveWeight) || options.MaximumDriveWeight <= 0 ||
            !double.IsFinite(options.ImprovementWeight) || options.ImprovementWeight is < 0 or > 1) throw new InvalidDataException("Invalid motivation options.");
        builder.Motivation = this;
    }
    private double Error(DriveState[] drives)
    {
        double sum = 0, weights = 0;
        foreach (var drive in drives)
        {
            if (!double.IsFinite(drive.Current) || !double.IsFinite(drive.Target) || !double.IsFinite(drive.Scale) || drive.Scale <= 0 ||
                !double.IsFinite(drive.Weight) || drive.Weight < 0) throw new InvalidDataException("Invalid drive.");
            double weight = Math.Min(drive.Weight, options.MaximumDriveWeight);
            weights += weight;
            sum += Math.Min(Math.Abs(drive.Current - drive.Target) / drive.Scale, 4) * weight;
        }
        return weights == 0 ? 0 : sum / weights;
    }
    public double Evaluate(MotivationInput before, MotivationInput after)
    {
        double previous = Error(before.Drives), current = Error(after.Drives);
        return Math.Clamp(1 - 2 * Math.Clamp(current, 0, 1) + options.ImprovementWeight * (previous - current), -1, 1);
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
