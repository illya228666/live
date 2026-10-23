using ArtificialLife.Core;

namespace ArtificialLife.Modules.Food;

public sealed record FoodOptions(int SpawnIntervalTicks = 200, int MaxApples = 8, double MinSpawnDistanceFromAgent = 10);
public sealed class RuntimeFood : IRuntimeModule
{
    private FoodOptions options = new();
    public void Register(ModuleBuilder builder)
    {
        options = new(builder.Settings.Integer("spawnIntervalTicks", 200), builder.Settings.Integer("maxApples", 8), builder.Settings.Number("minSpawnDistanceFromAgent", 10));
        if (options.SpawnIntervalTicks < 1 || options.MaxApples < 1 || !double.IsFinite(options.MinSpawnDistanceFromAgent) || options.MinSpawnDistanceFromAgent < 0)
            throw new InvalidDataException("Invalid food settings.");
        builder.Observations.Register("vision.appearance.diamond.v1");
        builder.Components.Add(new("food.apple", 1, (_, _) => false, (_, _, _) => { }));
        builder.Systems.Add(new("food.spawn", world =>
        {
            var state = world.State(builder.Manifest.Id);
            if (world.Age < state.Read<long>("nextSpawn")) return;
            state.Write("nextSpawn", world.Age + options.SpawnIntervalTicks);
            if (world.Entities.Count(e => e.Components.ContainsKey("food.apple")) >= options.MaxApples) return;
            for (int attempt = 0; attempt < 256; attempt++)
            {
                var position = new Position(world.Random(builder.Manifest.Id) * world.Options.Width, world.Random(builder.Manifest.Id) * world.Options.Height);
                if (position.DistanceTo(world.Agent.Position) < options.MinSpawnDistanceFromAgent) continue;
                var entity = world.CreateEntity(position);
                entity.Components["food.apple"] = new();
                entity.Components["nutrition"] = new(); entity.Require("nutrition").Write("reduction", 0.20);
                entity.Components["vision.appearance"] = new();
                entity.Require("vision.appearance").Write("type", "vision.appearance.diamond.v1");
                entity.Require("vision.appearance").Write("diameter", 2.0);
                state.Write("spawned", state.Read<long>("spawned") + 1);
                break;
            }
        }, ["hunger.update"]));
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous)
    {
        if (previous is not null) return;
        // Settings-dependent interval is applied by Update; no food or old schedule is reset on upgrades.
        candidate.State("food").Write("nextSpawn", candidate.Age + options.SpawnIntervalTicks);
    }
}
