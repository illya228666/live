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
                (Id: "flora", Project: "Garden", Entry: "RuntimeFlora", Schemas: new[] { "flora.plant" })
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
                    LogicHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary)))
                };
                WorldCheckpoint.Atomic(Path.Combine(destination, "module.json"), manifest);
            }
            Options = new() { Mode = RuntimeMode.WorldOnly, World = new() { TimeStep = 0.25 }, PackageRoot = directory,
                Packages = definitions.Where(d => d.Id != "flora").Select(d => new PackageReference(d.Id, "1.0.0")).ToArray() };
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
