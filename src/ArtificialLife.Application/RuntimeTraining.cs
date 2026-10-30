using ArtificialLife.Core;
using System.Diagnostics;
using System.Text.Json;

namespace ArtificialLife.Application;

/// <summary>Bounded episodic policy over the same modular runtime used for a continuous life.</summary>
public static class RuntimeTraining
{
    public static int Run(RuntimeOptions requested, string checkpoint)
    {
        int limit = requested.Learning.TrainingSteps;
        if (limit < 1) throw new ArgumentException("Training budget must be positive.");
        var options = ControllerRegistry.Resolve(requested with { Mode = RuntimeMode.Learning });
        PolicyEvaluation random, untrained;
        using (var baseline = RuntimeSessionFactory.Create(options with { Mode = RuntimeMode.Evaluation }, ControllerRegistry.Create(options)))
        {
            random = RuntimeEvaluator.Run(baseline, random: true);
            untrained = RuntimeEvaluator.Run(baseline);
        }
        using var controller = ControllerRegistry.Create(options);
        var timer = Stopwatch.StartNew();
        long completed = 0;
        for (int episode = 0; completed < limit; episode++)
        {
            using var runtime = RuntimeSessionFactory.Create(options with { WorldSeed = checked((options.WorldSeed ?? options.Learning.Seed) + episode) }, controller, ownsController: false);
            var metrics = new MetricAccumulator();
            for (int step = 1; step <= options.World.EpisodeSteps && completed < limit; step++)
            {
                var sample = runtime.Step(true, step == options.World.EpisodeSteps);
                metrics.Add(sample.Homeostasis, sample.Reward); completed++;
                if (completed % options.Learning.LogEvery == 0) Console.WriteLine($"step={completed} episode={episode + 1} loss={controller.Status.Loss:F4} balance={metrics.Metrics().MeanBalance:F4}");
            }
            if (completed == limit) RuntimeCheckpoint.Save(checkpoint, runtime);
        }
        Directory.CreateDirectory(checkpoint);
        double trainingSeconds = timer.Elapsed.TotalSeconds;
        File.WriteAllText(Path.Combine(checkpoint, "training.json"), JsonSerializer.Serialize(new { Steps = completed, Seconds = trainingSeconds, Controller = controller.Status }, PackageCatalog.Json));
        using var trained = RuntimeCheckpoint.Open(checkpoint, RuntimeMode.Evaluation);
        var evaluated = RuntimeEvaluator.Run(trained);
        var perSeed = options.Evaluation.Seeds.Select(seed => new SeedEvaluation(seed, random.Seeds[seed], untrained.Seeds[seed], evaluated.Seeds[seed])).ToArray();
        File.WriteAllText(Path.Combine(checkpoint, "evaluation.json"), JsonSerializer.Serialize(new EvaluationReport(DateTimeOffset.UtcNow, limit, trainingSeconds, random.Aggregate, untrained.Aggregate, evaluated.Aggregate, perSeed), PackageCatalog.Json));
        Console.WriteLine($"Trained balance={evaluated.Aggregate.MeanBalance:P1} reward={evaluated.Aggregate.MeanReward:F4}; checkpoint={checkpoint}");
        return 0;
    }
}
