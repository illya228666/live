using ArtificialLife.Application;
using ArtificialLife.Brain;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class TrainingVisualizationTests
{
    private static ExperimentOptions Options() => new()
    {
        World = new() { EpisodeSteps = 40 },
        Learning = new() { WarmupSteps = 64, BatchSize = 16, ReplayCapacity = 512, TrainingSteps = 1000, EpsilonDecaySteps = 1000 }
    };

    [Fact]
    public void IncrementalBatchBoundariesDoNotChangeLearning()
    {
        ExperimentOptions options = Options();
        TrainingVisualizationSnapshot whole;
        string directory = TemporaryDirectory();
        try
        {
            string wholePath = Path.Combine(directory, "whole");
            using (var session = new TrainingVisualizationSession(options))
            {
                Assert.Equal(1000, session.AdvanceTraining(1000));
                whole = session.Snapshot();
                session.SaveCheckpoint(wholePath);
            }
            using var chunks = new TrainingVisualizationSession(options);
            for (int index = 0; index < 10; index++)
            {
                Assert.Equal(100, chunks.AdvanceTraining(100));
            }
            TrainingVisualizationSnapshot split = chunks.Snapshot();
            Assert.Equal(whole.World, split.World);
            Assert.Equal(whole.Training, split.Training);
            Assert.Equal(whole.History.ToArray(), split.History.ToArray());
            Assert.Equal(whole.Trail.ToArray(), split.Trail.ToArray());
            string splitPath = Path.Combine(directory, "split");
            chunks.SaveCheckpoint(splitPath);
            Assert.Equal(Checkpoint.Read(wholePath).WeightSha256, Checkpoint.Read(splitPath).WeightSha256);
            Assert.True(split.Training.OptimizationUpdates > 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LiveAndHeadlessUseExactlyTheSameStepLogic()
    {
        ExperimentOptions options = Options();
        string directory = TemporaryDirectory();
        try
        {
            string headlessPath = Path.Combine(directory, "headless");
            var session = new SimulationSession(options);
            using (var brain = new DqnBrain(options.Network, options.Learning))
            {
                Trainer.Run(session, brain);
                Checkpoint.Save(headlessPath, session, brain);
            }
            using var live = new TrainingVisualizationSession(options);
            live.AdvanceTraining(options.Learning.TrainingSteps);
            string livePath = Path.Combine(directory, "live");
            live.SaveCheckpoint(livePath);
            Assert.Equal(Checkpoint.Read(headlessPath).WeightSha256, Checkpoint.Read(livePath).WeightSha256);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CountersEpsilonReplayAndTerminalSamplesAreCorrect()
    {
        using var session = new TrainingVisualizationSession(Options(), new() { HistorySampleInterval = 1 });
        session.AdvanceTraining(39);
        TrainingVisualizationSnapshot before = session.Snapshot();
        Assert.Equal(1, before.Training.Episode);
        Assert.Equal(39, before.Training.EpisodeStep);
        session.AdvanceTraining(1);
        TrainingVisualizationSnapshot boundary = session.Snapshot();
        Assert.Equal(40, boundary.Training.Step);
        Assert.Equal(2, boundary.Training.Episode);
        Assert.Equal(0, boundary.Training.EpisodeStep);
        Assert.Equal(40, boundary.Training.ReplayCount);
        Assert.True(boundary.History[^1].Terminal);
        Assert.Equal(1, boundary.History[^1].Episode);
        Assert.Equal(40, boundary.History[^1].EpisodeStep);
        session.AdvanceTraining(81);
        TrainingViewModel status = session.Snapshot().Training;
        Assert.Equal(121, status.Step);
        Assert.Equal(4, status.Episode);
        Assert.Equal(1, status.EpisodeStep);
        Assert.Equal(121, status.ReplayCount);
        Assert.Equal(1 - 0.95 * 121 / 1000, status.Epsilon, 10);
        Assert.Equal(15, status.OptimizationUpdates); // updates at steps 64,68,...,120
    }

    [Fact]
    public void ResetReinitializesWeightsOptimizerReplayAndAllCounters()
    {
        string directory = TemporaryDirectory();
        try
        {
            using var session = new TrainingVisualizationSession(Options());
            string initialPath = Path.Combine(directory, "initial");
            session.SaveCheckpoint(initialPath);
            session.AdvanceTraining(300);
            string learnedPath = Path.Combine(directory, "learned");
            session.SaveCheckpoint(learnedPath);
            Assert.NotEqual(Checkpoint.Read(initialPath).WeightSha256, Checkpoint.Read(learnedPath).WeightSha256);
            session.ResetTraining();
            TrainingVisualizationSnapshot reset = session.Snapshot();
            Assert.Equal(0, reset.Training.Step);
            Assert.Equal(1, reset.Training.Episode);
            Assert.Equal(0, reset.Training.EpisodeStep);
            Assert.Equal(0, reset.Training.ReplayCount);
            Assert.Equal(0, reset.Training.OptimizationUpdates);
            Assert.Equal(0, reset.Training.LastLoss);
            Assert.Equal(1, reset.Training.Epsilon);
            Assert.Equal(0, reset.Training.RecentMetrics.Steps);
            Assert.Empty(reset.History);
            Assert.Empty(reset.Trail);
            string resetPath = Path.Combine(directory, "reset");
            session.SaveCheckpoint(resetPath);
            Assert.Equal(Checkpoint.Read(initialPath).WeightSha256, Checkpoint.Read(resetPath).WeightSha256);
            session.AdvanceTraining(64);
            Assert.Equal(1, session.Snapshot().Training.OptimizationUpdates);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LiveCheckpointLoadsThroughExistingVisualizationAndEvaluation()
    {
        string directory = TemporaryDirectory();
        try
        {
            using var live = new TrainingVisualizationSession(Options());
            live.AdvanceTraining(73);
            live.SaveCheckpoint(directory);
            CheckpointManifest manifest = Checkpoint.Read(directory);
            Assert.Equal(1, manifest.FormatVersion);
            Assert.Equal(73, manifest.TrainingSteps);
            using var visualization = new VisualizationSession(directory);
            visualization.Advance();
            Assert.True(double.IsFinite(visualization.Snapshot().Agent.BodyTemperature));
            var session = new SimulationSession(manifest.Options);
            using var brain = new DqnBrain(manifest.Options.Network, manifest.Options.Learning);
            Checkpoint.Load(directory, session, brain);
            EvaluationMetrics result = Evaluator.Run(session, brain, [606]);
            Assert.Equal(160, result.Steps);
            Assert.True(double.IsFinite(result.MeanAbsoluteError));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FrozenEvaluationDoesNotModifyTrainingOrItsRng()
    {
        ExperimentOptions options = Options();
        TrainingVisualizationSnapshot expected;
        using (var baseline = new TrainingVisualizationSession(options))
        {
            baseline.AdvanceTraining(300);
            expected = baseline.Snapshot();
        }
        using var live = new TrainingVisualizationSession(options);
        live.AdvanceTraining(150);
        TrainingVisualizationSnapshot before = live.Snapshot();
        live.ToggleEvaluation();
        live.AdvanceEvaluation(75);
        TrainingVisualizationSnapshot frozen = live.Snapshot();
        Assert.True(frozen.Training.Evaluating);
        Assert.Equal(0, frozen.Training.Epsilon);
        Assert.Equal(before.Training.Step, frozen.Training.Step);
        Assert.Equal(before.Training.ReplayCount, frozen.Training.ReplayCount);
        Assert.Equal(before.Training.OptimizationUpdates, frozen.Training.OptimizationUpdates);
        Assert.Equal(75, frozen.World.Step);
        live.ToggleEvaluation();
        Assert.Equal(before.World, live.Snapshot().World);
        live.AdvanceTraining(150);
        Assert.Equal(expected.World, live.Snapshot().World);
        Assert.Equal(expected.Training, live.Snapshot().Training);
    }

    [Fact]
    public void HistoriesAreBoundedAndSnapshotsCannotBeMutatedByAdvancing()
    {
        var visual = new TrainingVisualizationOptions { HistoryLength = 7, TrailLength = 5, HistorySampleInterval = 1, RollingMetricWindow = 9 };
        using var live = new TrainingVisualizationSession(Options(), visual);
        live.AdvanceTraining(100);
        TrainingVisualizationSnapshot first = live.Snapshot();
        Assert.Equal(7, first.History.Count);
        Assert.Equal(5, first.Trail.Count);
        Assert.Equal(9, first.Training.RecentMetrics.Steps);
        TrainingSample[] history = first.History.ToArray();
        live.AdvanceTraining(25);
        Assert.Equal(history, first.History.ToArray());
        Assert.Equal(100, first.Training.Step);
        Assert.All(live.Snapshot().Trail, sample => Assert.Equal(4, sample.Episode));
    }

    [Fact]
    public void RollingMetricsEvictOldSamplesAndUseEvaluationDefinitions()
    {
        var temperature = new ArtificialLife.Modules.Temperature.TemperatureOptions { Target = 36, ComfortHalfWidth = 1 };
        var rolling = new RollingMetricAccumulator(temperature, 3);
        rolling.Add(100, -1);
        rolling.Add(36, 1);
        rolling.Add(37, 0.5);
        rolling.Add(40, -0.5);
        var expected = new MetricAccumulator(temperature);
        expected.Add(36, 1);
        expected.Add(37, 0.5);
        expected.Add(40, -0.5);
        Assert.Equal(expected.Metrics(), rolling.Metrics());
    }

    [Fact]
    public void BudgetReturnsBetweenStepsAndDisposedSessionRejectsWork()
    {
        var session = new TrainingVisualizationSession(Options());
        int completed = session.AdvanceTraining(100, TimeSpan.FromTicks(1));
        Assert.Equal(1, completed);
        Assert.Equal(completed, session.Snapshot().Training.Step);
        Assert.Equal(0, session.AdvanceTraining(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AdvanceTraining(-1));
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.AdvanceTraining(1));
        Assert.Throws<ObjectDisposedException>(() => session.ResetTraining());
    }

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ArtificialLife-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
