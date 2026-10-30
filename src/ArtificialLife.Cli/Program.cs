using System.Globalization;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Hosting;
using ArtificialLife.Brain;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var observationHost = args.Contains("verify") ? null : new ObservationHost(args.FirstOrDefault() is "experiment" or "experiment-worker" or "continual" || args.FirstOrDefault()?.Contains("audit") == true);
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
    if (command == "world") return WorldCommands.Run(args.Skip(1).ToArray());
    if (command == "live") return WorldCommands.Run(new[] { "run", "--mode", "learning", "--directory", Path.Combine("artifacts", "worlds", "live-" + Guid.NewGuid().ToString("N")) }.Concat(args.Skip(1)).ToArray());
    if (command == "continual") return ContinualExperiment.Run(args.Skip(1).ToArray());
    if (command == "evaluate") return RuntimeCommands.EvaluatePolicy(args.Skip(1).ToArray());
    if (command == "help")
    {
        Console.WriteLine("world run --mode world-only|learning|inference|evaluation --directory DIR [--config CONFIG | --policy CHECKPOINT | --restore WORLD_CHECKPOINT]\nworld list\nworld status|snapshot|pause|resume|speed|checkpoint|update|rollback|stop --session ID\nworld help");
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
    var options = RuntimeCommands.ReadOptions(arguments.GetValueOrDefault("--config", "configs/default.json"));
    if (arguments.TryGetValue("--steps", out string? steps))
    {
        if (command != "train")
        {
            throw new ArgumentException("--steps is only valid for training.");
        }
        options = options with { Learning = options.Learning with { TrainingSteps = int.Parse(steps, CultureInfo.InvariantCulture) } };
    }
    return RuntimeTraining.Run(options, checkpointPath);

}
catch (Exception exception)
{
    Console.Error.WriteLine($"ArtificialLife: {exception.Message}");
    return 1;
}
