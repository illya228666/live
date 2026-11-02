using ArtificialLife.Core;

namespace ArtificialLife.Modules.Garden;

/// <summary>Edible, heat-sensitive colonies; every clock, seed and growth value travels with the world.</summary>
public sealed class RuntimeFlora : IRuntimeModule
{
    public const string Plant = "flora.plant";
    private int maximumPlants;
    private double growthPerSecond, seedIntervalSeconds;

    public void Register(ModuleBuilder builder)
    {
        maximumPlants = builder.Settings.Integer("maximumPlants", 48);
        growthPerSecond = builder.Settings.Number("growthPerSecond", 0.018);
        seedIntervalSeconds = builder.Settings.Number("seedIntervalSeconds", 24);
        if (maximumPlants < 1 || !double.IsFinite(growthPerSecond) || growthPerSecond <= 0 ||
            !double.IsFinite(seedIntervalSeconds) || seedIntervalSeconds <= 0)
            throw new InvalidDataException("Invalid flora settings.");
        builder.Components.Add(new(Plant, 1, (_, e) => e.Components.ContainsKey("food.apple"), (_, _, data) =>
        {
            data.Write("biomass", 0.2);
            data.Write("seedInSeconds", seedIntervalSeconds);
        }));
        builder.Systems.Add(new("flora.grow", Update, ["food.spawn"]));
        builder.Metrics.Add(frame =>
        {
            var plants = frame.Entities.Where(e => e.Components.ContainsKey(Plant)).ToArray();
            return new[] { KeyValuePair.Create("plants", (double)plants.Length),
                KeyValuePair.Create("biomass", plants.Sum(e => e.Components[Plant].Read<double>("biomass"))),
                KeyValuePair.Create("seedlings", (double)frame.ModuleState["flora"].Read<long>("seedlings")) };
        });
    }

    public void Migrate(RuntimeWorld world, ModuleManifest? previous) => world.State("flora");

    private void Update(RuntimeWorld world)
    {
        var plants = world.Entities.Where(e => e.Components.ContainsKey(Plant)).ToArray();
        int count = plants.Length;
        foreach (var plant in plants)
        {
            var data = plant.Require(Plant);
            double heat = world.Entities.Where(e => e.Components.ContainsKey("temperature.emitter")).Sum(e =>
            {
                var emitter = e.Require("temperature.emitter");
                double distance = plant.Position.DistanceTo(e.Position), radius = emitter.Read<double>("radius");
                return emitter.Read<double>("strength") * Math.Exp(-distance * distance / (2 * radius * radius));
            });
            int neighbors = plants.Count(e => e.Id != plant.Id && e.Position.DistanceTo(plant.Position) < 7);
            double suitability = Math.Clamp(1 - heat / 32, 0.05, 1) / (1 + neighbors * 0.3);
            double biomass = Math.Min(1, data.Read<double>("biomass") + growthPerSecond * suitability * world.Options.TimeStep);
            double remaining = data.Read<double>("seedInSeconds") - world.Options.TimeStep;
            data.Write("suitability", suitability);
            if (biomass >= 0.85 && remaining <= 0 && count < maximumPlants)
            {
                double angle = world.Random("flora") * Math.Tau;
                double distance = 6 + world.Random("flora") * 8;
                var position = new Position(plant.Position.X + Math.Cos(angle) * distance, plant.Position.Y + Math.Sin(angle) * distance);
                if (world.Contains(position) && world.Entities.All(e => !e.Components.ContainsKey(Plant) || e.Position.DistanceTo(position) >= 5))
                {
                    var seed = world.CreateEntity(position);
                    seed.Components["food.apple"] = new();
                    seed.Components["nutrition"] = new(); seed.Require("nutrition").Write("reduction", 0.2);
                    seed.Components["vision.appearance"] = new();
                    seed.Require("vision.appearance").Write("type", "vision.appearance.diamond.v1");
                    seed.Require("vision.appearance").Write("diameter", 2.0);
                    count++;
                    var state = world.State("flora"); state.Write("seedlings", state.Read<long>("seedlings") + 1);
                    biomass -= 0.18;
                }
                remaining = seedIntervalSeconds;
            }
            data.Write("biomass", biomass);
            data.Write("seedInSeconds", Math.Max(-seedIntervalSeconds, remaining));
        }
    }
}
