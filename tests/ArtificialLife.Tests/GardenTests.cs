using ArtificialLife.Application;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class GardenTests
{
    [Fact]
    public void FloraPreservesExistingObjectsSpreadsWithinCapAndRestoresExactly()
    {
        using var packages = new GardenPackages();
        var options = packages.Options with {
            Settings = new() { ["flora"] = System.Text.Json.JsonSerializer.SerializeToElement(new { maximumPlants = 20 }) } };
        using var original = new RuntimeSession(options);
        for (int i = 0; i < 1601; i++) original.AdvanceWorld();
        var before = original.Snapshot();
        Assert.Equal(8, before.Entities.Count(e => e.Components.ContainsKey("food.apple")));
        var activated = original.Activate(new("flora", [new("flora", "1.0.0")]));
        Assert.True(activated.Accepted, activated.Message);
        var installed = original.Snapshot();
        Assert.Equal(before.Age, installed.Age);
        Assert.Equal(before.Time, installed.Time);
        foreach (var entity in before.Entities)
        {
            var retained = Assert.Single(installed.Entities, e => e.Id == entity.Id);
            Assert.Equal(entity.Position, retained.Position);
            foreach (var component in entity.Components)
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(component.Value), System.Text.Json.JsonSerializer.Serialize(retained.Components[component.Key]));
        }
        for (int i = 0; i < 2400; i++) original.AdvanceWorld();
        Assert.Equal(20, original.Snapshot().Entities.Count(e => e.Components.ContainsKey("flora.plant")));
        var plants = original.Snapshot().Entities.Where(e => e.Components.ContainsKey("flora.plant")).ToArray();
        Assert.True(plants.Max(e => e.Components["flora.plant"].Read<double>("suitability")) > plants.Min(e => e.Components["flora.plant"].Read<double>("suitability")) + 0.1);
        Assert.All(plants, e => Assert.InRange(e.Components["flora.plant"].Read<double>("biomass"), 0.2, 1));
        AssertExactContinuation(original);
    }

    private static void AssertExactContinuation(RuntimeSession original)
    {
        string checkpoint = Path.Combine(Path.GetTempPath(), "live-garden-" + Guid.NewGuid().ToString("N"));
        try
        {
            WorldCheckpoint.Save(checkpoint, original);
            using var restored = WorldCheckpoint.Open(checkpoint);
            Assert.Equal(original.Snapshot(), restored.Snapshot());
            for (int i = 0; i < 200; i++)
            {
                original.AdvanceWorld(); restored.AdvanceWorld();
                Assert.Equal(original.Snapshot(), restored.Snapshot());
            }
        }
        finally { Directory.Delete(checkpoint, true); }
    }

    [Fact]
    public void GrazingUsesExistingWildlifeAndPlantsRecoverAfterBites()
    {
        using var packages = new GardenPackages();
        using var world = new RuntimeSession(packages.Options);
        Assert.True(world.Activate(new("flora", [new("flora", "1.0.0")])).Accepted);
        for (int i = 0; i < 3000; i++) world.AdvanceWorld();
        var before = world.Snapshot();
        var outcome = world.Activate(new("graze", [new("grazing", "1.0.0")]));
        Assert.True(outcome.Accepted, outcome.Message);
        Assert.Equal(before.Age, world.Age);
        Assert.Equal(before.Entities.Select(e => e.Id), world.Snapshot().Entities.Select(e => e.Id));
        bool sawBite = false, sawRecovery = false;
        var lowPoints = new Dictionary<long, double>();
        for (int i = 0; i < 4000; i++)
        {
            world.AdvanceWorld();
            var frame = world.Snapshot();
            sawBite |= frame.ModuleState["grazing"].Read<long>("bites") > 0;
            foreach (var plant in frame.Entities.Where(e => e.Components.ContainsKey("grazing.scar")))
            {
                double biomass = plant.Components["flora.plant"].Read<double>("biomass");
                if (plant.Components["grazing.scar"].Read<double>("pressure") > 0.9) lowPoints[plant.Id] = biomass;
                if (lowPoints.TryGetValue(plant.Id, out double low) && biomass > low + 0.1) sawRecovery = true;
            }
        }
        Assert.True(sawBite); Assert.True(sawRecovery);
        Assert.All(before.Entities, e => Assert.Contains(world.Snapshot().Entities, retained => retained.Id == e.Id));
        AssertExactContinuation(world);
    }

    [Fact]
    public void PollinatorsConnectColoniesPreservePopulationAndRestoreFlightState()
    {
        using var packages = new GardenPackages();
        using var world = new RuntimeSession(packages.Options);
        Assert.True(world.Activate(new("flora", [new("flora", "1.0.0")])).Accepted);
        for (int i = 0; i < 4000; i++) world.AdvanceWorld();
        Assert.True(world.Activate(new("graze", [new("grazing", "1.0.0")])).Accepted);
        for (int i = 0; i < 1000; i++) world.AdvanceWorld();
        var before = world.Snapshot();
        var outcome = world.Activate(new("moths", [new("pollination", "1.0.0")]));
        Assert.True(outcome.Accepted, outcome.Message);
        Assert.Equal(before.Age, world.Age);
        Assert.Equal(before.Entities.Count + 6, world.Snapshot().Entities.Count);
        var visited = new HashSet<long>();
        for (int i = 0; i < 2000; i++)
        {
            world.AdvanceWorld();
            foreach (var plant in world.Snapshot().Entities.Where(e => e.Components.TryGetValue("pollination.bloom", out var bloom) && bloom.Read<double>("glow") > 0.3)) visited.Add(plant.Id);
        }
        var after = world.Snapshot();
        Assert.Equal(6, after.Entities.Count(e => e.Components.ContainsKey("pollination.moth")));
        Assert.True(visited.Count > 2);
        var state = after.ModuleState["pollination"];
        Assert.True(state.Read<long>("visits") > 100);
        Assert.True(state.Read<long>("fertilizations") > 5);
        // Nearly recovered plants receive only the space remaining below full biomass.
        Assert.InRange(state.Read<double>("recoveryAdded"), state.Read<long>("fertilizations") * 0.1, state.Read<long>("fertilizations") * 0.18);
        Assert.All(after.Entities.Where(e => e.Components.ContainsKey("flora.plant")), e => Assert.InRange(e.Components["flora.plant"].Read<double>("biomass"), 0.12, 1));
        Assert.All(before.Entities, e => Assert.Contains(after.Entities, retained => retained.Id == e.Id));
        AssertExactContinuation(world);
    }

    [Fact]
    public void PollinatorsWaitSafelyUntilFirstFoodAppears()
    {
        using var packages = new GardenPackages();
        using var world = new RuntimeSession(packages.Options);
        var outcome = world.Activate(new("garden", [new("flora", "1.0.0"), new("grazing", "1.0.0"), new("pollination", "1.0.0")]));
        Assert.True(outcome.Accepted, outcome.Message);
        for (int i = 0; i < 100; i++) world.AdvanceWorld();
        Assert.Equal(0, world.Snapshot().ModuleState["pollination"].Read<long>("visits"));
        Assert.Equal(6, world.Snapshot().Entities.Count(e => e.Components.ContainsKey("pollination.moth")));
        for (int i = 0; i < 400; i++) world.AdvanceWorld();
        Assert.True(world.Snapshot().ModuleState["pollination"].Read<long>("visits") > 0);
    }

    // Build the tiny catalogue from build outputs, not from a developer's experimental saves.
    private sealed class GardenPackages : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "live-garden-packages-" + Guid.NewGuid().ToString("N"));
        public RuntimeOptions Options { get; }
        public GardenPackages()
        {
            var definitions = new[] {
                (Id: "vision", Project: "Vision", Entry: "RuntimeVision", Schemas: new[] { "vision.appearance" }),
                (Id: "hunger", Project: "Hunger", Entry: "RuntimeHunger", Schemas: new[] { "hunger.body", "nutrition" }),
                (Id: "food", Project: "Food", Entry: "RuntimeFood", Schemas: new[] { "food.apple" }),
                (Id: "temperature", Project: "Temperature", Entry: "RuntimeTemperature", Schemas: new[] { "temperature.body", "temperature.emitter" }),
                (Id: "wildlife", Project: "Wildlife", Entry: "RuntimeWildlife", Schemas: new[] { "wildlife.boar" }),
                (Id: "flora", Project: "Garden", Entry: "RuntimeFlora", Schemas: new[] { "flora.plant" }),
                (Id: "grazing", Project: "Garden", Entry: "RuntimeGrazing", Schemas: new[] { "grazing.animal", "grazing.scar" }),
                (Id: "pollination", Project: "Garden", Entry: "RuntimePollination", Schemas: new[] { "pollination.moth", "pollination.bloom" })
            };
            foreach (var definition in definitions)
            {
                string assembly = "ArtificialLife.Modules." + definition.Project;
                string destination = Path.Combine(directory, definition.Id, "1.0.0");
                Directory.CreateDirectory(destination);
                string binary = Path.Combine(RuntimeDefaults.RepositoryRoot(), "src", assembly, "bin", "Release", "net10.0", assembly + ".dll");
                File.Copy(binary, Path.Combine(destination, assembly + ".dll"));
                var manifest = new ModuleManifest(definition.Id, "1.0.0", 1, assembly + ".dll", assembly + "." + definition.Entry)
                {
                    SupportsWorldOnly = true, Schemas = definition.Schemas.ToDictionary(key => key, _ => 1),
                    Provides = new[] { "vision.appearance", "nutrition", "flora", "wildlife" }.Where(key => key.StartsWith(definition.Id == "vision" ? "vision" : definition.Id == "hunger" ? "nutrition" : definition.Id, StringComparison.Ordinal)).ToArray(),
                    LogicHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary)))
                };
                WorldCheckpoint.Atomic(Path.Combine(destination, "module.json"), manifest);
            }
            Options = new() { Mode = RuntimeMode.WorldOnly, World = new() { TimeStep = 0.25 }, PackageRoot = directory,
                Packages = definitions.Where(d => d.Id is not ("flora" or "grazing" or "pollination")).Select(d => new PackageReference(d.Id, "1.0.0")).ToArray() };
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
