using System.Diagnostics;
using System.Text.Json;
using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public static class RuntimeCommands
{
    public static Dictionary<string, string> Arguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Options must be unique --key value pairs.");
        return values;
    }
    public static RuntimeOptions ReadOptions(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("packages", out _) || json.RootElement.TryGetProperty("Packages", out _)
            ? RuntimeOptions.Load(path) : RuntimeDefaults.FromLegacy(ExperimentOptions.Load(path));
    }
    public static int EvaluatePolicy(string[] args)
    {
        var arguments = Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--checkpoint" or "--steps"))) throw new ArgumentException("Unknown policy evaluation option.");
        string path = arguments.GetValueOrDefault("--checkpoint", "artifacts/checkpoints/thermal");
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(RuntimeCheckpoint.Resolve(path), "manifest.json")));
        long trainingSteps = metadata.RootElement.GetProperty("FormatVersion").GetInt32() == 1 ? Checkpoint.Read(path).TrainingSteps : RuntimeCheckpoint.Read(path).TrainingSteps;
        using var runtime = RuntimeCheckpoint.Open(path, RuntimeMode.Evaluation);
        EvaluationMetrics metrics;
        IReadOnlyDictionary<int, EvaluationMetrics>? seeds = null;
        if (arguments.TryGetValue("--steps", out var value))
        {
            int count = int.Parse(value);
            if (count < 1) throw new ArgumentException("Evaluation steps must be positive.");
            var accumulator = new MetricAccumulator();
            for (int i = 0; i < count; i++) { var sample = runtime.Step(false); accumulator.Add(sample.Homeostasis, sample.Reward); }
            metrics = accumulator.Metrics();
        }
        else
        {
            var evaluated = RuntimeEvaluator.Run(runtime);
            metrics = evaluated.Aggregate; seeds = evaluated.Seeds;
        }
        var report = new { TrainingSteps = trainingSteps, Evaluation = metrics, PerSeed = seeds };
        File.WriteAllText(Path.Combine(path, "reevaluation.json"), JsonSerializer.Serialize(report, PackageCatalog.Json));
        Print(runtime, metrics, evaluation: true);
        return 0;
    }
    private static void Print(RuntimeSession runtime, EvaluationMetrics metrics, bool evaluation = false)
    {
        var state = runtime.Controller.Status;
        Console.WriteLine($"age={runtime.Snapshot().Age} training={state.Steps} revision={runtime.Revision} epsilon={(evaluation ? 0 : state.Epsilon):F3} replay={state.ReplayCount} eligible={state.EligibleReplayCount} updates={state.Updates} loss={state.Loss:F4} balance={metrics.MeanBalance:P1} needs=[{string.Join(", ", metrics.Drives.Select(p => $"{p.Key}:{p.Value.MeanSatisfaction:P1}"))}]");
    }
}
