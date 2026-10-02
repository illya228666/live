namespace ArtificialLife.Modules.Temperature;

/// <summary>Thermal properties of any world entity, independent of its appearance.</summary>
public sealed record HeatEmitter
{
    public double BaseStrength { get; }
    public double Radius { get; }
    public double OscillationPeriod { get; }
    public double OscillationAmplitude { get; }

    public HeatEmitter(double baseStrength, double radius, double oscillationPeriod, double oscillationAmplitude)
    {
        if (!double.IsFinite(baseStrength) || baseStrength <= 0 || !double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(oscillationPeriod) || oscillationPeriod <= 0 ||
            !double.IsFinite(oscillationAmplitude) || oscillationAmplitude is < 0 or >= 1)
        {
            throw new ArgumentException("Invalid heat emitter coefficients.");
        }
        BaseStrength = baseStrength;
        Radius = radius;
        OscillationPeriod = oscillationPeriod;
        OscillationAmplitude = oscillationAmplitude;
    }

    public double Strength(double time) => BaseStrength *
        (1 + OscillationAmplitude * Math.Sin(2 * Math.PI * time / OscillationPeriod));
}
