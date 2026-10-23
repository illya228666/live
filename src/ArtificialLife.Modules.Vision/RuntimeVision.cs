using ArtificialLife.Core;

namespace ArtificialLife.Modules.Vision;

public sealed class RuntimeVision : IRuntimeModule
{
    public void Register(ModuleBuilder builder)
    {
        int maximum = builder.Settings.Integer("maxPerAppearance", int.MaxValue);
        if (maximum < 1) throw new InvalidDataException("MaxPerAppearance must be positive.");
        builder.Components.Add(new("vision.appearance", 1, (_, _) => false, (_, _, _) => { }));
        builder.Sensors.Add(world =>
        {
            double heading = world.Agent.Components.TryGetValue("movement.heading", out var h) ? h.Read<double>("radians") : 0;
            double cosine = Math.Cos(heading), sine = Math.Sin(heading);
            var result = new List<ObservationToken>();
            IEnumerable<RuntimeEntity> visible = world.Entities.Where(e => e.Components.ContainsKey("vision.appearance"));
            if (maximum != int.MaxValue) visible = visible.GroupBy(e => e.Require("vision.appearance").Read<string>("type"))
                .SelectMany(group => group.OrderBy(e => e.Position.DistanceTo(world.Agent.Position)).ThenBy(e => e.Id).Take(maximum));
            foreach (var entity in visible)
            {
                if (!entity.Components.TryGetValue("vision.appearance", out var appearance)) continue;
                double diameter = appearance.Read<double>("diameter");
                if (!double.IsFinite(diameter) || diameter <= 0) throw new InvalidDataException("Invalid appearance diameter.");
                double dx = entity.Position.X - world.Agent.Position.X, dy = entity.Position.Y - world.Agent.Position.Y;
                double distance = entity.Position.DistanceTo(world.Agent.Position);
                double direction = distance == 0 ? 0 : Math.Atan2(-dx * sine + dy * cosine, dx * cosine + dy * sine);
                result.Add(new(builder.Observations.Resolve(appearance.Read<string>("type")),
                    [(float)Math.Cos(direction), (float)Math.Sin(direction), (float)(distance / (1 + distance)), (float)(2 * Math.Atan2(diameter / 2, distance) / Math.PI)]));
            }
            return result;
        });
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
