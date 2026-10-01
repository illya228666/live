using ArtificialLife.Application;
using ArtificialLife.Brain;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class LearningTests
{
    [Fact]
    [Trait("Category", "Learning")]
    public void SmallExperimentImprovesHeldOutThermoregulation()
    {
        var options = new ExperimentOptions
        {
            Learning = new LearningOptions { TrainingSteps = 30000 },
            Evaluation = new EvaluationOptions { Seeds = [606, 707, 808], EpisodesPerSeed = 2 }
        };
        var session = new SimulationSession(options);
        using var brain = new DqnBrain(options.Network, options.Learning);
        EvaluationMetrics random = Evaluator.Run(session, null, options.Evaluation.Seeds);
        Trainer.Run(session, brain);
        EvaluationMetrics trained = Evaluator.Run(session, brain, options.Evaluation.Seeds);
        Assert.True(trained.MeanAbsoluteError < random.MeanAbsoluteError * 0.5,
            $"Trained MAE {trained.MeanAbsoluteError:F3} must be < half random {random.MeanAbsoluteError:F3}.");
        Assert.True(trained.ComfortPercent > random.ComfortPercent + 30,
            $"Comfort trained={trained.ComfortPercent:F1}%, random={random.ComfortPercent:F1}%.");
        Assert.True(trained.MeanReward > random.MeanReward + 0.3);
    }
}
