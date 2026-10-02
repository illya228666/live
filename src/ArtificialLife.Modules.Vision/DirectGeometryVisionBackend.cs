using ArtificialLife.Core;

namespace ArtificialLife.Modules.Vision;

/// <summary>All appearances are visible; no range, field of view or occlusion filtering.</summary>
public sealed class DirectGeometryVisionBackend : IVisionBackend
{
    public IEnumerable<VisualPerception> Perceive(WorldState world, VisionObserver observer)
    {
        if (!double.IsFinite(observer.Position.X) || !double.IsFinite(observer.Position.Y) ||
            !double.IsFinite(observer.OrientationRadians))
            throw new ArgumentException("Observer pose must be finite.");
        double cosine = Math.Cos(observer.OrientationRadians);
        double sine = Math.Sin(observer.OrientationRadians);
        foreach (Entity entity in world.Entities)
        {
            if (!entity.TryGet<VisualAppearance>(out var appearance)) continue;
            double dx = entity.Position.X - observer.Position.X;
            double dy = entity.Position.Y - observer.Position.Y;
            double forward = dx * cosine + dy * sine;
            double lateral = -dx * sine + dy * cosine;
            double distance = observer.Position.DistanceTo(entity.Position);
            // At coincidence the bearing is defined as zero, angular size reaches pi.
            double direction = distance == 0 ? 0 : Math.Atan2(lateral, forward);
            double size = 2 * Math.Atan2(appearance.Diameter / 2, distance);
            yield return new VisualPerception(appearance.Type, direction, distance, size);
        }
    }
}
