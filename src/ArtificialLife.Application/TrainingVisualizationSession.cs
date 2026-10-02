using System.Collections.ObjectModel;
using System.Diagnostics;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;

namespace ArtificialLife.Application;

/// <summary>Safe values copied from the single mutable training owner.</summary>
public sealed record TrainingViewModel(int Step, int Life, long Age,
    double Epsilon, double LastLoss, int OptimizationUpdates, int ReplayCount, int ReplayCapacity,
    EvaluationMetrics RecentMetrics, bool Evaluating);

public sealed record TrainingVisualizationSnapshot(WorldViewModel World, TrainingViewModel Training,
    ReadOnlyCollection<TrainingSample> History, ReadOnlyCollection<TrainingSample> Trail);

/// <summary>Incremental live learning; owns simulation/brain lifetime and exposes no native tensors.</summary>
/// <remarks>All operations run synchronously on one owner thread. A frontend supplies bounded batches.</remarks>
public sealed class TrainingVisualizationSession : IDisposable
{
    private readonly ExperimentOptions options;
    private readonly TrainingVisualizationOptions visualOptions;
    private SimulationSession session = null!;
    private DqnBrain brain = null!;
    private TrainingEngine engine = null!;
    private RollingMetricAccumulator metrics = null!;
    private SimulationSession? evaluation;
    private RollingMetricAccumulator? evaluationMetrics;
    private int evaluationStep;
    private readonly Queue<TrainingSample> history = new();
    private readonly Queue<TrainingSample> trail = new();
    private bool disposed;
    private int life;

    public bool Evaluating => evaluation is not null;

    public TrainingVisualizationSession(ExperimentOptions options, TrainingVisualizationOptions? visualOptions = null)
    {
        options.Validate();
        this.options = options;
        this.visualOptions = visualOptions ?? new TrainingVisualizationOptions();
        this.visualOptions.Validate();
        ResetTraining();
    }

    /// <summary>Advances real training, returning early between steps when the optional time budget expires.</summary>
    public int AdvanceTraining(int numberOfSteps, TimeSpan? timeBudget = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Evaluating)
        {
            throw new InvalidOperationException("Leave frozen evaluation before advancing training.");
        }
        ValidateBatch(numberOfSteps, timeBudget);
        var stopwatch = timeBudget.HasValue ? Stopwatch.StartNew() : null;
        int completed = 0;
        while (completed < numberOfSteps)
        {
            TrainingSample sample = engine.AdvanceOne();
            metrics.Add(sample.BodyTemperature, sample.Reward);
            Record(sample);
            completed++;
            // Бюджет проверяется между полными переходами: оптимизация никогда не прерывается посередине.
            if (stopwatch is not null && stopwatch.Elapsed >= timeBudget!.Value)
            {
                break;
            }
        }
        return completed;
    }

    /// <summary>Runs the frozen policy in an independent world; training state, replay and RNG remain untouched.</summary>
    public int AdvanceEvaluation(int numberOfSteps, TimeSpan? timeBudget = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (evaluation is null)
        {
            throw new InvalidOperationException("Enter frozen evaluation first.");
        }
        ValidateBatch(numberOfSteps, timeBudget);
        var stopwatch = timeBudget.HasValue ? Stopwatch.StartNew() : null;
        int completed = 0;
        while (completed < numberOfSteps)
        {
            Simulation simulation = evaluation.Simulation;
            ActionCandidate action = brain.ChooseGreedy(simulation.Observe(), simulation.LegalActions());
            StepResult result = simulation.Step(action);
            double body = simulation.Agent.Get<ThermalBody>().Temperature;
            evaluationStep++;
            evaluationMetrics!.Add(body, result.Reward);
            Record(new TrainingSample(evaluationStep, 0, evaluationStep, simulation.Agent.Position.X,
                simulation.Agent.Position.Y, body, Math.Abs(body - options.Temperature.Target), result.Reward, false));
            completed++;
            if (stopwatch is not null && stopwatch.Elapsed >= timeBudget!.Value)
            {
                break;
            }
        }
        return completed;
    }

    public void ToggleEvaluation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Evaluating)
        {
            evaluation = null;
            evaluationMetrics = null;
        }
        else
        {
            evaluation = new SimulationSession(options, SimulationLifecycle.Continuous);
            evaluation.Simulation.Reset(options.Evaluation.Seeds[0]);
            evaluationMetrics = new RollingMetricAccumulator(options.Temperature, visualOptions.RollingMetricWindow);
            evaluationStep = 0;
        }
        history.Clear();
        trail.Clear();
    }

    /// <summary>Disposes the old optimizer/model and restarts from the configured deterministic seed.</summary>
    public void ResetTraining()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        brain?.Dispose();
        session = new SimulationSession(options, SimulationLifecycle.Continuous);
        brain = new DqnBrain(options.Network, options.Learning);
        engine = new TrainingEngine(session, brain);
        life++;
        metrics = new RollingMetricAccumulator(options.Temperature, visualOptions.RollingMetricWindow);
        evaluation = null;
        evaluationMetrics = null;
        evaluationStep = 0;
        history.Clear();
        trail.Clear();
    }

    public TrainingVisualizationSnapshot Snapshot()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SimulationSession displayed = evaluation ?? session;
        var training = new TrainingViewModel(engine.Step, life, session.Simulation.World.Step,
            Evaluating ? 0 : brain.Epsilon(engine.Step), brain.LastLoss, brain.OptimizationUpdates,
            brain.Replay.Count, options.Learning.ReplayCapacity, (evaluationMetrics ?? metrics).Metrics(), Evaluating);
        WorldViewModel world = WorldSnapshots.Create(displayed, Evaluating ? evaluationStep : engine.Step,
            Evaluating ? "Evaluation · frozen policy · ε = 0" : "Live Training");
        return new TrainingVisualizationSnapshot(world, training, Array.AsReadOnly(history.ToArray()), Array.AsReadOnly(trail.ToArray()));
    }

    public void SaveCheckpoint(string directory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Checkpoint.Save(directory, session, brain, engine.Step);
    }

    private void Record(TrainingSample sample)
    {
        trail.Enqueue(sample);
        if (trail.Count > visualOptions.TrailLength)
        {
            trail.Dequeue();
        }
        if (sample.Step % visualOptions.HistorySampleInterval == 0 || sample.Terminal)
        {
            history.Enqueue(sample);
            if (history.Count > visualOptions.HistoryLength)
            {
                history.Dequeue();
            }
        }
    }

    private static void ValidateBatch(int count, TimeSpan? timeBudget)
    {
        if (count < 0 || (timeBudget.HasValue && timeBudget.Value <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Step count must be nonnegative; time budget must be positive.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        brain.Dispose();
        disposed = true;
    }
}
