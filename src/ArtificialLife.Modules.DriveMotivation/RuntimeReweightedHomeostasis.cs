using ArtificialLife.Core;

namespace ArtificialLife.Modules.DriveMotivation;

/// <summary>Motivation-only drive weighting; no physical drive, observation or target changes.</summary>
public sealed class RuntimeReweightedHomeostasis : IRuntimeModule, IMotivationPolicy
{
    private readonly RuntimePowerHomeostasis inner = new();
    private readonly Dictionary<string, double> weights = new(StringComparer.Ordinal);

    public void Register(ModuleBuilder builder)
    {
        inner.Register(builder);
        foreach (var property in builder.Manifest.Settings.EnumerateObject().Where(p => p.Name.Equals("driveWeights", StringComparison.OrdinalIgnoreCase)))
        {
            if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("driveWeights must be an object.");
            foreach (var item in property.Value.EnumerateObject())
            {
                double value = item.Value.GetDouble();
                if (!double.IsFinite(value) || value <= 0 || value > 10 || !weights.TryAdd(item.Name, value))
                    throw new InvalidDataException("Drive multipliers must be unique and in(0,10].");
            }
        }
        builder.Motivation = this;
    }
    private MotivationInput Reweight(MotivationInput input) => input with
    {
        Drives = input.Drives.Select(d => d with { Weight = d.Weight * weights.GetValueOrDefault(d.Key, 1) }).ToArray()
    };
    public double Evaluate(MotivationInput before, MotivationInput after) => inner.Evaluate(Reweight(before), Reweight(after));
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
