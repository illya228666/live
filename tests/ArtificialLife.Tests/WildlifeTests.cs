using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class WildlifeTests
{
    private const string Logic = "ArtificialLife.Modules.Wildlife";
    private const string View = "ArtificialLife.Modules.Wildlife.Presentation.Godot";
    private static string Repo
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ArtificialLife.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
    private static string Binary(string project, bool presentation = false) => Path.Combine(Repo, "src", project,
        presentation ? ".godot/mono/temp/bin/Release" : "bin/Release/net10.0", project + ".dll");
    private static RuntimeWorld World(double step = 1) => new()
    {
        Options = new() { TimeStep = step }, Agent = new() { Id = 1, Position = new(50, 50) }
    };
    private static RuntimeEntity Animal(RuntimeWorld world) => Assert.Single(world.Entities, e => e.Components.ContainsKey("wildlife.boar"));
    [Fact]
    public void StandardRuntimeStartsWithOneBoarAndSafetyWithoutManualActivation()
    {
        var options = RuntimeOptions.Load(Path.Combine(Repo, "configs/runtime.json"));
        options = options with { PackageRoot = Path.Combine(Repo, options.PackageRoot) };
        using var runtime = new RuntimeSession(options, new DqnController(options.Network, options.Learning));
        var initial = runtime.Snapshot();
        Assert.Equal(0, initial.Age);
        var boar = Assert.Single(initial.Entities, e => e.Components.ContainsKey("wildlife.boar"));
        Assert.Contains("safety", runtime.Report().Drives.Keys);
        var package = new PackageCatalog(options.PackageRoot).Read(new("wildlife", "1.0.0"));
        Assert.Equal(View + ".dll", package.PresentationAssembly);
        Assert.Contains("assets/boar.png", package.PresentationFiles.Keys);
        for (int i = 0; i < 20; i++) runtime.AdvanceOne(false);
        var moved = Assert.Single(runtime.Snapshot().Entities, e => e.Components.ContainsKey("wildlife.boar"));
        Assert.Equal(boar.Id, moved.Id); Assert.NotEqual(boar.Position, moved.Position);
    }

    private static void Step(Plugin plugin, RuntimeWorld world)
    {
        // Clock setters are host-owned; unit tests emulate the host's advance order.
        typeof(RuntimeWorld).GetProperty(nameof(RuntimeWorld.Time))!.SetValue(world, world.Time + world.Options.TimeStep);
        foreach (var system in plugin.Builder.Systems) system.Update(world);
    }

    [Fact]
    public void SeededTrajectoriesAndCopiedRandomStateAreReproducible()
    {
        using var plugin = new Plugin();
        var a = World(); a.State("rng:wildlife").Write("state", 42UL);
        var b = World(); b.State("rng:wildlife").Write("state", 42UL);
        plugin.Module.Migrate(a, null); plugin.Module.Migrate(b, null);
        var start = Animal(a).Position;
        for (int i = 0; i < 150; i++) { Step(plugin, a); Step(plugin, b); Assert.Equal(Animal(a).Position, Animal(b).Position); }
        Assert.NotEqual(start, Animal(a).Position);
        var copy = a.Copy();
        for (int i = 0; i < 100; i++) { Step(plugin, a); Step(plugin, copy); Assert.Equal(Animal(a).Position, Animal(copy).Position); }
        Assert.Equal(JsonSerializer.Serialize(a.ModuleState), JsonSerializer.Serialize(copy.ModuleState));
    }

    [Theory]
    [InlineData(1)] [InlineData(1000)]
    public void MovementStaysInBoundsIncludingLongSteps(double step)
    {
        using var plugin = new Plugin(); var world = World(step);
        plugin.Module.Migrate(world, null);
        for (int i = 0; i < 100; i++) { Step(plugin, world); Assert.True(world.Contains(Animal(world).Position)); }
    }

    [Fact]
    public void StraightMovementPreservesSpeedAndReflectsOvershootAndCorners()
    {
        using var plugin = new Plugin(); var world = World(); plugin.Module.Migrate(world, null);
        var animal = Animal(world); animal.Position = new(50, 50);
        var data = animal.Require("wildlife.boar"); data.Write("heading", 0.0); data.Write("targetHeading", 0.0); data.Write("nextTurnAt", 1e9);
        Step(plugin, world); Assert.Equal(51.125, animal.Position.X, 10); Assert.Equal(50, animal.Position.Y, 10);
        animal.Position = new(99.5, 99.5); data.Write("heading", Math.PI / 4); data.Write("targetHeading", Math.PI / 4);
        Step(plugin, world);
        Assert.Equal(200 - (99.5 + 1.125 / Math.Sqrt(2)), animal.Position.X, 10);
        Assert.Equal(animal.Position.X, animal.Position.Y, 10);
        Assert.Equal(5 * Math.PI / 4, data.Read<double>("heading"), 10);
        var big = World(1000); plugin.Module.Migrate(big, null); var runner = Animal(big); runner.Position = new(50, 50);
        runner.Require("wildlife.boar").Write("heading", 0.0); runner.Require("wildlife.boar").Write("targetHeading", 0.0); runner.Require("wildlife.boar").Write("nextTurnAt", 1e9);
        Step(plugin, big); Assert.Equal(25, runner.Position.X, 10); Assert.Equal(50, runner.Position.Y, 10);
    }

    [Fact]
    public void TurnsAreSmoothAndMotionDoesNotDependOnAgentPosition()
    {
        using var plugin = new Plugin(); var a = World(); plugin.Module.Migrate(a, null);
        var animal = Animal(a); animal.Position = new(50, 50);
        var data = animal.Require("wildlife.boar"); data.Write("heading", 0.0); data.Write("targetHeading", Math.PI); data.Write("nextTurnAt", 1e9);
        var b = a.Copy(); b.Agent.Position = new(0, 0);
        Step(plugin, a); Step(plugin, b);
        Assert.Equal(Math.PI / 4, data.Read<double>("heading"), 10);
        Assert.Equal(animal.Position, Animal(b).Position);
        Assert.Equal(1.125 / (Math.PI / 4) * Math.Sin(Math.PI / 4), animal.Position.X - 50, 10);
    }

    [Fact]
    public void InvestigativeTurnsAreSeededAndDoNotContinuouslyTrackTheAgent()
    {
        using var plugin = new Plugin(new { approachProbability = 1.0, approachSpreadDegrees = 0.0, minTurnSeconds = 10, maxTurnSeconds = 10 });
        var a = World(); plugin.Module.Migrate(a, null);
        var animal = Animal(a); animal.Position = new(50, 50);
        var data = animal.Require("wildlife.boar"); data.Write("nextTurnAt", 0.0); data.Write("heading", Math.PI / 2); data.Write("targetHeading", Math.PI / 2);
        a.Agent.Position = new(70, 50); var b = a.Copy(); b.Agent.Position = new(30, 50);
        Step(plugin, a); Step(plugin, b);
        Assert.Equal(0, data.Read<double>("targetHeading"), 10);
        Assert.Equal(Math.PI, Animal(b).Require("wildlife.boar").Read<double>("targetHeading"), 10);
        Assert.NotEqual(animal.Position, Animal(b).Position);
        var copy = a.Copy(); copy.Agent.Position = new(0, 0);
        Step(plugin, a); Step(plugin, copy);
        Assert.Equal(animal.Position, Animal(copy).Position); // No new turn: moving the agent does not steer the animal.
        var exact = a.Copy();
        for (int i = 0; i < 200; i++) { Step(plugin, a); Step(plugin, exact); Assert.Equal(Animal(a).Position, Animal(exact).Position); }
        Assert.Equal(JsonSerializer.Serialize(a.ModuleState), JsonSerializer.Serialize(exact.ModuleState));
    }

    [Fact]
    public void DisabledInvestigationPreservesLegacyRandomConsumptionAndTrajectories()
    {
        using var legacy = new Plugin(); using var disabled = new Plugin(new { approachProbability = 0.0, approachSpreadDegrees = 0.0 });
        var a = World(); var b = World(); legacy.Module.Migrate(a, null); disabled.Module.Migrate(b, null);
        b.Agent.Position = new(0, 0);
        for (int i = 0; i < 1000; i++) { Step(legacy, a); Step(disabled, b); Assert.Equal(Animal(a).Position, Animal(b).Position); }
        Assert.Equal(JsonSerializer.Serialize(a.ModuleState), JsonSerializer.Serialize(b.ModuleState));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(5, 0.5)] [InlineData(10, 0)] [InlineData(20, 0)]
    public void SafetyAndSenseFollowDistanceWithoutAccumulation(double distance, double expected)
    {
        using var plugin = new Plugin(); var world = World(); plugin.Module.Migrate(world, null);
        Animal(world).Position = new(50 + distance, 50);
        for (int i = 0; i < 3; i++)
        {
            var drive = Assert.Single(plugin.Builder.Drives.Single()(world));
            Assert.Equal("safety", drive.Key); Assert.Equal(expected, drive.Current, 10);
            Assert.Equal(0, drive.Target); Assert.Equal(1, drive.Scale); Assert.Equal(1, drive.Weight);
            Assert.Equal((float)expected, plugin.Builder.Sensors.Single()(world).Single().Features.Single());
        }
        Animal(world).Position = new(100, 100);
        Assert.Equal(0, plugin.Builder.Drives.Single()(world).Single().Current);
    }

    [Fact]
    public void MultipleAnimalsUseMaximumDangerAndAreNotRespawned()
    {
        using var plugin = new Plugin(new { initialCount = 3 }); var world = World(); plugin.Module.Migrate(world, null);
        Assert.Equal(3, world.Entities.Count);
        world.Entities[0].Position = new(55, 50); world.Entities[1].Position = new(52, 50); world.Entities[2].Position = new(100, 100);
        Assert.Equal(0.8, plugin.Builder.Drives.Single()(world).Single().Current, 10);
        world.Entities.Clear(); Step(plugin, world);
        Assert.Empty(world.Entities); Assert.Equal(0, plugin.Builder.Drives.Single()(world).Single().Current);
    }

    [Fact]
    public void MinimumSpawnDistanceUsesCornerFallbackAndRejectsImpossibleMap()
    {
        using var plugin = new Plugin(new { minSpawnDistance = Math.Sqrt(5000) });
        var world = World(); plugin.Module.Migrate(world, null);
        Assert.Equal(new Position(0, 0), Animal(world).Position);
        using var impossible = new Plugin(new { minSpawnDistance = 71.0 });
        Assert.Throws<InvalidDataException>(() => impossible.Module.Migrate(World(), null));
    }

    [Theory]
    [InlineData("initialCount", "0")] [InlineData("speedFactor", "-1")]
    [InlineData("minTurnSeconds", "0")] [InlineData("maxTurnSeconds", "2")]
    [InlineData("turnDegreesPerSecond", "0")] [InlineData("dangerRadius", "0")]
    [InlineData("driveWeight", "-1")] [InlineData("minSpawnDistance", "-1")]
    [InlineData("diameter", "0")] [InlineData("speedFactor", "1e309")]
    [InlineData("approachProbability", "-0.1")] [InlineData("approachProbability", "1.1")]
    [InlineData("approachSpreadDegrees", "-1")] [InlineData("approachSpreadDegrees", "181")]
    public void InvalidSettingsAreRejected(string key, string value)
    {
        using var settings = JsonDocument.Parse("{\"" + key + "\":" + value + "}");
        Assert.Throws<InvalidDataException>(() => { using var plugin = new Plugin(settings.RootElement); });
    }

    [Fact]
    public void RealDllInstallPreservesLearningLifeAndExistingObjects()
    {
        using var fixture = new Catalog();
        var options = fixture.Options with { Learning = fixture.Options.Learning with { WarmupSteps = 64, BatchSize = 16 } };
        using var runtime = new RuntimeSession(options, new DqnController(options.Network, options.Learning));
        for (int i = 0; i < 100; i++) runtime.AdvanceOne();
        var before = runtime.Snapshot(); var status = runtime.Controller.Status;
        var oldTokens = runtime.Observe(); var oldActions = runtime.LegalActions();
        var scores = ((DqnController)runtime.Controller).Brain.Scores(oldTokens, oldActions);
        var installed = runtime.Activate(new("wildlife-test", [new("wildlife", "1.0.0")]));
        Assert.True(installed.Accepted, installed.Message);
        var after = runtime.Snapshot();
        Assert.Equal(before.Age, after.Age); Assert.Equal(before.Time, after.Time);
        Assert.Equal(JsonSerializer.Serialize(before.Agent), JsonSerializer.Serialize(after.Agent));
        Assert.All(before.Entities, e => Assert.Contains(after.Entities, n => JsonSerializer.Serialize(n) == JsonSerializer.Serialize(e)));
        Assert.Single(after.Entities, e => e.Components.ContainsKey("wildlife.boar"));
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(status.Steps, runtime.Controller.Status.Steps); Assert.Equal(status.Updates, runtime.Controller.Status.Updates);
        Assert.Equal(status.ReplayCount, runtime.Controller.Status.ReplayCount); Assert.Equal(0, runtime.Controller.Status.EligibleReplayCount);
        Assert.Equal(scores, ((DqnController)runtime.Controller).Brain.Scores(oldTokens, oldActions));
        Assert.Contains("safety", runtime.Report().Drives.Keys);
        Assert.Contains(runtime.Observe(), t => runtime.ObservationKeys[t.Type] == "vision.appearance.boar.v1");
        Assert.Contains(runtime.Observe(), t => runtime.ObservationKeys[t.Type] == "safety.threat.v1");
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == View || a.GetName().Name == "GodotSharp");
        Assert.True(runtime.Activate(new("same", [new("wildlife", "1.0.0")])).Accepted);
        Assert.Equal(after, runtime.Snapshot());
        fixture.Publish("1.1.0", new { initialCount = 9 });
        Assert.True(runtime.Activate(new("upgrade", [new("wildlife", "1.1.0")])).Accepted);
        Assert.Equal(after.Entities.Single(e => e.Components.ContainsKey("wildlife.boar")).Id,
            Assert.Single(runtime.Snapshot().Entities, e => e.Components.ContainsKey("wildlife.boar")).Id);
        Assert.Equal(JsonSerializer.Serialize(after.Entities), JsonSerializer.Serialize(runtime.Snapshot().Entities));
        runtime.AdvanceOne(); Assert.Equal(before.Age + 1, runtime.Snapshot().Age);
    }

    [Fact]
    public void InvestigativeUpgradePreservesExistingAnimalsSchedulesAndRegistries()
    {
        using var fixture = new Catalog();
        using var runtime = fixture.Session();
        Assert.True(runtime.Activate(new("legacy", [new("wildlife", "1.0.0")])).Accepted);
        for (int i = 0; i < 20; i++) runtime.AdvanceOne(false);
        var before = runtime.Snapshot(); var observations = runtime.ObservationKeys.ToArray(); var actions = runtime.ActionKeys.ToArray();
        fixture.Publish("1.1.0", new { initialCount = 6, approachProbability = 0.1, approachSpreadDegrees = 60 });
        Assert.True(runtime.Activate(new("investigate", [new("wildlife", "1.1.0")])).Accepted);
        var after = runtime.Snapshot();
        Assert.Equal(before.Age, after.Age); Assert.Equal(before.Time, after.Time);
        Assert.Equal(JsonSerializer.Serialize(before.Entities), JsonSerializer.Serialize(after.Entities));
        Assert.Equal(JsonSerializer.Serialize(before.ModuleState), JsonSerializer.Serialize(after.ModuleState));
        Assert.Equal(observations, runtime.ObservationKeys); Assert.Equal(actions, runtime.ActionKeys);
        Assert.Single(after.Entities, e => e.Components.ContainsKey("wildlife.boar")); // initialCount never respawns on upgrade.
        runtime.AdvanceOne(false); Assert.Equal(before.Age + 1, runtime.Snapshot().Age);
    }

    [Fact]
    public void CloserWildlifeLowersSustainedHomeostasisReward()
    {
        using var wildlife = new Plugin();
        string path = Path.Combine(Repo, "src/ArtificialLife.Modules.Homeostasis/bin/Release/net10.0/ArtificialLife.Modules.Homeostasis.dll");
        var context = new ModuleLoadContext(path);
        try
        {
            using var homeostasis = (IRuntimeModule)Activator.CreateInstance(context.LoadPluginAssembly(path).GetType("ArtificialLife.Modules.Homeostasis.RuntimeHomeostasis", true)!)!;
            var builder = new ModuleBuilder(new("homeostasis", "1", 1, "", ""), new(256), new(256)); homeostasis.Register(builder);
            var world = World(); wildlife.Module.Migrate(world, null);
            double Reward(double distance)
            {
                Animal(world).Position = new(50 + distance, 50);
                var input = new MotivationInput([new("temperature", 0, 0, 1, 1), new("hunger", 0, 0, 1, 1), wildlife.Builder.Drives.Single()(world).Single()], new Dictionary<string, double>());
                return builder.Motivation!.Evaluate(input, input);
            }
            Assert.Equal(1, Reward(10), 10); Assert.True(Reward(5) < Reward(10)); Assert.True(Reward(0) < Reward(5));
            Assert.Equal(1.0 / 3, Reward(0), 10); Assert.Equal(1.0 / 3, Reward(0), 10);
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData("assets/boar.png")] [InlineData("ArtificialLife.Modules.Wildlife.dll")]
    [InlineData("ArtificialLife.Modules.Wildlife.Presentation.Godot.deps.json")]
    public void CorruptedPackageIsRejectedAtomically(string relative)
    {
        using var fixture = new Catalog(); using var runtime = fixture.Session(); var before = runtime.Snapshot();
        File.AppendAllText(Path.Combine(fixture.Root, "wildlife/1.0.0", relative), "corrupt");
        Assert.False(runtime.Activate(new("bad", [new("wildlife", "1.0.0")])).Accepted);
        Assert.Equal(before, runtime.Snapshot());
    }

    [Fact]
    public void FailedInstallDiscardsSpawnAndRngAndCheckpointArchivesSprite()
    {
        using var fixture = new Catalog(); fixture.Publish("bad", new { minSpawnDistance = 1000 });
        using var runtime = fixture.Session(); var before = runtime.Snapshot();
        Assert.False(runtime.Activate(new("impossible", [new("wildlife", "bad")])).Accepted);
        Assert.Equal(before, runtime.Snapshot());
        Assert.True(runtime.Activate(new("install", [new("wildlife", "1.0.0")])).Accepted);
        string checkpoint = Path.Combine(fixture.Root, "policy"); RuntimeCheckpoint.Save(checkpoint, runtime);
        var manifest = RuntimeCheckpoint.Read(checkpoint);
        Assert.True(File.Exists(Path.Combine(manifest.Options.PackageRoot, "wildlife/1.0.0/assets/boar.png")));
        using var restored = RuntimeCheckpoint.Open(checkpoint);
        Assert.Contains("safety", restored.Report().Drives.Keys); restored.AdvanceOne(false);
    }

    [Fact]
    public void RetiredPluginContextsCanBeCollected()
    {
        var contexts = Retire();
        for (int i = 0; i < 10 && contexts.Any(c => c.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(contexts, c => Assert.False(c.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Retire()
    {
        using var fixture = new Catalog(); fixture.Publish("2"); using var runtime = fixture.Session();
        Assert.True(runtime.Activate(new("one", [new("wildlife", "1.0.0")])).Accepted);
        runtime.AdvanceOne(false);
        Assert.True(runtime.Activate(new("two", [new("wildlife", "2")])).Accepted);
        return runtime.RetiredContexts.ToArray();
    }

    private sealed class Plugin : IDisposable
    {
        private readonly ModuleLoadContext context;
        public IRuntimeModule Module { get; }
        public ModuleBuilder Builder { get; }
        public Plugin(object? settings = null)
        {
            string path = Binary(Logic); context = new(path);
            Module = (IRuntimeModule)Activator.CreateInstance(context.LoadPluginAssembly(path).GetType(Logic + ".RuntimeWildlife", true)!)!;
            Builder = new(new("wildlife", "1.0.0", 1, Logic + ".dll", Logic + ".RuntimeWildlife") { Settings = JsonSerializer.SerializeToElement(settings ?? new { }) }, new(256), new(256));
            try { Module.Register(Builder); } catch { Dispose(); throw; }
        }
        public void Dispose() { Module.Dispose(); context.Unload(); }
    }

    private sealed class Catalog : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ArtificialLife-wildlife", Guid.NewGuid().ToString("N"));
        public RuntimeOptions Options { get; }
        public Catalog()
        {
            Options = RuntimeDefaults.FromLegacy(new()) with { PackageRoot = Root, Inbox = Path.Combine(Root, "isolated-inbox") };
            foreach (string id in new[] { "movement", "vision", "temperature", "homeostasis", "hunger", "food" })
            {
                string source = Path.Combine(Repo, "artifacts/packages", id, "1.0.0"), destination = Path.Combine(Root, id, "1.0.0");
                Directory.CreateDirectory(destination);
                foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            Publish("1.0.0");
        }
        public RuntimeSession Session() => new(Options, new DqnController(Options.Network, Options.Learning));
        public void Publish(string version, object? settings = null)
        {
            string directory = Path.Combine(Root, "wildlife", version); Directory.CreateDirectory(Path.Combine(directory, "assets"));
            foreach (var project in new[] { Logic, View })
            {
                string path = Binary(project, project == View);
                File.Copy(path, Path.Combine(directory, project + ".dll"));
                File.Copy(Path.ChangeExtension(path, ".deps.json"), Path.Combine(directory, project + ".deps.json"));
            }
            File.Copy(Path.Combine(Repo, "assets/wildlife/boar.png"), Path.Combine(directory, "assets/boar.png"));
            string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, file))));
            var manifest = new ModuleManifest("wildlife", version, 1, Logic + ".dll", Logic + ".RuntimeWildlife")
            {
                Requires = ["vision.appearance", "motivation"], Schemas = new() { ["wildlife.boar"] = 1 },
                Settings = JsonSerializer.SerializeToElement(settings ?? new { }), LogicHash = Hash(Logic + ".dll"),
                LogicFiles = new() { [Logic + ".deps.json"] = Hash(Logic + ".deps.json") },
                PresentationAssembly = View + ".dll", PresentationType = View + ".BoarPresentation", PresentationHash = Hash(View + ".dll"),
                PresentationFiles = new() { [View + ".deps.json"] = Hash(View + ".deps.json"), ["assets/boar.png"] = Hash("assets/boar.png") }
            };
            File.WriteAllText(Path.Combine(directory, "module.json"), JsonSerializer.Serialize(manifest, PackageCatalog.Json));
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            if (!absolute.StartsWith(Path.Combine(Path.GetTempPath(), "ArtificialLife-wildlife") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(absolute, true);
        }
    }
}
