using ArtificialLife.Core;

namespace ArtificialLife.Modules.Temperature;

public sealed record TemperatureOptions
{
    public double Ambient { get; init; } = 18;
    public double FireStrength { get; init; } = 26;
    public double FireRadius { get; init; } = 18;
    public double OscillationPeriod { get; init; } = 180;
    public double OscillationAmplitude { get; init; } = 0.23;
    public double HeatProduction { get; init; } = 0.2;
    public double HeatTransfer { get; init; } = 0.12;
    public double Target { get; init; } = 36.6;
    public double SensorDistance { get; init; } = 3;
    public double DriveScale { get; init; } = 6;
    public double ComfortHalfWidth { get; init; } = 1;

    public void Validate()
    {
        double[] values = [Ambient, FireStrength, FireRadius, OscillationPeriod, OscillationAmplitude,
            HeatProduction, HeatTransfer, Target, SensorDistance, DriveScale, ComfortHalfWidth];
        if (values.Any(value => !double.IsFinite(value)) || FireStrength <= 0 || FireRadius <= 0 ||
            OscillationPeriod <= 0 || OscillationAmplitude is < 0 or >= 1 || HeatProduction < 0 ||
            HeatTransfer <= 0 || SensorDistance <= 0 || DriveScale <= 0 || ComfortHalfWidth <= 0)
        {
            throw new ArgumentException("Invalid thermal coefficients.");
        }
    }
}

public sealed class ThermalBody
{
    public double Temperature { get; set; }
}

/// <summary>First independent homeostasis module; the brain never references it.</summary>
public sealed class TemperatureModule : IWorldSystem, IObservationProvider, IDriveProvider
{
    private readonly int bodyType;
    private readonly int sensorType;
    public TemperatureOptions Options { get; }

    public TemperatureModule(TemperatureOptions options, TypeRegistry registry)
    {
        options.Validate();
        Options = options;
        bodyType = registry.Register("temperature.body.v1");
        sensorType = registry.Register("temperature.sensor.v1");
    }

    public double EnvironmentAt(WorldState world, Position position)
    {
        double temperature = Options.Ambient;
        foreach (Entity entity in world.Entities)
        {
            if (!entity.TryGet<HeatEmitter>(out var emitter)) continue;
            double distance = position.DistanceTo(entity.Position);
            // Гладкое гауссово поле каждого источника; вдали остаётся фоновая температура.
            temperature += emitter.Strength(world.Time) * Math.Exp(-distance * distance / (2 * emitter.Radius * emitter.Radius));
        }
        return temperature;
    }

    public void Reset(WorldState world, AgentState agent, Random random)
    {
        agent.Set(new ThermalBody { Temperature = Options.Target + (random.NextDouble() * 4 - 2) });
    }

    public void Update(WorldState world, AgentState agent, double elapsedSeconds)
    {
        ThermalBody body = agent.Get<ThermalBody>();
        double environment = EnvironmentAt(world, agent.Position);
        // Точное решение dB/dt = production + transfer*(environment-B) при постоянном окружении в шаге.
        double equilibrium = environment + Options.HeatProduction / Options.HeatTransfer;
        body.Temperature = equilibrium + (body.Temperature - equilibrium) * Math.Exp(-Options.HeatTransfer * elapsedSeconds);
    }

    public IEnumerable<ObservationToken> Observe(WorldState world, AgentState agent)
    {
        yield return new ObservationToken(bodyType, [Normalize(agent.Get<ThermalBody>().Temperature, Options.Target, 20)]);
        (float X, float Y)[] sensors = [(0, 0), (0, -1), (1, 0), (0, 1), (-1, 0)];
        foreach ((float x, float y) in sensors)
        {
            Position sample = new(Math.Clamp(agent.Position.X + x * Options.SensorDistance, 0, world.Options.Width),
                Math.Clamp(agent.Position.Y + y * Options.SensorDistance, 0, world.Options.Height));
            yield return new ObservationToken(sensorType, [Normalize(EnvironmentAt(world, sample), 30, 40), x, y]);
        }
    }

    public IEnumerable<DriveState> GetDrives(AgentState agent)
    {
        yield return new DriveState("temperature", agent.Get<ThermalBody>().Temperature, Options.Target, Options.DriveScale, 1);
    }

    private static float Normalize(double value, double center, double scale) => (float)Math.Clamp((value - center) / scale, -1, 1);
}
