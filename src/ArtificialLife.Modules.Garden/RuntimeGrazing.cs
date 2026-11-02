using ArtificialLife.Core;

namespace ArtificialLife.Modules.Garden;

/// <summary>Hunger steers existing wildlife through its normal turning physics; grazing never deletes a colony.</summary>
public sealed class RuntimeGrazing : IRuntimeModule
{
    private const string Grazer = "grazing.animal", Scar = "grazing.scar";
    private const double BiteRadius = 4, SearchRadius = 32, BiteSize = 0.42;

    public void Register(ModuleBuilder builder)
    {
        builder.Components.Add(new(Grazer, 1, (_, e) => e.Components.ContainsKey("wildlife.boar"), (_, _, data) =>
        {
            data.Write("hunger", 0.6); data.Write("biteInSeconds", 0.0);
        }));
        builder.Components.Add(new(Scar, 1, (_, e) => e.Components.ContainsKey("flora.plant"), (_, _, data) => data.Write("pressure", 0.0)));
        builder.Systems.Add(new("grazing.feed", Update, ["flora.grow", "wildlife.move"]));
        builder.Metrics.Add(frame => new[] {
            KeyValuePair.Create("bites", (double)frame.ModuleState["grazing"].Read<long>("bites")),
            KeyValuePair.Create("hungry", (double)frame.Entities.Count(e => e.Components.TryGetValue(Grazer, out var data) && data.Read<double>("hunger") >= 0.35)),
            KeyValuePair.Create("grazedPlants", (double)frame.Entities.Count(e => e.Components.TryGetValue(Scar, out var data) && data.Read<double>("pressure") > 0.1)) });
    }

    public void Migrate(RuntimeWorld world, ModuleManifest? previous) => world.State("grazing");

    private static void Update(RuntimeWorld world)
    {
        double dt = world.Options.TimeStep;
        var plants = world.Entities.Where(e => e.Components.ContainsKey("flora.plant")).ToArray();
        foreach (var plant in plants)
            if (plant.Components.TryGetValue(Scar, out var scar)) scar.Write("pressure", scar.Read<double>("pressure") * Math.Exp(-dt / 16));
        foreach (var animal in world.Entities.Where(e => e.Components.ContainsKey(Grazer)))
        {
            var data = animal.Require(Grazer);
            double hunger = Math.Min(1, data.Read<double>("hunger") + 0.012 * dt);
            double biteIn = Math.Max(0, data.Read<double>("biteInSeconds") - dt);
            var food = plants.Where(e => e.Require("flora.plant").Read<double>("biomass") >= 0.45 &&
                e.Position.DistanceTo(animal.Position) <= SearchRadius).MinBy(e => e.Position.DistanceTo(animal.Position));
            if (hunger >= 0.35 && food is not null)
            {
                // Only choose a direction; Wildlife still owns speed, turn limits, collisions and RNG.
                animal.Require("wildlife.boar").Write("targetHeading", Math.Atan2(food.Position.Y - animal.Position.Y, food.Position.X - animal.Position.X));
                if (biteIn <= 0 && food.Position.DistanceTo(animal.Position) <= BiteRadius)
                {
                    var plant = food.Require("flora.plant");
                    plant.Write("biomass", Math.Max(0.12, plant.Read<double>("biomass") - BiteSize));
                    food.Require(Scar).Write("pressure", 1.0);
                    hunger = Math.Max(0, hunger - 0.38);
                    biteIn = 4;
                    var state = world.State("grazing"); state.Write("bites", state.Read<long>("bites") + 1);
                }
            }
            data.Write("hunger", hunger); data.Write("biteInSeconds", biteIn);
        }
    }
}
