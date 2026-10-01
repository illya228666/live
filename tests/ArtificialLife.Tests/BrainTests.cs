using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using TorchSharp;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class BrainTests
{
    [Fact]
    public void RaggedTrainingBatchesReleaseTemporaryTensorAliases()
    {
        var learning = new LearningOptions { BatchSize = 8, WarmupSteps = 8, ReplayCapacity = 128, TrainEvery = 1, TargetSyncInterval = 2 };
        using var brain = new DqnBrain(new NetworkOptions(), learning);
        for (int index = 1; index <= 32; index++)
        {
            ObservationToken[] state = Enumerable.Range(0, index % 7 + 1).Select(token => new ObservationToken(token % 3, [token / 10f])).ToArray();
            ActionCandidate[] actions = Enumerable.Range(0, index % 5 + 1).Select(action => new ActionCandidate(action % 2, [action / 10f])).ToArray();
            brain.Replay.Add(new Experience(state, actions[0], 0.5f, state.Reverse().ToArray(), actions, index % 3 == 0));
        }
        Assert.True(brain.Learn(1));
        long live = torch.Tensor.TotalCount;
        for (int step = 2; step <= 64; step++)
        {
            Assert.True(brain.Learn(step));
            Assert.True(double.IsFinite(brain.LastLoss));
            brain.Scores([new(0, [0.2f])], [new(0, [0, 0])]);
        }
        Assert.Equal(live, torch.Tensor.TotalCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 9)]
    [InlineData(23, 17)]
    public void EncoderAndScorerAcceptVariableSets(int tokenCount, int candidateCount)
    {
        var options = new NetworkOptions();
        using var brain = new DqnBrain(options, new LearningOptions());
        ObservationToken[] tokens = Enumerable.Range(0, tokenCount).Select(index => new ObservationToken(index % 3, [index / 30f])).ToArray();
        ActionCandidate[] candidates = Enumerable.Range(0, candidateCount).Select(index => new ActionCandidate(index % 5, [index / 30f])).ToArray();
        Assert.Equal(options.StateSize, brain.Encode(tokens).Length);
        float[] scores = brain.Scores(tokens, candidates);
        Assert.Equal(candidateCount, scores.Length);
        Assert.All(scores, score => Assert.True(float.IsFinite(score)));
    }

    [Fact]
    public void DeepSetsArePermutationInvariant()
    {
        var session = new SimulationSession(new ExperimentOptions());
        using var brain = new DqnBrain(session.Options.Network, session.Options.Learning);
        ObservationToken[] tokens = session.Simulation.Observe();
        float[] first = brain.Encode(tokens);
        float[] second = brain.Encode(tokens.Reverse().ToArray());
        for (int index = 0; index < first.Length; index++)
        {
            Assert.InRange(Math.Abs(first[index] - second[index]), 0, 1e-6);
        }
    }

    [Fact]
    public void NewTypeWithinCapacityNeedsNoModelResize()
    {
        var session = new SimulationSession(new ExperimentOptions());
        using var brain = new DqnBrain(session.Options.Network, session.Options.Learning);
        int newSense = session.Observations.Register("hypothetical.new-sense.v1");
        int newAction = session.Actions.Register("hypothetical.new-capability.v1");
        ObservationToken[] state = [.. session.Simulation.Observe(), new(newSense, [0.2f, -0.1f])];
        ActionCandidate[] actions = [.. session.Simulation.LegalActions(), new(newAction, [0.4f])];
        Assert.Equal(32, brain.Encode(state).Length);
        Assert.Equal(10, brain.Scores(state, actions).Length);
    }

    [Fact]
    public void NetworkRejectsOverflowAndInvalidNormalization()
    {
        using var brain = new DqnBrain(new NetworkOptions(), new LearningOptions());
        ActionCandidate[] actions = [new(0, [0, 0])];
        Assert.Throws<ArgumentException>(() => brain.Scores([new(256, [0])], actions));
        Assert.Throws<ArgumentException>(() => brain.Scores([new(0, [0, 0, 0, 0, 0])], actions));
        Assert.Throws<ArgumentException>(() => brain.Scores([new(0, [float.NaN])], actions));
    }

    [Fact]
    public void ExplorationAlwaysSelectsAnOfferedCandidate()
    {
        using var brain = new DqnBrain(new NetworkOptions(), new LearningOptions());
        ActionCandidate[] actions = [new(1, [0.25f]), new(4, [0.5f])];
        for (int index = 0; index < 100; index++)
        {
            Assert.Contains(brain.Choose([new(0, [0])], actions, 1), actions);
        }
        Assert.Equal(1, brain.Epsilon(0));
        Assert.Equal(0.05, brain.Epsilon(100000), 8);
    }

    [Fact]
    public void SaveLoadPreservesQValuesAndDetectsCorruption()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ArtificialLife-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var session = new SimulationSession(new ExperimentOptions());
            using var brain = new DqnBrain(session.Options.Network, session.Options.Learning);
            float[] before = brain.Scores(session.Simulation.Observe(), session.Simulation.LegalActions());
            Checkpoint.Save(directory, session, brain);
            using var loaded = new DqnBrain(session.Options.Network, session.Options.Learning with { Seed = 99 });
            Checkpoint.Load(directory, session, loaded);
            Assert.Equal(before, loaded.Scores(session.Simulation.Observe(), session.Simulation.LegalActions()));
            session.Observations.Register("new-sense");
            Assert.Throws<InvalidDataException>(() => Checkpoint.Load(directory, session, loaded));
            File.AppendAllText(Path.Combine(directory, "weights.bin"), "corrupt");
            Assert.Throws<InvalidDataException>(() => Checkpoint.Read(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
