using System.Globalization;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Brain;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    string command = args.FirstOrDefault() ?? "help";
    if (command == "help")
    {
        Console.WriteLine("ArtificialLife: train [--config configs/default.json] [--steps N] [--checkpoint artifacts/checkpoints/thermal]\nevaluate [--checkpoint PATH]");
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
            $"step={progress.Step,7} ε={progress.Epsilon:F3} reward={progress.Metrics.MeanReward:F3} loss={progress.Loss:F4} body={progress.Metrics.MeanBodyTemperature:F2}°C error={progress.Metrics.MeanAbsoluteError:F2}°C comfort={progress.Metrics.ComfortPercent:F1}% replay={progress.ReplaySize} elapsed={progress.Seconds:F1}s"));
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

static void Print(string label, EvaluationMetrics metrics) => Console.WriteLine($"{label,-10} MAE={metrics.MeanAbsoluteError:F3}°C comfort={metrics.ComfortPercent:F2}% reward={metrics.MeanReward:F4}");
