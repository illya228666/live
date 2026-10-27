using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class GenericActorCriticTests
{
    private static readonly ActionCandidate[] Actions = [new(0, [-1]), new(0, [1])];

    [Fact]
    public void LearnsAnUnknownTypedChannelWithoutSemanticKeys()
    {
        using var controller = new GenericActorCriticController(new() { Features = 32, Gamma = 0, Lambda = 0, ActorStep = 0.1 }, 42);
        controller.SchemaChanged(["opaque-a", "opaque-b"], ["opaque-action"]);
        controller.ConditionsChanged(1);
        for (int i = 0; i < 4000; i++)
        {
            float sign = i % 2 == 0 ? -1 : 1;
            ObservationToken[] state = [new(1, [sign])];
            var action = controller.Choose(state, Actions, true);
            controller.Receive(new(state, action, action.Parameters[0] == sign ? 1 : -1, state, Actions, true, 1));
        }
        Assert.True(controller.ActionProbabilities([new(1, [-1])], Actions)[0] > 0.8);
        Assert.True(controller.ActionProbabilities([new(1, [1])], Actions)[1] > 0.8);
    }

    [Fact]
    public void ExtensionAndPermutationPreserveTheExistingPolicyAndFreezeKeepsTheExplorationClock()
    {
        using var controller = new GenericActorCriticController(new() { Features = 16, FreezeAfterSteps = 20, Exploration = "coverage", CountCapacity = 8 }, 73);
        ObservationToken[] state = [new(0, [0.1f]), new(1, [-0.3f, 0.5f])];
        controller.ConditionsChanged(1);
        for (int i = 0; i < 20; i++) Step(controller, state);
        var before = controller.ActionProbabilities(state, Actions);
        controller.SchemaChanged(["renamed-a", "renamed-b", "never-seen"], ["renamed-action", "another-action"]);
        Assert.Equal(before, controller.ActionProbabilities(state.Reverse().ToArray(), Actions));
        for (int i = 0; i < 20; i++) Step(controller, state);
        Assert.Equal(before, controller.ActionProbabilities(state, Actions));
        Assert.Equal(40, controller.Status.Steps); Assert.Equal(20, controller.Status.Updates);
        var newState = state.Append(new ObservationToken(2, [0.7f])).ToArray();
        ActionCandidate[] more = [.. Actions, new(1, [0.25f, -0.6f])];
        Assert.Contains(controller.Choose(newState, more, true), more);
    }

    [Fact]
    public void InferenceDoesNotConsumeTrainingRngOrChangeLaterUpdates()
    {
        using var a = new GenericActorCriticController(new() { Features = 16 }, 101);
        using var b = new GenericActorCriticController(new() { Features = 16 }, 101);
        a.ConditionsChanged(1); b.ConditionsChanged(1);
        ObservationToken[] state = [new(0, [0.2f])];
        for (int i = 0; i < 30; i++) { Step(a, state); Step(b, state); }
        using (var frozen = a.CreateInferenceController())
            for (int i = 0; i < 100; i++) frozen.Choose([new(2, [-0.5f])], Actions, false);
        for (int i = 0; i < 30; i++)
        {
            var action = a.Choose(state, Actions, true);
            var second = b.Choose(state, Actions, true);
            Assert.Equal(action, second);
            a.Receive(new(state, action, 0.4, state, Actions, false, 1));
            b.Receive(new(state, second, 0.4, state, Actions, false, 1));
        }
        Assert.Equal(a.ActionProbabilities(state, Actions), b.ActionProbabilities(state, Actions));
    }

    private static void Step(GenericActorCriticController controller, ObservationToken[] state)
    {
        var action = controller.Choose(state, Actions, true);
        controller.Receive(new(state, action, action.Parameters[0], state, Actions, false, 1));
    }
}
