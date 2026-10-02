using ArtificialLife.Core;

namespace ArtificialLife.Modules.Vision;

/// <summary>Neutral visible shape, not an object's purpose or physical effect.</summary>
public enum AppearanceType { Disc }

public sealed record VisualAppearance
{
    public AppearanceType Type { get; }
    public double Diameter { get; }

    public VisualAppearance(AppearanceType type, double diameter)
    {
        if (!Enum.IsDefined(type) || !double.IsFinite(diameter) || diameter <= 0)
            throw new ArgumentException("Appearance needs a known shape and a positive finite diameter.");
        Type = type;
        Diameter = diameter;
    }
}

/// <summary>Backend input only. Heading maps the observer's forward axis into world space.</summary>
public readonly record struct VisionObserver(Position Position, double OrientationRadians);

/// <summary>Backend output: bearing from forward in radians; distance in simulation units;
/// apparent size is angular diameter in radians. No world coordinates or entity identity.</summary>
public readonly record struct VisualPerception(AppearanceType AppearanceType, double DirectionRadians,
    double Distance, double ApparentSize);

public interface IVisionBackend
{
    IEnumerable<VisualPerception> Perceive(WorldState world, VisionObserver observer);
}
