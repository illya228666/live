using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class RelationalSarsaTests
{
    [Theory]
    [InlineData(0, 0.199)]
    [InlineData(0.8, 0.269632)]
    public void DutchTraceMatchesTwoStepAnalyticalExample(double lambda, double expected)
    {
        using var controller = new RelationalSarsaController(new() { Alpha = 0.1, Gamma = 0.9, Lambda = lambda, EpsilonStart = 0, EpsilonEnd = 0 }, 42);
        var action = new ActionCandidate(0, [0, 0]);
        for (int i = 0; i < 2; i++) controller.Receive(new([], action, 1, [], [action], false, 0));
        string file = Path.GetTempFileName();
        try
        {
            controller.Export(file);
            Assert.Equal(expected, JsonSerializer.Deserialize<double[]>(File.ReadAllText(file))![0], 10);
        }
        finally { File.Delete(file); }
        Assert.Equal(2, controller.Status.Updates);
    }

    [Fact]
    public void FeatureKernelsArePermutationInvariantAndDoNotAssignObjectUtilities()
    {
        using var controller = new RelationalSarsaController(new(), 42);
        controller.SchemaChanged(["vision.appearance.a", "vision.appearance.b", "hunger.body.v1"], []);
        ObservationToken[] state = [new(0, [1, 0, 0.8f]), new(0, [0, 1, 0.9f]), new(1, [-1, 0, 0.85f]), new(2, [0.4f])];
        var action = new ActionCandidate(0, [1, 0]);
        Assert.Equal(controller.Features(state, action), controller.Features(state.Reverse().ToArray(), action));
        Assert.InRange(controller.Features(state, action).Sum(x => x * x), 0.99, 1.000000001);
        var left = new ActionCandidate(0, [-1, 0]);
        Assert.Same(left, controller.Choose(state, [left, action], false)); // zero weights carry no attraction/avoidance rule
    }

    [Fact]
    public void FixedNormalizationPreservesAnActionIndependentIntercept()
    {
        using var fixedController = new RelationalSarsaController(new() { Normalization = "fixed" }, 42);
        using var l2Controller = new RelationalSarsaController(new(), 42);
        foreach (var c in new[] { fixedController, l2Controller }) c.SchemaChanged(["vision.appearance.object", "hunger.body.v1"], []);
        ObservationToken[] state = [new(0, [1, 0, 0.8f]), new(1, [0.8f])];
        var towards = new ActionCandidate(0, [1, 0]);
        var away = new ActionCandidate(0, [-1, 0]);
        Assert.Equal(1, fixedController.Features(state, towards)[0]);
        Assert.Equal(1, fixedController.Features(state, away)[0]);
        Assert.NotEqual(l2Controller.Features(state, towards)[0], l2Controller.Features(state, away)[0]);
        Assert.All(fixedController.Features(state, towards).Skip(1), value => Assert.InRange(value, -0.25, 0.25));
    }

    [Fact]
    public void FrozenEvaluationPreservesCachedOnPolicyActionAndRoundTripWorks()
    {
        var options = new RelationalSarsaOptions { EpsilonStart = 1, EpsilonEnd = 1 };
        using var source = new RelationalSarsaController(options, 73);
        using var reference = new RelationalSarsaController(options, 73);
        string[] keys = ["hunger.body.v1", "vision.appearance.diamond.v1"];
        source.SchemaChanged(keys, []); reference.SchemaChanged(keys, []);
        ObservationToken[] state = [new(0, [0.4f]), new(1, [1, 0, 0.8f])];
        ActionCandidate[] actions = [new(0, [1, 0]), new(0, [-1, 0])];
        for (int i = 0; i < 100; i++)
        {
            var a = source.Choose(state, actions, true);
            var b = reference.Choose(state, actions, true);
            Assert.Equal(a.Parameters, b.Parameters);
            source.Receive(new(state, a, 0.2, state, actions, false, 0));
            reference.Receive(new(state, b, 0.2, state, actions, false, 0));
            using var frozen = source.CreateInferenceController();
            frozen.Choose(state, actions, false);
        }
        string file = Path.GetTempFileName();
        try
        {
            source.Export(file);
            using var imported = new RelationalSarsaController(options, 99);
            imported.SchemaChanged(keys, []); imported.Import(file); imported.ImportInferenceState(source.InferenceState);
            Assert.Equal(source.Choose(state, actions, false).Parameters, imported.Choose(state, actions, false).Parameters);
            Assert.Equal(source.Status.Steps, imported.Status.Steps);
        }
        finally { File.Delete(file); }
        Assert.Throws<InvalidDataException>(() => new RelationalSarsaController(new() { Lambda = 2 }, 0));
        Assert.Equal(RelationalSarsaController.Architecture, ControllerRegistry.Factory("relational-sarsa").Architecture);
    }
}
