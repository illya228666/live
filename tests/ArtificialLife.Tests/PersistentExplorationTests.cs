using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class PersistentExplorationTests
{
    private static ActionCandidate[] Actions() => [new(0, [1, 0]), new(0, [-1, 0]), new(0, [0, 1]), new(0, [0, -1])];

    [Fact]
    public void DurationOnePreservesTheLegacyRandomSequenceExactly()
    {
        var legacy = new Random(73);
        var explorer = new PersistentExploration(new Random(73), 1);
        var actions = Actions();
        for (int step = 0; step < 10000; step++)
        {
            double epsilon = 1 - step / 10000.0;
            var expected = legacy.NextDouble() < epsilon ? actions[legacy.Next(actions.Length)] : actions[0];
            Assert.Same(expected, explorer.Choose(actions, epsilon, () => actions[0]));
        }
    }

    [Fact]
    public void PersistencePreservesNominalExplorationOccupancyAndIncreasesLongRuns()
    {
        var explorer = new PersistentExploration(new Random(101), 32);
        var actions = Actions();
        int run = 0, longest = 0;
        ActionCandidate? previous = null;
        for (int i = 0; i < 100000; i++)
        {
            var selected = explorer.Choose(actions, 0.4, () => actions[0]);
            run = ReferenceEquals(previous, selected) ? run + 1 : 1;
            longest = Math.Max(longest, run); previous = selected;
        }
        Assert.InRange((double)explorer.ExploratoryActions / explorer.Choices, 0.38, 0.42);
        Assert.True(longest >= 25);
        Assert.InRange(explorer.MeanDuration, 2.4, 2.6);
    }

    [Fact]
    public void ChangingLegalSetsAndResetCannotReturnAStaleAction()
    {
        var explorer = new PersistentExploration(new Random(42), 32);
        var actions = Actions();
        for (int i = 0; i < 1000; i++)
        {
            var selected = explorer.Choose(actions, 1, () => throw new InvalidOperationException());
            actions = actions.Where(a => !ReferenceEquals(a, selected)).ToArray();
            if (actions.Length == 0) actions = Actions();
            if (i % 13 == 0) explorer.Reset();
        }
        Assert.True(explorer.IllegalTerminations > 0);
        explorer.Reset();
        var only = new ActionCandidate(7, [9]);
        Assert.Same(only, explorer.Choose([only], 0, () => only));
    }

    [Fact]
    public void FrozenEvaluationDoesNotConsumeExplorationAndObjectStateRestoresWeights()
    {
        var settings = new LearningOptions { ExplorationMaxDuration = 32, EpsilonStart = 1, EpsilonEnd = 1 };
        using var source = new DqnController(new(), settings);
        using var reference = new DqnController(new(), settings);
        using var frozen = source.CreateInferenceController();
        var actions = Actions();
        ObservationToken[] state = [new(0, [0.3f])];
        source.Brain.ObservationWeights[0] = 0.7f;
        for (int i = 0; i < 300; i++)
        {
            var expected = reference.Choose(state, actions, true);
            var actual = source.Choose(state, actions, true);
            Assert.Equal(expected.Parameters, actual.Parameters);
            long choices = source.Brain.Exploration.Choices;
            frozen.Choose(state, actions, false);
            Assert.Equal(choices, source.Brain.Exploration.Choices);
        }
        using var restored = new DqnController(new(), settings);
        restored.ImportInferenceState(source.InferenceState);
        Assert.Equal(source.Brain.ObservationWeights, restored.Brain.ObservationWeights);
        Assert.Equal(300, source.InferenceState.GetProperty("exploration").GetProperty("Choices").GetInt64());
    }
}
