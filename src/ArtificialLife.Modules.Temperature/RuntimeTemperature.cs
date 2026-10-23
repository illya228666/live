using ArtificialLife.Core;

namespace ArtificialLife.Modules.Temperature;

public sealed class RuntimeTemperature : IRuntimeModule
{
    private TemperatureOptions options = new();
    public void Register(ModuleBuilder builder)
    {
        var s = builder.Settings;
        options = new()
        {
            Ambient = s.Number("ambient", 18), FireStrength = s.Number("fireStrength", 26), FireRadius = s.Number("fireRadius", 18),
            OscillationPeriod = s.Number("oscillationPeriod", 180), OscillationAmplitude = s.Number("oscillationAmplitude", 0.23),
            HeatProduction = s.Number("heatProduction", 0.2), HeatTransfer = s.Number("heatTransfer", 0.12), Target = s.Number("target", 36.6),
            SensorDistance = s.Number("sensorDistance", 3), DriveScale = s.Number("driveScale", 6), ComfortHalfWidth = s.Number("comfortHalfWidth", 1)
        };
        options.Validate();
        int body = builder.Observations.Register("temperature.body.v1");
        int sensor = builder.Observations.Register("temperature.sensor.v1");
        builder.Observations.Register("vision.appearance.disc.v1");
        builder.Components.Add(new("temperature.body", 1, (w, e) => e.Id == w.Agent.Id, (w, _, data) =>
            data.Write("temperature", options.Target + w.Random(builder.Manifest.Id) * 4 - 2)));
        builder.Components.Add(new("temperature.emitter", 1, (_, _) => false, (_, _, _) => { }));
        builder.Systems.Add(new("temperature.update", w =>
        {
            var data = w.Agent.Require("temperature.body");
            double environment = Environment(w, w.Agent.Position);
            double equilibrium = environment + options.HeatProduction / options.HeatTransfer;
            data.Write("temperature", equilibrium + (data.Read<double>("temperature") - equilibrium) * Math.Exp(-options.HeatTransfer * w.Options.TimeStep));
        }, []));
        builder.Sensors.Add(w =>
        {
            double temperature = w.Agent.Require("temperature.body").Read<double>("temperature");
            var tokens = new List<ObservationToken> { new(body, [Normalize(temperature, options.Target, 20)]) };
            foreach (var (x, y) in new (double, double)[] { (0,0), (0,-1), (1,0), (0,1), (-1,0) })
                tokens.Add(new(sensor, [Normalize(Environment(w, new(Math.Clamp(w.Agent.Position.X + x * options.SensorDistance, 0, w.Options.Width), Math.Clamp(w.Agent.Position.Y + y * options.SensorDistance, 0, w.Options.Height))), 30, 40), (float)x, (float)y]));
            return tokens;
        });
        builder.Drives.Add(w => [new("temperature", w.Agent.Require("temperature.body").Read<double>("temperature"), options.Target, options.DriveScale, 1)]);
        builder.Descriptors.Add(new("temperature", "Body temperature", "°C"));
    }
    private static float Normalize(double value, double target, double scale) => (float)Math.Clamp((value - target) / scale, -1, 1);
    public void Migrate(RuntimeWorld world, ModuleManifest? previous)
    {
        var sources = world.Entities.Where(e => e.Components.ContainsKey("temperature.emitter")).ToArray();
        if (previous is null && sources.Length == 0)
        {
            var source = world.CreateEntity(new(world.Options.Width / 2, world.Options.Height / 2));
            source.Components["temperature.emitter"] = new();
            source.Components["vision.appearance"] = new();
            source.Require("vision.appearance").Write("type", "vision.appearance.disc.v1");
            source.Require("vision.appearance").Write("diameter", 4.0);
            sources = [source];
        }
        foreach (var entity in sources)
        {
            var data = entity.Require("temperature.emitter");
            data.Write("strength", options.FireStrength); data.Write("radius", options.FireRadius);
            data.Write("period", options.OscillationPeriod); data.Write("amplitude", options.OscillationAmplitude);
        }
        world.State("temperature").Write("ambient", options.Ambient);
    }
    private double Environment(RuntimeWorld world, Position at)
    {
        double result = options.Ambient;
        foreach (var entity in world.Entities)
        {
            if (!entity.Components.TryGetValue("temperature.emitter", out var data)) continue;
            double distance = at.DistanceTo(entity.Position);
            double strength = data.Read<double>("strength") * (1 + data.Read<double>("amplitude") * Math.Sin(2 * Math.PI * world.Time / data.Read<double>("period")));
            double radius = data.Read<double>("radius");
            result += strength * Math.Exp(-distance * distance / (2 * radius * radius));
        }
        return result;
    }
}
