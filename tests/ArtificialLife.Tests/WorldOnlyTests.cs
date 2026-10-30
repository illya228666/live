using ArtificialLife.Application;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class WorldOnlyTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "live-world-" + Guid.NewGuid().ToString("N"));
    private static RuntimeOptions Empty() => new() { Mode = RuntimeMode.WorldOnly, World = new() { EpisodeSteps = 2 } };
    [Fact]
    public void EmptyWorldHasNoAgentOrControllerAndNeverEndsAtEpisodeBoundary()
    {
        using var world = new RuntimeSession(Empty());
        Assert.False(world.Snapshot().HasAgent);
        Assert.Throws<InvalidOperationException>(() => world.Controller);
        for (int i = 0; i < 100; i++) world.AdvanceWorld();
        Assert.Equal(100, world.Snapshot().Age);
        Assert.Empty(world.Snapshot().Entities);
    }
    [Fact]
    public void EmptyWorldRestoresClockAndAllocatorExactly()
    {
        string root = Root();
        try
        {
            using var original = new RuntimeSession(Empty());
            for (int i = 0; i < 13; i++) original.AdvanceWorld();
            WorldCheckpoint.Save(root, original);
            using var restored = WorldCheckpoint.Open(root);
            Assert.Equal(original.Snapshot(), restored.Snapshot());
            for (int i = 0; i < 71; i++) { original.AdvanceWorld(); restored.AdvanceWorld(); }
            Assert.Equal(original.Snapshot(), restored.Snapshot());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ShippedMechanicsRestoreRngSchedulesAndFutureEntityIds()
    {
        string root = Root();
        try
        {
            var options = RuntimeOptions.Load(Path.Combine(RuntimeDefaults.RepositoryRoot(), "configs/world-only.json"));
            options = options with { PackageRoot = Environment.GetEnvironmentVariable("LIVE_WORLD_PACKAGE_ROOT") ?? Path.Combine(RuntimeDefaults.RepositoryRoot(), options.PackageRoot), Settings = new() { ["food"] = System.Text.Json.JsonSerializer.SerializeToElement(new { SpawnIntervalTicks = 3, MaxApples = 100 }) } };
            using var original = new RuntimeSession(options);
            for (int i = 0; i < 43; i++) original.AdvanceWorld();
            WorldCheckpoint.Save(root, original);
            using var restored = WorldCheckpoint.Open(root);
            Assert.Equal(original.Snapshot(), restored.Snapshot());
            for (int i = 0; i < 79; i++) { original.AdvanceWorld(); restored.AdvanceWorld(); Assert.Equal(original.Snapshot(), restored.Snapshot()); }
            Assert.True(restored.Snapshot().Entities.Count > 20);
            Assert.False(restored.Snapshot().HasAgent);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void CheckpointRejectsCorruptionAndObservationDoesNotConsumeRandomness()
    {
        string root = Root();
        try
        {
            using var world = new RuntimeSession(Empty());
            var before = world.Snapshot();
            world.CaptureMetrics(); world.Snapshot();
            Assert.Equal(before, world.Snapshot());
            string generation = WorldCheckpoint.Save(root, world);
            File.AppendAllText(Path.Combine(generation, "world.json"), " ");
            Assert.Throws<InvalidDataException>(() => WorldCheckpoint.Open(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void PackagesMustDeclareWorldOnlySupport()
    {
        string root = Root();
        try
        {
            var options = RuntimeDefaults.FromLegacy(new());
            new RuntimePackageImporter(root).Import([new PackageCatalog(options.PackageRoot).DirectoryFor(new("movement", "1.0.0"))]);
            string path = Path.Combine(root, "movement", "1.0.0", "module.json");
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), PackageCatalog.Json)!;
            WorldCheckpoint.Atomic(path, manifest with { SupportsWorldOnly = false });
            using var world = new RuntimeSession(Empty() with { PackageRoot = root });
            var before = world.Snapshot();
            Assert.False(world.Activate(new("old", [new("movement", "1.0.0")])).Accepted);
            Assert.Equal(before, world.Snapshot());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void InhabitedWorldRestoresPhysicalStateWithExplicitFreshController()
    {
        string root = Root();
        try
        {
            var options = RuntimeDefaults.FromLegacy(new());
            using var original = new RuntimeSession(options, new ArtificialLife.Brain.DqnController(options.Network, options.Learning));
            for (int i = 0; i < 17; i++) original.AdvanceOne();
            WorldCheckpoint.Save(root, original);
            Assert.Throws<InvalidOperationException>(() => WorldCheckpoint.Open(root));
            using var restored = WorldCheckpoint.Open(root, new ArtificialLife.Brain.DqnController(options.Network, options.Learning));
            Assert.Equal(original.Snapshot(), restored.Snapshot());
            Assert.True(restored.Snapshot().HasAgent);
            restored.AdvanceOne();
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("hunger")] [InlineData("safety")] [InlineData("ecology")]
    public void PhysicalTrajectoriesStillMatchPreservedEcologyEvidence(string factor)
    {
        string root = RuntimeDefaults.RepositoryRoot();
        var definition = ExperimentDefinition.Load(Path.Combine(root, "configs", $"research-{factor}-v2.json"));
        using var evidence = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "research", "scenarios", $"{factor}-v2-results.json")));
        var expected = evidence.RootElement.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("seed").GetInt32() == 61001 && c.GetProperty("policy").GetString() == "joint");
        var actual = EcologyScenarioAudit.Evaluate(definition.Runtime, definition.Behavior, 0, 61001, "joint", 12000, 1000);
        Assert.Equal(expected.GetProperty("actionHash").GetString(), actual.ActionHash);
        Assert.Equal(expected.GetProperty("stateHash").GetString(), actual.StateHash);
    }

}
// Real DLL fixture for process-isolated world update/recovery smoke tests.
public sealed class WorldEvolutionFixture : ArtificialLife.Core.IRuntimeModule
{
    private string id = "";
    public void Register(ArtificialLife.Core.ModuleBuilder builder)
    {
        id = builder.Manifest.Id;
        int failAfter = builder.Settings.Integer("failAfter", 0);
        int hangAfter = builder.Settings.Integer("hangAfter", 0);
        builder.Systems.Add(new(id + ".tick", w =>
        {
            var state = w.State(id);
            long ticks = state.Read<long>("ticks") + 1;
            state.Write("ticks", ticks);
            if (failAfter > 0 && ticks >= failAfter) throw new InvalidOperationException("Intentional delayed world fixture failure.");
            if (hangAfter > 0 && ticks >= hangAfter) Thread.Sleep(Timeout.Infinite);
        }, []));
        builder.Metrics.Add(frame => [KeyValuePair.Create("ticks", (double)frame.ModuleState[id].Read<long>("ticks"))]);
    }
    public void Migrate(ArtificialLife.Core.RuntimeWorld world, ArtificialLife.Core.ModuleManifest? previous)
    {
        world.State(id).Write("ticks", 0L);
        world.State(id).Write("installs", world.State(id).Read<int>("installs") + 1);
    }
}
