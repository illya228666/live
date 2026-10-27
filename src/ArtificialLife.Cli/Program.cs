using System.Globalization;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    string command = args.FirstOrDefault() ?? "help";
    if (command == "scenario-describe") return ScenarioSuitability.Command(args.Skip(1).ToArray(), false);
    if (command == "scenario-validate-review") return ScenarioSuitability.Command(args.Skip(1).ToArray(), true);
    if (command == "scenario-audit") return TemperatureScenarioAudit.Command(args.Skip(1).ToArray());
    if (command == "ecology-audit") return EcologyScenarioAudit.Command(args.Skip(1).ToArray());
    if (command == "expansion-audit") return ExpansionScenarioAudit.Command(args.Skip(1).ToArray());
    if (command == "signal-audit") return SignalScenarioAudit.Command(args.Skip(1).ToArray());
    if (command == "experiment") return ExperimentRunner.Run(args.Skip(1).ToArray());
    if (command == "experiment-prepare") return ExperimentRunner.Prepare(args.Skip(1).ToArray());
    if (command == "experiment-worker") return ExperimentRunner.Worker(args.Skip(1).ToArray());
    if (command == "compare") return ExperimentComparison.Run(args.Skip(1).ToArray());
    if (command == "live") return RuntimeCommands.Live(args.Skip(1).ToArray());
    if (command == "continual") return ContinualExperiment.Run(args.Skip(1).ToArray());
    if (command == "evaluate" && args.Contains("--checkpoint"))
    {
        int checkpointIndex = Array.IndexOf(args, "--checkpoint");
        if (checkpointIndex + 1 < args.Length && File.Exists(Path.Combine(args[checkpointIndex + 1], "current")))
            return RuntimeCommands.EvaluatePolicy(args.Skip(1).ToArray());
    }
    if (command == "help")
    {
        Console.WriteLine("experiment --config PATH --output DIR [--variant MOTIVATION--LEARNER]\ncompare --inputs DIR1,DIR2 --output DIR");
        Console.WriteLine("experiment options: --parallel N --threads-per-worker N --max-minutes N --trial-timeout-minutes N --stop-file PATH\nexperiment-prepare --config PATH --output NEW_DIR");
        Console.WriteLine("scenario-audit --config PATH --protocol PATH --output NEW_DIR\nscenario-describe --config PATH [--output PATH]\nscenario-validate-review --config PATH --review PATH");
        Console.WriteLine("ecology-audit --config PATH --protocol PATH --output NEW_DIR");
        Console.WriteLine("expansion-audit --config PATH --output NEW_DIR");
        Console.WriteLine("signal-audit --config PATH --output NEW_DIR");
        Console.WriteLine("ArtificialLife: live [--config configs/runtime.json] [--steps N] [--checkpoint PATH] [--trajectory PATH]\ncontinual [--config configs/continual.json] [--output PATH] [--pilot true]\ntrain [--config configs/default.json] [--steps N] [--checkpoint PATH]\nevaluate [--checkpoint PATH]");
        return 0;
    }
    var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 1; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !arguments.TryAdd(args[index], args[index + 1]))
        {
            throw new ArgumentException("Options must be unique --key value pairs.");
        }
    }
    if (command is not ("train" or "evaluate") || arguments.Keys.Any(key => key is not ("--config" or "--steps" or "--checkpoint")))
    {
        throw new ArgumentException("Unknown command or option. Use help.");
    }
    if (command == "evaluate" && arguments.ContainsKey("--config"))
    {
        throw new ArgumentException("Evaluation uses the checkpoint configuration; --config is only valid for training.");
    }
    string checkpointPath = Path.GetFullPath(arguments.GetValueOrDefault("--checkpoint", "artifacts/checkpoints/thermal"));
    ExperimentOptions options = command == "evaluate" ? Checkpoint.Read(checkpointPath).Options : ExperimentOptions.Load(arguments.GetValueOrDefault("--config", "configs/default.json"));
    if (arguments.TryGetValue("--steps", out string? steps))
    {
        if (command != "train")
        {
            throw new ArgumentException("--steps is only valid for training.");
        }
        options = options with { Learning = options.Learning with { TrainingSteps = int.Parse(steps, CultureInfo.InvariantCulture) } };
    }
    var session = new SimulationSession(options);
    using var brain = new DqnBrain(options.Network, options.Learning);
    int[] seeds = options.Evaluation.Seeds;
    EvaluationMetrics random = Evaluator.Run(session, null, seeds);
    EvaluationMetrics untrained = Evaluator.Run(session, brain, seeds);
    double seconds = 0;
    if (command == "train")
    {
        Print("Random", random);
        Print("Untrained", untrained);
        seconds = Trainer.Run(session, brain, progress => Console.WriteLine(
            $"step={progress.Step,7} epsilon={progress.Epsilon:F3} balance={progress.Metrics.MeanBalance:P1} reward={progress.Metrics.MeanReward:F3} needs=[{Needs(progress.Metrics)}] loss={progress.Loss:F4} replay={progress.ReplaySize} elapsed={progress.Seconds:F1}s"));
        Checkpoint.Save(checkpointPath, session, brain);
    }
    else
    {
        Checkpoint.Load(checkpointPath, session, brain);
    }
    EvaluationMetrics trained = Evaluator.Run(session, brain, seeds);
    Print("Trained", trained);
    var perSeed = new List<SeedEvaluation>();
    using var fresh = new DqnBrain(options.Network, options.Learning);
    foreach (int seed in seeds)
    {
        perSeed.Add(new SeedEvaluation(seed, Evaluator.Run(session, null, [seed]), Evaluator.Run(session, fresh, [seed]), Evaluator.Run(session, brain, [seed])));
    }
    int completedTrainingSteps = command == "evaluate" ? Checkpoint.Read(checkpointPath).TrainingSteps : options.Learning.TrainingSteps;
    var report = new EvaluationReport(DateTimeOffset.UtcNow, completedTrainingSteps, seconds, random, untrained, trained, perSeed.ToArray());
    Directory.CreateDirectory(checkpointPath);
    File.WriteAllText(Path.Combine(checkpointPath, command == "train" ? "evaluation.json" : "reevaluation.json"), JsonSerializer.Serialize(report, ExperimentOptions.Json));
    Console.WriteLine($"Checkpoint: {checkpointPath}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ArtificialLife: {exception.Message}");
    return 1;
}

static void Print(string label, EvaluationMetrics metrics) => Console.WriteLine($"{label,-10} balance={metrics.MeanBalance:P1} reward={metrics.MeanReward:F4} needs=[{Needs(metrics)}]");

static string Needs(EvaluationMetrics metrics) => string.Join(", ", metrics.Drives.Select(pair => $"{pair.Key}={pair.Value.MeanSatisfaction:P1}"));
