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
    public static int Live(string[] args)
    {
        var arguments = Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--config" or "--steps" or "--checkpoint" or "--trajectory"))) throw new ArgumentException("Unknown live option.");
        var options = ReadOptions(arguments.GetValueOrDefault("--config", "configs/runtime.json"));
        long limit = arguments.TryGetValue("--steps", out string? text) ? long.Parse(text) : long.MaxValue;
        if (limit <= 0) throw new ArgumentException("Steps must be positive.");
        using var runtime = new RuntimeSession(options, new DqnController(options.Network, options.Learning));
        using var trajectory = arguments.TryGetValue("--trajectory", out string? trajectoryPath) ? new StreamWriter(trajectoryPath) : null;
        long loggedRevision = -1;
        if (trajectory is not null) runtime.TrajectorySink = t =>
        {
            if (loggedRevision != t.Revision)
            {
                trajectory.WriteLine(JsonSerializer.Serialize(new { Kind = "schema", t.Revision, runtime.ObservationKeys, runtime.ActionKeys, Modules = runtime.Packages }));
                loggedRevision = t.Revision;
            }
            trajectory.WriteLine(JsonSerializer.Serialize(new { Kind = "transition", Transition = t }));
        };
        var inbox = new ActivationInbox(options.Inbox);
        var metrics = new RollingMetricAccumulator(1000);
        var timer = Stopwatch.StartNew();
        var nextPoll = TimeSpan.Zero;
        long nextLog = 5000;
        bool stop = false;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop = true; };
        Console.CancelKeyPress += handler;
        Console.WriteLine($"LIVE started; packages={string.Join(',', runtime.Packages.Select(p => p.Id))}; inbox={inbox.Directory}");
        try
        {
            for (long i = 0; i < limit && !stop; i++)
            {
                if (timer.Elapsed >= nextPoll)
                {
                    runtime.Poll(inbox);
                    nextPoll = timer.Elapsed + TimeSpan.FromSeconds(1);
                    if (runtime.LastActivation is { Id: not "initial" } result) Console.WriteLine($"ACTIVATION {result.Id} accepted={result.Accepted} revision={result.Revision} {result.Message}");
                }
                var sample = runtime.AdvanceOne();
                metrics.Add(sample.Homeostasis, sample.Reward);
                if (sample.Step >= nextLog) { Print(runtime, metrics.Metrics()); nextLog += 5000; }
            }
            Print(runtime, metrics.Metrics());
            if (arguments.TryGetValue("--checkpoint", out string? path)) RuntimeCheckpoint.Save(path, runtime);
            Console.WriteLine($"LIVE finished age={runtime.Snapshot().Age} revision={runtime.Revision}; life was never reset.");
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    public static int EvaluatePolicy(string[] args)
    {
        var arguments = Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--checkpoint" or "--steps"))) throw new ArgumentException("Unknown policy evaluation option.");
        string path = arguments.GetValueOrDefault("--checkpoint", "artifacts/checkpoints/live-thermal");
        var policy = RuntimeCheckpoint.Read(path);
        int count = arguments.TryGetValue("--steps", out var value) ? int.Parse(value) : 4000;
        if (count < 1) throw new ArgumentException("Evaluation steps must be positive.");
        using var runtime = RuntimeCheckpoint.Open(path);
        var metrics = new MetricAccumulator();
        for (int i = 0; i < count; i++) { var sample = runtime.AdvanceOne(false); metrics.Add(sample.Homeostasis, sample.Reward); }
        var report = new { policy.TrainingSteps, Evaluation = metrics.Metrics() };
        File.WriteAllText(Path.Combine(path, "reevaluation.json"), JsonSerializer.Serialize(report, PackageCatalog.Json));
        Print(runtime, metrics.Metrics(), evaluation: true);
        return 0;
    }
    private static void Print(RuntimeSession runtime, EvaluationMetrics metrics, bool evaluation = false)
    {
        var state = runtime.Controller.Status;
        Console.WriteLine($"age={runtime.Snapshot().Age} training={state.Steps} revision={runtime.Revision} epsilon={(evaluation ? 0 : state.Epsilon):F3} replay={state.ReplayCount} eligible={state.EligibleReplayCount} updates={state.Updates} loss={state.Loss:F4} balance={metrics.MeanBalance:P1} needs=[{string.Join(", ", metrics.Drives.Select(p => $"{p.Key}:{p.Value.MeanSatisfaction:P1}"))}]");
    }
}
