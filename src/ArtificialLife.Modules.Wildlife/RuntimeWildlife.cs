using ArtificialLife.Core;

namespace ArtificialLife.Modules.Wildlife;

/// <summary>All mutable animal state and randomness belong to the copied runtime world.</summary>
public sealed class RuntimeWildlife : IRuntimeModule
{
    private const string Boar = "wildlife.boar";
    private const string Appearance = "vision.appearance.boar.v1";
    private string stream = "wildlife";
    private int initialCount = 1;
    private double speedFactor = 0.75, turnMin = 3, turnMax = 8, turnRate = Math.PI / 4;
    private double radius = 10, weight = 1, spawnDistance = 15, diameter = 4;
    private double approachProbability, approachSpread;

    public void Register(ModuleBuilder builder)
    {
        stream = builder.Manifest.Id;
        initialCount = builder.Settings.Integer("initialCount", 1);
        speedFactor = builder.Settings.Number("speedFactor", 0.75);
        turnMin = builder.Settings.Number("minTurnSeconds", 3);
        turnMax = builder.Settings.Number("maxTurnSeconds", 8);
        turnRate = builder.Settings.Number("turnDegreesPerSecond", 45) * Math.PI / 180;
        radius = builder.Settings.Number("dangerRadius", 10);
        weight = builder.Settings.Number("driveWeight", 1);
        spawnDistance = builder.Settings.Number("minSpawnDistance", 15);
        diameter = builder.Settings.Number("diameter", 4);
        approachProbability = builder.Settings.Number("approachProbability", 0);
        approachSpread = builder.Settings.Number("approachSpreadDegrees", 90) * Math.PI / 180;
        if (initialCount < 1 || !Nonnegative(speedFactor) || !Positive(turnMin) || !Positive(turnMax) ||
            turnMax < turnMin || !Positive(turnRate) || !Positive(radius) || !Nonnegative(weight) ||
            !Nonnegative(spawnDistance) || !Positive(diameter) || !Nonnegative(approachProbability) || approachProbability > 1 ||
            !Nonnegative(approachSpread) || approachSpread > Math.PI)
            throw new InvalidDataException("Invalid wildlife settings.");

        builder.Components.Add(new(Boar, 1, (_, _) => false, (_, _, _) => { }));
        builder.Observations.Register(Appearance);
        int sense = builder.Observations.Register("safety.threat.v1");
        builder.Sensors.Add(world => [new(sense, [(float)Danger(world)])]);
        builder.Drives.Add(world => [new("safety", Danger(world), 0, 1, weight)]);
        builder.Descriptors.Add(new("safety", "Safety"));
        builder.Systems.Add(new("wildlife.move", Update, []));
    }

    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous)
    {
        if (!double.IsFinite(candidate.Options.MovementSpeed * speedFactor))
            throw new InvalidDataException("Wildlife speed exceeds the numeric range.");
        if (previous is not null) return;
        for (int i = 0; i < initialCount; i++)
        {
            var animal = candidate.CreateEntity(Spawn(candidate));
            animal.Components[Boar] = new();
            var state = animal.Require(Boar);
            double heading = candidate.Random(stream) * Math.Tau;
            state.Write("heading", heading);
            state.Write("targetHeading", heading);
            state.Write("nextTurnAt", candidate.Time + Interval(candidate));
            animal.Components["vision.appearance"] = new();
            animal.Require("vision.appearance").Write("type", Appearance);
            animal.Require("vision.appearance").Write("diameter", diameter);
        }
    }

    private Position Spawn(RuntimeWorld world)
    {
        for (int attempt = 0; attempt < 256; attempt++)
        {
            var at = new Position(world.Random(stream) * world.Options.Width, world.Random(stream) * world.Options.Height);
            if (!world.HasAgent || at.DistanceTo(world.Agent.Position) >= spawnDistance) return at;
        }
        Position[] corners = [new(0, 0), new(world.Options.Width, 0), new(0, world.Options.Height), new(world.Options.Width, world.Options.Height)];
        var farthest = corners.MaxBy(p => p.DistanceTo(world.Agent.Position));
        if (farthest.DistanceTo(world.Agent.Position) < spawnDistance)
            throw new InvalidDataException("The map cannot accommodate wildlife at the minimum spawn distance.");
        return farthest;
    }

    private double Danger(RuntimeWorld world) => !world.HasAgent ? 0 : world.Entities.Where(e => e.Components.ContainsKey(Boar))
        .Select(e => Math.Clamp(1 - e.Position.DistanceTo(world.Agent.Position) / radius, 0, 1)).DefaultIfEmpty(0).Max();

    private double Interval(RuntimeWorld world) => turnMin + (turnMax - turnMin) * world.Random(stream);

    private void Update(RuntimeWorld world)
    {
        foreach (var animal in world.Entities.Where(e => e.Components.ContainsKey(Boar)))
        {
            var state = animal.Require(Boar);
            double time = world.Time - world.Options.TimeStep;
            double next = state.Read<double>("nextTurnAt");
            double heading = state.Read<double>("heading"), target = state.Read<double>("targetHeading");
            while (time < world.Time)
            {
                if (next <= time)
                {
                    target = world.Random(stream) * Math.Tau;
                    // Occasional investigation, sampled only at ordinary turn times. No continuous
                    // pursuit, interception or privileged response to the agent's chosen action.
                    // Probability zero consumes no additional RNG and preserves legacy trajectories.
                    if (world.HasAgent && approachProbability > 0 && world.Random(stream) < approachProbability)
                    {
                        double dx = world.Agent.Position.X - animal.Position.X, dy = world.Agent.Position.Y - animal.Position.Y;
                        target = Normalize(Math.Atan2(dy, dx) + (2 * world.Random(stream) - 1) * approachSpread);
                    }
                    next = time + Interval(world);
                    if (next <= time) throw new InvalidOperationException("Wildlife turn interval is below clock precision.");
                }
                double end = Math.Min(next, world.Time);
                Move(animal, world, end - time, ref heading, ref target);
                time = end;
            }
            state.Write("heading", heading);
            state.Write("targetHeading", target);
            state.Write("nextTurnAt", next);
        }
    }

    private void Move(RuntimeEntity animal, RuntimeWorld world, double duration, ref double heading, ref double target)
    {
        double speed = world.Options.MovementSpeed * speedFactor;
        double difference = Math.IEEERemainder(target - heading, Math.Tau);
        double turnTime = Math.Min(duration, Math.Abs(difference) / turnRate);
        double omega = Math.CopySign(turnRate, difference);
        double turned = heading + omega * turnTime;
        // Integrate a constant-speed arc, then the remaining straight segment. Folding the
        // unbounded path mirrors wall crossings, including multiple crossings in one step.
        double dx = speed * ((Math.Sin(turned) - Math.Sin(heading)) / omega + Math.Cos(turned) * (duration - turnTime));
        double dy = speed * ((Math.Cos(heading) - Math.Cos(turned)) / omega + Math.Sin(turned) * (duration - turnTime));
        heading = Normalize(turned);
        var x = Reflect(animal.Position.X + dx, world.Options.Width, Math.Cos(heading));
        var y = Reflect(animal.Position.Y + dy, world.Options.Height, Math.Sin(heading));
        animal.Position = new(x.Value, y.Value);
        if (x.Flip) { heading = Math.PI - heading; target = Math.PI - target; }
        if (y.Flip) { heading = -heading; target = -target; }
        heading = Normalize(heading); target = Normalize(target);
    }

    private static (double Value, bool Flip) Reflect(double value, double extent, double velocity)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("Non-finite wildlife movement.");
        double folded = value % (2 * extent);
        if (folded < 0) folded += 2 * extent;
        if (folded == 0) return (0, velocity < 0);
        if (folded == extent) return (extent, velocity > 0);
        return folded < extent ? (folded, false) : (2 * extent - folded, true);
    }

    private static double Normalize(double radians) => (radians % Math.Tau + Math.Tau) % Math.Tau;
    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
