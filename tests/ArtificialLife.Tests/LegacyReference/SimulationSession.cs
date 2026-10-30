using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
using ArtificialLife.Modules.Vision;
using ArtificialLife.Modules.Hunger;
namespace ArtificialLife.Application;
/// <summary>Composition root; new modules register here, without edits in Brain or Godot.</summary>
public sealed class SimulationSession
{
    public TypeRegistry Observations { get; }
    public TypeRegistry Actions { get; }
    public TemperatureModule Temperature { get; }
    public VisionModule Vision { get; }
    public HungerModule Hunger { get; }
    public FoodSpawner FoodSpawner { get; }
    public Entity Fire { get; }
    public Simulation Simulation { get; }
    public ExperimentOptions Options { get; }

    public SimulationSession(ExperimentOptions options, SimulationLifecycle lifecycle = SimulationLifecycle.Episodic)
    {
        options.Validate();
        Options = options;
        Observations = new TypeRegistry(options.Network.ObservationCapacity);
        Actions = new TypeRegistry(options.Network.ActionCapacity);
        Temperature = new TemperatureModule(options.Temperature, Observations);
        Vision = new VisionModule(new DirectGeometryVisionBackend(), Observations, options.Vision);
        Hunger = new HungerModule(options.Hunger, Observations);
        Fire = new Entity { Position = new Position(options.World.Width / 2, options.World.Height / 2) };
        Fire.Set(new HeatEmitter(options.Temperature.FireStrength, options.Temperature.FireRadius,
            options.Temperature.OscillationPeriod, options.Temperature.OscillationAmplitude));
        Fire.Set(new VisualAppearance(AppearanceType.Disc, diameter: 4));
        FoodSpawner = new FoodSpawner(options.FoodSpawner);
        var movement = new MovementProvider(Actions);
        Simulation = new Simulation(options.World, options.Reward, [Temperature, Hunger, FoodSpawner], [Temperature, Vision, Hunger],
            [movement], [Temperature, Hunger], lifecycle, [Fire]);
        Simulation.Reset(options.Learning.Seed);
    }
}
