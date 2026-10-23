using System.Collections.ObjectModel;
using System.Diagnostics;
using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record TrainingViewModel(long Step, int Life, long Age,
    double Epsilon, double LastLoss, long OptimizationUpdates, int ReplayCount, int ReplayCapacity,
    EvaluationMetrics RecentMetrics, bool Evaluating);
public sealed record TrainingVisualizationSnapshot(WorldViewModel World, TrainingViewModel Training,
    ReadOnlyCollection<TrainingSample> History, ReadOnlyCollection<TrainingSample> Trail);

/// <summary>Bounded UI adapter; the same RuntimeSession also runs in the console.</summary>
public sealed class TrainingVisualizationSession : IDisposable
{
    private readonly RuntimeOptions options;
    private readonly TrainingVisualizationOptions visualOptions;
    private RuntimeSession runtime = null!;
    private RuntimeSession? evaluation;
    private RollingMetricAccumulator metrics = null!;
    private RollingMetricAccumulator? evaluationMetrics;
    private readonly Queue<TrainingSample> history = new();
    private readonly Queue<TrainingSample> trail = new();
    private bool disposed;
    private int life;
    public RuntimeSession Runtime => runtime;
    public bool Evaluating => evaluation is not null;
    public TrainingVisualizationSession(ExperimentOptions options, TrainingVisualizationOptions? visualOptions = null)
        : this(RuntimeDefaults.FromLegacy(options), visualOptions) { }
    public TrainingVisualizationSession(RuntimeOptions options, TrainingVisualizationOptions? visualOptions = null)
    {
        this.options = options;
        this.visualOptions = visualOptions ?? new();
        this.visualOptions.Validate();
        ResetTraining();
    }
    public int AdvanceTraining(int numberOfSteps, TimeSpan? timeBudget = null)
    {
        if (Evaluating) throw new InvalidOperationException("Leave evaluation first.");
        return Advance(numberOfSteps, timeBudget, false);
    }
    public int AdvanceEvaluation(int numberOfSteps, TimeSpan? timeBudget = null)
    {
        if (!Evaluating) throw new InvalidOperationException("Enter evaluation first.");
        return Advance(numberOfSteps, timeBudget, true);
    }
    private int Advance(int count, TimeSpan? budget, bool evaluating)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (count < 0 || budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(count));
        var timer = Stopwatch.StartNew();
        int completed = 0;
        while (completed < count)
        {
            var sample = (evaluating ? evaluation! : runtime).AdvanceOne(!evaluating);
            (evaluating ? evaluationMetrics! : metrics).Add(sample.Homeostasis, sample.Reward);
            trail.Enqueue(sample);
            while (trail.Count > visualOptions.TrailLength) trail.Dequeue();
            if (sample.Step % visualOptions.HistorySampleInterval == 0)
            {
                history.Enqueue(sample);
                while (history.Count > visualOptions.HistoryLength) history.Dequeue();
            }
            completed++;
            if (budget.HasValue && timer.Elapsed >= budget.Value) break;
        }
        return completed;
    }
    public void ToggleEvaluation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (evaluation is not null) { evaluation.Dispose(); evaluation = null; evaluationMetrics = null; }
        else
        {
            var active = options with
            {
                Packages = runtime.Packages.Select(p => new PackageReference(p.Id, p.Version)).ToArray(),
                Settings = runtime.Packages.ToDictionary(p => p.Id, p => p.Settings.Clone())
            };
            evaluation = new(active, runtime.Controller.CreateInferenceController(), observationKeys: runtime.ObservationKeys, actionKeys: runtime.ActionKeys);
            evaluationMetrics = new(visualOptions.RollingMetricWindow);
        }
        history.Clear(); trail.Clear();
    }
    public void ResetTraining()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var presentation = runtime?.PresentationHost;
        evaluation?.Dispose(); evaluation = null; evaluationMetrics = null;
        runtime?.Dispose();
        runtime = new(options, new DqnController(options.Network, options.Learning), presentationHost: presentation);
        metrics = new(visualOptions.RollingMetricWindow);
        history.Clear(); trail.Clear(); life++;
    }
    public TrainingVisualizationSnapshot Snapshot()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var displayed = evaluation ?? runtime;
        var status = runtime.Controller.Status;
        var training = new TrainingViewModel(status.Steps, life, runtime.Snapshot().Age, Evaluating ? 0 : status.Epsilon,
            status.Loss, status.Updates, status.ReplayCount, options.Learning.ReplayCapacity,
            (evaluationMetrics ?? metrics).Metrics(), Evaluating);
        return new(new(displayed.Snapshot(), displayed.LastReward, Evaluating ? "Evaluation · frozen policy" : "Live training", displayed.Report()),
            training, Array.AsReadOnly(history.ToArray()), Array.AsReadOnly(trail.ToArray()));
    }
    public void SaveCheckpoint(string directory) => RuntimeCheckpoint.Save(directory, runtime);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        evaluation?.Dispose(); runtime.Dispose();
    }
}
