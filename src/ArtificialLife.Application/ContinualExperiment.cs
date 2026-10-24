using System.Diagnostics;
using System.Text.Json;
using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record ContinualExperimentOptions
{
    public ExperimentOptions Experiment { get; init; } = new();
    public int BeforeSteps { get; init; } = 100000;
    public int AfterSteps { get; init; } = 200000;
    public int EvaluationSteps { get; init; } = 4000;
    public int[] TrainingSeeds { get; init; } = [42, 73, 101, 211, 509];
    public int[] EvaluationSeeds { get; init; } = [1001, 2002, 3003, 4004, 5005];
}
public sealed record ContinualSeedResult(int Seed, EvaluationMetrics Pretrained, EvaluationMetrics Frozen, EvaluationMetrics Random, EvaluationMetrics Adapted,
    long AgeBefore, long AgeAfter, long UpdatesBefore, long UpdatesAfter);
public sealed record ContinualReport(bool Pilot, bool Passed, double Seconds, double HungerRatioFrozen, double HungerRatioRandom,
    double TemperatureError, double TemperatureLimit, ContinualExperimentOptions Configuration, ContinualSeedResult[] Seeds);

public static class ContinualExperiment
{
    public static int Run(string[] args)
    {
        var arguments = RuntimeCommands.Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--config" or "--output" or "--pilot"))) throw new ArgumentException("Unknown continual experiment option.");
        var options = JsonSerializer.Deserialize<ContinualExperimentOptions>(File.ReadAllText(arguments.GetValueOrDefault("--config", "configs/continual.json")), PackageCatalog.Json)
            ?? throw new InvalidDataException("Missing continual experiment configuration.");
        bool pilot = arguments.GetValueOrDefault("--pilot", "false") == "true";
        if (options.BeforeSteps < 1 || options.AfterSteps < 1 || options.EvaluationSteps < 1 || options.TrainingSeeds.Length == 0 || options.EvaluationSeeds.Length == 0 ||
            options.TrainingSeeds.Intersect(options.EvaluationSeeds).Any()) throw new InvalidDataException("Invalid experiment budgets or seeds.");
        var results = new List<ContinualSeedResult>();
        var timer = Stopwatch.StartNew();
        string output = Path.GetFullPath(arguments.GetValueOrDefault("--output", "artifacts/continual-report.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string working = Path.Combine(Path.GetDirectoryName(output)!, "continual-policies-" + Guid.NewGuid().ToString("N"));
        foreach (int seed in pilot ? options.TrainingSeeds.Take(1) : options.TrainingSeeds)
        {
            var experiment = options.Experiment with { Learning = options.Experiment.Learning with { Seed = seed } };
            var beforeOptions = RuntimeDefaults.FromLegacy(experiment, hunger: false);
            using var runtime = new RuntimeSession(beforeOptions, new DqnController(experiment.Network, experiment.Learning));
            for (int i = 0; i < options.BeforeSteps; i++)
            {
                runtime.AdvanceOne();
                if ((i + 1) % 50000 == 0) Console.WriteLine($"CONTINUAL seed={seed} before={i + 1}");
            }
            long ageBefore = runtime.Snapshot().Age, updatesBefore = runtime.Controller.Status.Updates;
            string pretrainedPath = Path.Combine(working, $"{seed}-pretrained");
            RuntimeCheckpoint.Save(pretrainedPath, runtime);
            var activation = runtime.Activate(new("introduce-hunger", [new("hunger", "1.0.0"), new("food", "1.0.0")]));
            if (!activation.Accepted || runtime.Snapshot().Age != ageBefore) throw new InvalidOperationException("Live activation failed: " + activation.Message);
            string frozenPath = Path.Combine(working, $"{seed}-frozen");
            RuntimeCheckpoint.Save(frozenPath, runtime);
            for (int i = 0; i < options.AfterSteps; i++)
            {
                runtime.AdvanceOne();
                if ((i + 1) % 50000 == 0) Console.WriteLine($"CONTINUAL seed={seed} after={i + 1}");
            }
            string adaptedPath = Path.Combine(working, $"{seed}-adapted");
            RuntimeCheckpoint.Save(adaptedPath, runtime);
            int[] evaluationSeeds = pilot ? [seed + 10000, seed + 20000] : options.EvaluationSeeds;
            var pretrained = Evaluate(pretrainedPath, evaluationSeeds, options.EvaluationSteps, false);
            var frozen = Evaluate(frozenPath, evaluationSeeds, options.EvaluationSteps, false);
            var random = Evaluate(frozenPath, evaluationSeeds, options.EvaluationSteps, true);
            var adapted = Evaluate(adaptedPath, evaluationSeeds, options.EvaluationSteps, false);
            results.Add(new(seed, pretrained, frozen, random, adapted, ageBefore, runtime.Snapshot().Age, updatesBefore, runtime.Controller.Status.Updates));
            Console.WriteLine($"CONTINUAL seed={seed} hunger frozen={frozen.Drives["hunger"].MeanNormalizedError:F4} random={random.Drives["hunger"].MeanNormalizedError:F4} adapted={adapted.Drives["hunger"].MeanNormalizedError:F4} temperature pretrained={pretrained.Drives["temperature"].MeanNormalizedError:F4} frozen={frozen.Drives["temperature"].MeanNormalizedError:F4} adapted={adapted.Drives["temperature"].MeanNormalizedError:F4}");
            // Partial reports preserve expensive completed runs if a later run fails.
            WriteReport(output, pilot, timer.Elapsed.TotalSeconds, options, results);
        }
        var report = WriteReport(output, pilot, timer.Elapsed.TotalSeconds, options, results);
        Console.WriteLine($"CONTINUAL passed={report.Passed} pilot={pilot} report={output}");
        return report.Passed ? 0 : 2;
    }
    private static ContinualReport WriteReport(string path, bool pilot, double seconds, ContinualExperimentOptions config, List<ContinualSeedResult> results)
    {
        var report = Summarize(pilot, seconds, config, results);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, PackageCatalog.Json));
        File.Move(path + ".tmp", path, true);
        return report;
    }
    public static ContinualReport Summarize(bool pilot, double seconds, ContinualExperimentOptions config, IReadOnlyList<ContinualSeedResult> results)
    {
        if (results.Count == 0 || results.Select(r => r.Seed).Distinct().Count() != results.Count) throw new ArgumentException("Experiment results must be nonempty and unique.");
        double Mean(Func<ContinualSeedResult, double> selector) => results.Average(selector);
        double hunger = Mean(r => r.Adapted.Drives["hunger"].MeanNormalizedError);
        double ratioFrozen = hunger / Math.Max(1e-12, Mean(r => r.Frozen.Drives["hunger"].MeanNormalizedError));
        double ratioRandom = hunger / Math.Max(1e-12, Mean(r => r.Random.Drives["hunger"].MeanNormalizedError));
        double thermal = Mean(r => r.Adapted.Drives["temperature"].MeanNormalizedError);
        double limit = Math.Max(0.05, 1.25 * Mean(r => r.Frozen.Drives["temperature"].MeanNormalizedError));
        int[] expected = pilot ? config.TrainingSeeds.Take(1).ToArray() : config.TrainingSeeds;
        bool complete = results.Select(r => r.Seed).Order().SequenceEqual(expected.Order()) &&
            results.All(r => r.AgeBefore == config.BeforeSteps && r.AgeAfter == (long)config.BeforeSteps + config.AfterSteps && r.UpdatesAfter > r.UpdatesBefore &&
                r.Frozen.Steps == config.EvaluationSteps * (pilot ? 2 : config.EvaluationSeeds.Length) &&
                r.Random.Steps == r.Frozen.Steps && r.Adapted.Steps == r.Frozen.Steps);
        var report = new ContinualReport(pilot, complete && ratioFrozen <= 0.8 && ratioRandom <= 0.8 && thermal <= limit, seconds, ratioFrozen, ratioRandom, thermal, limit, config, results.ToArray());
        return report;
    }
    private static EvaluationMetrics Evaluate(string policyPath, int[] seeds, int steps, bool random)
    {
        var manifest = RuntimeCheckpoint.Read(policyPath);
        var metrics = new MetricAccumulator();
        foreach (int seed in seeds)
        {
            var options = manifest.Options with { WorldSeed = seed, ControllerSeed = seed, Learning = manifest.Options.Learning with { Seed = seed } };
            using var runtime = new RuntimeSession(options, new DqnController(options.Network, options.Learning),
                observationKeys: manifest.ObservationKeys, actionKeys: manifest.ActionKeys);
            runtime.Controller.Import(Path.Combine(RuntimeCheckpoint.Resolve(policyPath), "weights.bin"));
            runtime.Controller.ImportInferenceState(manifest.ControllerState);
            if (random) ((DqnController)runtime.Controller).EvaluationRandom = new(seed);
            for (int i = 0; i < steps; i++)
            {
                var sample = runtime.AdvanceOne(false);
                metrics.Add(sample.Homeostasis, sample.Reward);
            }
        }
        return metrics.Metrics();
    }
}
