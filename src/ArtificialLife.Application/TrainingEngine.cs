using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

/// <summary>An actual completed transition, captured before an episode reset.</summary>
public sealed record TrainingSample(int Step, int Episode, int EpisodeStep, double X, double Y,
    double BodyTemperature, double AbsoluteError, double Reward, bool Terminal);

/// <summary>One incremental training loop shared by the CLI and presentation sessions.</summary>
/// <remarks>The caller owns the supplied brain. This engine must have one synchronous owner.</remarks>
public sealed class TrainingEngine
{
    private readonly SimulationSession session;
    private readonly DqnBrain brain;
    private ObservationToken[] state;
    private ActionCandidate[] actions;

    public int Step { get; private set; }
    public int Episode { get; private set; } = 1;
    public int EpisodeStep => checked((int)session.Simulation.World.Step);

    public TrainingEngine(SimulationSession session, DqnBrain brain)
    {
        this.session = session;
        this.brain = brain;
        session.Simulation.Reset(session.Options.Learning.Seed);
        state = session.Simulation.Observe();
        actions = session.Simulation.LegalActions();
    }

    public TrainingSample AdvanceOne()
    {
        int nextStep = checked(Step + 1);
        ActionCandidate action = brain.Choose(state, actions, brain.Epsilon(nextStep));
        StepResult result = session.Simulation.Step(action);
        brain.Replay.Add(new Experience(state, action, (float)result.Reward, result.Observations, result.Actions, result.Terminal));
        brain.Learn(nextStep);
        Step = nextStep;
        state = result.Observations;
        actions = result.Actions;
        Simulation simulation = session.Simulation;
        double body = simulation.Agent.Get<ThermalBody>().Temperature;
        var sample = new TrainingSample(Step, Episode, EpisodeStep, simulation.Agent.Position.X, simulation.Agent.Position.Y,
            body, Math.Abs(body - session.Options.Temperature.Target), result.Reward, result.Terminal);
        if (result.Terminal)
        {
            // Терминальное состояние уже записано в replay и sample; reset не смешивает эпизоды.
            Episode++;
            simulation.Reset(checked(session.Options.Learning.Seed + Episode - 1));
            state = simulation.Observe();
            actions = simulation.LegalActions();
        }
        return sample;
    }
}
