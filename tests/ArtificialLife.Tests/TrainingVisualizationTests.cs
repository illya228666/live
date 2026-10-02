using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using ArtificialLife.Modules.Temperature;
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
    public void ContinuousLifeCrossesThreeEpisodeBoundariesWithoutResetOrTerminalReplay()
    {
        ExperimentOptions options = Options() with
        {
            World = new() { EpisodeSteps = 400 },
            Learning = Options().Learning with { ReplayCapacity = 2048 }
        };
        var session = new SimulationSession(options, SimulationLifecycle.Continuous);
        Simulation simulation = session.Simulation;
        ThermalBody body = simulation.Agent.Get<ThermalBody>();
        double initialTime = simulation.World.Time;
        using var brain = new DqnBrain(options.Network, options.Learning);
        var engine = new TrainingEngine(session, brain);
        Assert.Same(body, simulation.Agent.Get<ThermalBody>()); // Engine must not initialize a second body.
        for (int step = 1; step <= 1201; step++)
        {
            double oldTime = simulation.World.Time;
            Position oldPosition = simulation.Agent.Position;
            double oldBody = body.Temperature;
            TrainingSample sample = engine.AdvanceOne();
            Assert.False(sample.Terminal);
            Assert.Equal(1, engine.Episode);
            Assert.Equal(step, simulation.World.Step);
            Assert.Equal(initialTime + step * options.World.TimeStep, simulation.World.Time, 9);
            Assert.Equal(oldTime + options.World.TimeStep, simulation.World.Time, 9);
            Assert.Same(body, simulation.Agent.Get<ThermalBody>());
            Assert.True(oldPosition.DistanceTo(simulation.Agent.Position) <= options.World.MovementSpeed * options.World.TimeStep + 1e-9);
            double equilibrium = session.Temperature.EnvironmentAt(simulation.World, simulation.Agent.Position)
                + options.Temperature.HeatProduction / options.Temperature.HeatTransfer;
            double expectedBody = equilibrium + (oldBody - equilibrium) * Math.Exp(-options.Temperature.HeatTransfer * options.World.TimeStep);
            Assert.Equal(expectedBody, body.Temperature, 10);
            Assert.Equal(body.Temperature, sample.BodyTemperature);
            Assert.Equal(step, brain.Replay.Count);
        }
        // Read every slot through the existing sampler, including all former terminal boundaries.
        Assert.All(brain.Replay.Sample(1201, new SequentialRandom()), experience => Assert.False(experience.Terminal));
        Assert.Equal(285, brain.OptimizationUpdates); // 64,68,...,1200; no restarts at 400/800/1200.
        Assert.Equal(options.Learning.EpsilonEnd, brain.Epsilon(engine.Step));
    }

    private sealed class SequentialRandom : Random
    {
        private int index;
        public override int Next(int maxValue) => index++ % maxValue;
    }

    [Fact]
    public void LiveHistoriesRetainSamplesAcrossFormerEpisodeBoundaries()
    {
        using var live = new TrainingVisualizationSession(Options(), new()
        {
            HistoryLength = 200, TrailLength = 200, HistorySampleInterval = 1
        });
        live.AdvanceTraining(121);
        TrainingVisualizationSnapshot snapshot = live.Snapshot();
        Assert.Equal(121, snapshot.History.Count);
        Assert.Equal(121, snapshot.Trail.Count);
        Assert.Equal(Enumerable.Range(1, 121), snapshot.Trail.Select(sample => sample.Step));
        Assert.All(snapshot.History, sample => Assert.False(sample.Terminal));
        Assert.Equal(1, snapshot.Training.Life);
        Assert.Equal(121, snapshot.Training.Age);
    }

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
    public void HeadlessRetainsOriginalEpisodicLearning()
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
            var reference = new SimulationSession(options);
            using var referenceBrain = new DqnBrain(options.Network, options.Learning);
            ObservationToken[] state = reference.Simulation.Observe();
            ActionCandidate[] actions = reference.Simulation.LegalActions();
            int episode = 0;
            for (int step = 1; step <= options.Learning.TrainingSteps; step++)
            {
                ActionCandidate action = referenceBrain.Choose(state, actions, referenceBrain.Epsilon(step));
                StepResult result = reference.Simulation.Step(action);
                referenceBrain.Replay.Add(new Experience(state, action, (float)result.Reward, result.Observations, result.Actions, result.Terminal));
                referenceBrain.Learn(step);
                state = result.Observations;
                actions = result.Actions;
                Assert.Equal(step % options.World.EpisodeSteps == 0, result.Terminal);
                if (result.Terminal)
                {
                    reference.Simulation.Reset(options.Learning.Seed + ++episode);
                    state = reference.Simulation.Observe();
                    actions = reference.Simulation.LegalActions();
                }
            }
            Assert.Equal(25, episode);
            Assert.Equal(0, session.Simulation.World.Step);
            Assert.Equal(reference.Simulation.World.Time, session.Simulation.World.Time);
            Assert.Equal(reference.Simulation.Agent.Position, session.Simulation.Agent.Position);
            string referencePath = Path.Combine(directory, "reference");
            Checkpoint.Save(referencePath, reference, referenceBrain);
            Assert.Equal(Checkpoint.Read(headlessPath).WeightSha256, Checkpoint.Read(referencePath).WeightSha256);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ContinuousCountersEpsilonReplayAndNonterminalSamplesAreCorrect()
    {
        using var session = new TrainingVisualizationSession(Options(), new() { HistorySampleInterval = 1 });
        session.AdvanceTraining(39);
        TrainingVisualizationSnapshot before = session.Snapshot();
        Assert.Equal(1, before.Training.Life);
        Assert.Equal(39, before.Training.Age);
        session.AdvanceTraining(1);
        TrainingVisualizationSnapshot boundary = session.Snapshot();
        Assert.Equal(40, boundary.Training.Step);
        Assert.Equal(1, boundary.Training.Life);
        Assert.Equal(40, boundary.Training.Age);
        Assert.Equal(40, boundary.Training.ReplayCount);
        Assert.False(boundary.History[^1].Terminal);
        Assert.Equal(1, boundary.History[^1].Episode);
        Assert.Equal(40, boundary.History[^1].EpisodeStep);
        session.AdvanceTraining(81);
        TrainingViewModel status = session.Snapshot().Training;
        Assert.Equal(121, status.Step);
        Assert.Equal(1, status.Life);
        Assert.Equal(121, status.Age);
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
            TrainingVisualizationSnapshot initial = session.Snapshot();
            string initialPath = Path.Combine(directory, "initial");
            session.SaveCheckpoint(initialPath);
            session.AdvanceTraining(300);
            string learnedPath = Path.Combine(directory, "learned");
            session.SaveCheckpoint(learnedPath);
            Assert.NotEqual(Checkpoint.Read(initialPath).WeightSha256, Checkpoint.Read(learnedPath).WeightSha256);
            session.ResetTraining();
            TrainingVisualizationSnapshot reset = session.Snapshot();
            Assert.Equal(0, reset.Training.Step);
            Assert.Equal(2, reset.Training.Life);
            Assert.Equal(0, reset.Training.Age);
            Assert.Equal(initial.World, reset.World);
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
        Assert.All(live.Snapshot().Trail, sample => Assert.Equal(1, sample.Episode));
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
