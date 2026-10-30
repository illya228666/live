using ArtificialLife.Core;

namespace ArtificialLife.Modules.Hunger;

public sealed class RuntimeHunger : IRuntimeModule
{
    public void Register(ModuleBuilder builder)
    {
        var options = new HungerOptions { GrowthPerSecond = builder.Settings.Number("growthPerSecond", 0.001), EatRadius = builder.Settings.Number("eatRadius", 2), DriveWeight = builder.Settings.Number("driveWeight", 1) };
        options.Validate();
        int type = builder.Observations.Register("hunger.body.v1");
        builder.Components.Add(new("hunger.body", 1, (w, e) => w.HasAgent && e.Id == w.Agent.Id, (_, _, data) => data.Write("level", 0.0)));
        builder.Components.Add(new("nutrition", 1, (_, _) => false, (_, _, _) => { }));
        builder.Systems.Add(new("hunger.update", world =>
        {
            if (!world.HasAgent) return;
            var data = world.Agent.Require("hunger.body");
            double hunger = Math.Clamp(data.Read<double>("level") + options.GrowthPerSecond * world.Options.TimeStep, 0, 1);
            for (int index = world.Entities.Count - 1; index >= 0; index--)
            {
                var entity = world.Entities[index];
                if (!entity.Components.TryGetValue("nutrition", out var nutrition) || world.Agent.Position.DistanceTo(entity.Position) > options.EatRadius) continue;
                hunger = Math.Max(0, hunger - nutrition.Read<double>("reduction"));
                world.Entities.RemoveAt(index);
            }
            data.Write("level", hunger);
        }, []));
        builder.Sensors.Add(world => !world.HasAgent ? [] : [new(type, [(float)world.Agent.Require("hunger.body").Read<double>("level")])]);
        builder.Drives.Add(world => !world.HasAgent ? [] : [new("hunger", world.Agent.Require("hunger.body").Read<double>("level"), 0, 1, options.DriveWeight)]);
        builder.Descriptors.Add(new("hunger", "Hunger"));
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
