using ArtificialLife.Core;

namespace ArtificialLife.Modules.Garden;

/// <summary>Persistent moths connect mature flowers to grazed colonies through feeding and pollen transfer.</summary>
public sealed class RuntimePollination : IRuntimeModule
{
    private const string Moth = "pollination.moth", Bloom = "pollination.bloom";
    private const int Population = 6;
    private const double FlightSpeed = 6, VisitRadius = 1.5, PickupMass = 0.04;

    public void Register(ModuleBuilder builder)
    {
        builder.Components.Add(new(Moth, 2, (_, _) => false, (_, _, _) => { }));
        builder.Components.Add(new(Bloom, 1, (_, e) => e.Components.ContainsKey("flora.plant"), (_, _, data) => data.Write("glow", 0.0)));
        builder.Observations.Register("vision.appearance.moth.v1");
        builder.Systems.Add(new("pollination.fly", Update, ["grazing.feed"]));
        builder.Metrics.Add(frame => new[] {
            KeyValuePair.Create("moths", (double)frame.Entities.Count(e => e.Components.ContainsKey(Moth))),
            KeyValuePair.Create("visits", (double)frame.ModuleState["pollination"].Read<long>("visits")),
            KeyValuePair.Create("fertilizations", (double)frame.ModuleState["pollination"].Read<long>("fertilizations")),
            KeyValuePair.Create("recoveryAdded", frame.ModuleState["pollination"].Read<double>("recoveryAdded")),
            KeyValuePair.Create("cargoInitial", frame.ModuleState["pollination"].Read<double>("cargoInitial")),
            KeyValuePair.Create("cargoCollected", frame.ModuleState["pollination"].Read<double>("cargoCollected")),
            KeyValuePair.Create("cargoDeposited", frame.ModuleState["pollination"].Read<double>("cargoDeposited")),
            KeyValuePair.Create("cargoOnBoard", frame.Entities.Sum(e => e.Components.TryGetValue(Moth, out var data) ? data.Read<double>("cargo") : 0)) });
    }

    public void Migrate(RuntimeWorld world, ModuleManifest? previous)
    {
        var state = world.State("pollination");
        if (state.Version == 2) return;
        double initialCargo = 0;
        foreach (var moth in world.Entities.Where(e => e.Components.ContainsKey(Moth)))
        {
            var data = moth.Require(Moth);
            // Legacy collection already deducted this mass from the donor.
            double cargo = data.Read<bool>("pollen") ? PickupMass : 0;
            data.Write("cargo", cargo);
            data.Values.Remove("pollen");
            data.Version = 2;
            initialCargo += cargo;
        }
        state.Write("cargoInitial", initialCargo);
        state.Write("cargoCollected", 0.0);
        state.Write("cargoDeposited", 0.0);
        state.Version = 2;
        if (previous is not null) return;
        var plants = world.Entities.Where(e => e.Components.ContainsKey("flora.plant")).ToArray();
        for (int i = 0; i < Population; i++)
        {
            var position = plants.Length > 0 ? plants[(int)(world.Random("pollination") * plants.Length)].Position : new Position(world.Options.Width / 2, world.Options.Height / 2);
            var moth = world.CreateEntity(position);
            moth.Components[Moth] = new() { Version = 2 };
            moth.Require(Moth).Write("target", 0L);
            moth.Require(Moth).Write("lastFlower", 0L);
            moth.Require(Moth).Write("cargo", 0.0);
            moth.Require(Moth).Write("restInSeconds", i * 0.4);
            moth.Require(Moth).Write("heading", world.Random("pollination") * Math.Tau);
            moth.Components["vision.appearance"] = new();
            moth.Require("vision.appearance").Write("type", "vision.appearance.moth.v1");
            moth.Require("vision.appearance").Write("diameter", 1.5);
        }
    }

    private static void Update(RuntimeWorld world)
    {
        double dt = world.Options.TimeStep;
        var plants = world.Entities.Where(e => e.Components.ContainsKey("flora.plant")).ToArray();
        foreach (var plant in plants)
            plant.Require(Bloom).Write("glow", plant.Require(Bloom).Read<double>("glow") * Math.Exp(-dt / 8));
        foreach (var moth in world.Entities.Where(e => e.Components.ContainsKey(Moth)))
        {
            var data = moth.Require(Moth);
            double cargo = data.Read<double>("cargo");
            double rest = Math.Max(0, data.Read<double>("restInSeconds") - dt);
            data.Write("restInSeconds", rest);
            if (rest > 0 || plants.Length == 0) continue;
            var target = plants.FirstOrDefault(e => e.Id == data.Read<long>("target"));
            if (target is null)
            {
                long last = data.Read<long>("lastFlower");
                var candidates = plants.Where(e => plants.Length == 1 || e.Id != last).ToArray();
                target = candidates.MinBy(e => (cargo > 0 ? e.Require("flora.plant").Read<double>("biomass") : -e.Require("flora.plant").Read<double>("biomass"))
                    + e.Position.DistanceTo(moth.Position) * 0.001 + world.Random("pollination") * 0.04)!;
                data.Write("target", target.Id);
            }
            double distance = moth.Position.DistanceTo(target.Position);
            double heading = Math.Atan2(target.Position.Y - moth.Position.Y, target.Position.X - moth.Position.X);
            data.Write("heading", heading);
            double travel = Math.Min(distance, FlightSpeed * dt);
            moth.Position = new(moth.Position.X + Math.Cos(heading) * travel, moth.Position.Y + Math.Sin(heading) * travel);
            if (distance - travel > VisitRadius) continue;
            var plantData = target.Require("flora.plant");
            double biomass = plantData.Read<double>("biomass");
            var state = world.State("pollination");
            state.Write("visits", state.Read<long>("visits") + 1);
            double added = biomass < 0.9 ? Math.Min(cargo, 1 - biomass) : 0;
            if (added > 0)
            {
                plantData.Write("biomass", biomass + added);
                data.Write("cargo", cargo - added);
                state.Write("fertilizations", state.Read<long>("fertilizations") + 1);
                state.Write("recoveryAdded", state.Read<double>("recoveryAdded") + added);
                state.Write("cargoDeposited", state.Read<double>("cargoDeposited") + added);
                target.Require(Bloom).Write("glow", 1.0);
            }
            else if (cargo == 0 && biomass >= 0.75)
            {
                double remaining = biomass - PickupMass;
                double collected = biomass - remaining;
                plantData.Write("biomass", remaining);
                data.Write("cargo", collected);
                state.Write("cargoCollected", state.Read<double>("cargoCollected") + collected);
                target.Require(Bloom).Write("glow", 0.5);
            }
            data.Write("lastFlower", target.Id); data.Write("target", 0L); data.Write("restInSeconds", 2.5);
        }
    }
}
