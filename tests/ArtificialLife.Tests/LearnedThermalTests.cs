using ArtificialLife.Brain;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class LearnedThermalTests
{
    [Fact]
    public void ModelLearnsUnknownTransitionCoefficientsAndPredictsHeldOutInputs()
    {
        var model = new LearnedThermalTransition();
        var rng = new Random(42);
        for (int i = 0; i < 2000; i++)
        {
            double body = rng.NextDouble() - 0.5, field = rng.NextDouble() - 0.5;
            model.Observe(body, field, 0.83 * body + 0.27 * field - 0.017);
        }
        Assert.Equal(0.83 * 0.77 + 0.27 * -0.62 - 0.017, model.Predict(0.77, -0.62), 5);
        Assert.Equal(2000, model.Samples);
        Assert.Throws<InvalidDataException>(() => model.Observe(double.NaN, 0, 0));
    }

    [Fact]
    public void WarmupLearnsThenFreezesTheModelAndInferenceRestoresItsFeatures()
    {
        var options = new RelationalSarsaOptions { Normalization = "fixed", UseThermalModel = true, WarmupSteps = 40 };
        using var controller = new RelationalSarsaController(options, 42);
        string[] keys = ["temperature.body.v1", "temperature.sensor.v1"];
        controller.SchemaChanged(keys, []);
        var action = new ActionCandidate(0, [0, 0]);
        ObservationToken[] State(float b, float f) => [new(0, [b]), new(1, [f, 0, 0])];
        for (int i = 0; i < 60; i++)
        {
            float b = (i % 5 - 2) / 10f, f = (i % 7 - 3) / 10f;
            controller.Receive(new(State(b, 0), action, 0.3, State(0.83f * b + 0.27f * f - 0.017f, f), [action], false, 0));
        }
        Assert.Equal(40, controller.ThermalModel.Samples);
        Assert.Equal(20, controller.Status.Updates);
        using var clone = controller.CreateInferenceController();
        clone.Choose(State(0.1f, 0.3f), [action], false);
        Assert.Equal(40, controller.ThermalModel.Samples);
        using var restored = new RelationalSarsaController(options, 99);
        restored.SchemaChanged(keys, []); restored.ImportInferenceState(controller.InferenceState);
        Assert.Equal(controller.ThermalModel.Coefficients, restored.ThermalModel.Coefficients);
        Assert.Equal(controller.Features(State(0.1f, 0.3f), action), restored.Features(State(0.1f, 0.3f), action));
    }
}
