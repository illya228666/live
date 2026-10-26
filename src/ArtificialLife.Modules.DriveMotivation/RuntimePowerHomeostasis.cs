using ArtificialLife.Core;

namespace ArtificialLife.Modules.DriveMotivation;

/// <summary>Weighted power mean of normalized deficits; p=1 matches weighted homeostasis.</summary>
public sealed class RuntimePowerHomeostasis : IRuntimeModule, IMotivationPolicy
{
    private double exponent = 1, maximumDriveWeight = 10, improvementWeight = 0.2;

    public void Register(ModuleBuilder builder)
    {
        exponent = builder.Settings.Number("exponent", 1);
        maximumDriveWeight = builder.Settings.Number("maximumDriveWeight", 10);
        improvementWeight = builder.Settings.Number("improvementWeight", 0.2);
        if (!double.IsFinite(exponent) || exponent is < 1 or > 16 ||
            !double.IsFinite(maximumDriveWeight) || maximumDriveWeight <= 0 ||
            !double.IsFinite(improvementWeight) || improvementWeight is < 0 or > 1)
            throw new InvalidDataException("Invalid power-homeostasis settings.");
        builder.Motivation = this;
    }

    private double Error(DriveState[] drives)
    {
        double sum = 0, weights = 0;
        foreach (var drive in drives)
        {
            if (!double.IsFinite(drive.Current) || !double.IsFinite(drive.Target) ||
                !double.IsFinite(drive.Scale) || drive.Scale <= 0 ||
                !double.IsFinite(drive.Weight) || drive.Weight < 0)
                throw new InvalidDataException("Invalid drive.");
            double weight = Math.Min(drive.Weight, maximumDriveWeight);
            double deficit = Math.Min(Math.Abs(drive.Current - drive.Target) / drive.Scale, 4);
            weights += weight;
            sum += Math.Pow(deficit, exponent) * weight;
        }
        return weights == 0 ? 0 : Math.Pow(sum / weights, 1 / exponent);
    }

    public double Evaluate(MotivationInput before, MotivationInput after)
    {
        double previous = Error(before.Drives), current = Error(after.Drives);
        return Math.Clamp(1 - 2 * Math.Clamp(current, 0, 1) + improvementWeight * (previous - current), -1, 1);
    }

    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
