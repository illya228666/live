using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record EvaluationEpisode(long AtStep, string Phase, string Scenario, string Kind, int Seed, int Episode,
    int WorldSeed, int Steps, Dictionary<string, BehaviorMetric> Metrics, double MeanReward, string ActionHash);
public sealed record LearningPoint(long Step, string Phase, long Age, long Revision, ControllerStatus Controller,
    double MeanReward, double TrainingSeconds, double EvaluationSeconds);
public sealed record ExperimentRunResult(int SchemaVersion, string Variant, int Seed, int WorldSeed, string ConfigurationHash,
    string ProtocolHash, string ExecutionHash, string Status, string? Error, string BehaviorOutcome, long TrainingSteps,
    double TrainingSeconds, double EvaluationSeconds, string TrainingActionHash, EvaluationEpisode[] Evaluations, LearningPoint[] Learning)
{
    public string TreatmentHash { get; init; } = "";
}
public sealed record ExperimentSuiteResult(int SchemaVersion, string Status, int ExpectedRuns, string[] Runs, string[] Errors);

/// <summary>Finite headless trials over the live runtime; never polls the user's activation inbox.</summary>
public static class ExperimentRunner
{
    public static int Run(string[] args)
    {
        var arguments = RuntimeCommands.Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--config" or "--output" or "--variant" or "--parallel" or "--max-minutes" or "--stop-file" or "--trial-timeout-minutes" or "--threads-per-worker"))) throw new ArgumentException("Unknown experiment option.");
        int parallel = int.Parse(arguments.GetValueOrDefault("--parallel", "1"));
        int threads = int.Parse(arguments.GetValueOrDefault("--threads-per-worker", "1"));
        double minutes = double.Parse(arguments.GetValueOrDefault("--max-minutes", "Infinity"), System.Globalization.CultureInfo.InvariantCulture);
        double timeout = double.Parse(arguments.GetValueOrDefault("--trial-timeout-minutes", "Infinity"), System.Globalization.CultureInfo.InvariantCulture);
        if (parallel < 1 || threads < 1 || !double.IsFinite(parallel * (double)threads) || double.IsNaN(minutes) || minutes <= 0 || double.IsNaN(timeout) || timeout <= 0)
            throw new ArgumentException("Parallelism, threads and budgets must be positive.");
        string config = Path.GetFullPath(arguments.GetValueOrDefault("--config", "configs/experiments-pilot.json"));
        string output = Path.GetFullPath(arguments.GetValueOrDefault("--output", "artifacts/experiments/pilot"));
        var definition = ExperimentDefinition.Load(config);
        var jobs = definition.Jobs(arguments.GetValueOrDefault("--variant")).ToArray();
        if (jobs.Length == 0) throw new ArgumentException("No experiment variant matches the filter.");
        Directory.CreateDirectory(output);
        using var suiteLock = new FileStream(Path.Combine(output, "suite.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var clock = Stopwatch.StartNew();
        int stopState = 0;
        string? stopFile = arguments.GetValueOrDefault("--stop-file");
        bool StopRequested()
        {
            if (stopFile is not null && File.Exists(stopFile))
            {
                try
                {
                    if (File.ReadAllText(stopFile).Trim().Equals("force", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref stopState, 2);
                    else Interlocked.CompareExchange(ref stopState, 1, 0);
                }
                catch (IOException) { /* an atomically replaced/removed request is retried on the next poll */ }
            }
            return Volatile.Read(ref stopState) > 0 || clock.Elapsed.TotalMinutes >= minutes;
        }
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Interlocked.Increment(ref stopState); };
        Console.CancelKeyPress += cancel;
        string auditOutput = File.Exists(Path.Combine(output, "provenance.json")) ? Path.Combine(output, "attempts", Guid.NewGuid().ToString("N")) : output;
        Directory.CreateDirectory(auditOutput);
        File.Copy(config, Path.Combine(auditOutput, "source.json"), true);
        ExperimentJson.Atomic(Path.Combine(auditOutput, "resolved.json"), definition);
        string executionHash = Provenance(auditOutput, threads);
        var errors = new List<string>();
        var runs = new List<string>();
        var prepared = new List<(ExperimentJob Job, string Directory, string Relative)>();
        foreach (var original in jobs)
        {
            string directory = Path.Combine(output, original.Variant, $"seed-{original.Seed}-world-{original.WorldSeed}");
            Directory.CreateDirectory(directory);
            string relative = Path.GetRelativePath(output, directory);
            runs.Add(relative);
            try
            {
                string packageRoot = Path.Combine(output, "packages");
                // Snapshot before a child starts; the trial only reads these immutable copies.
                var archived = ArchivePackages(original, packageRoot);
                string protocol = ProtocolHash(original);
                var job = archived with
                {
                    TreatmentHash = ExperimentJson.Hash(new
                    {
                        Controller = ControllerRegistry.Resolve(original.Runtime with { ControllerSeed = 0 }).Controller,
                        Motivation = original.Definition.Motivations.Single(m => original.Variant.StartsWith(m.Id + "--", StringComparison.Ordinal))
                    }),
                    ConfigurationHash = ExperimentJson.Hash(new { original.Variant, original.Seed, original.WorldSeed, Runtime = PortableOptions(original.Runtime),
                        Phases = original.Definition.Phases.Select(p => p with { EvaluationRuntime = p.EvaluationRuntime is null ? null : PortableOptions(p.EvaluationRuntime) }),
                        original.Definition.Behavior, original.Definition.EvaluationSeeds,
                        original.Definition.EpisodesPerSeed, original.Definition.EvaluationSteps, original.Definition.EvaluateEvery,
                        Packages = PackageEvidence(original) }),
                    ProtocolHash = protocol, ExecutionHash = executionHash
                };
                string resultPath = Path.Combine(directory, "result.json");
                if (File.Exists(resultPath))
                {
                    var previous = ExperimentJson.Read<ExperimentRunResult>(resultPath);
                    if (previous.ConfigurationHash != job.ConfigurationHash || previous.ExecutionHash != job.ExecutionHash)
                        throw new InvalidDataException($"Output already contains a different configuration or build: {directory}. Use a new output directory.");
                    if (previous.Status == "completed") { Console.WriteLine($"SKIP {relative}"); continue; }
                }
                ExperimentJson.Atomic(Path.Combine(directory, "job.json"), job);
                prepared.Add((job, directory, relative));
            }
            catch (Exception ex)
            {
                errors.Add($"{relative}: {ex.Message}");
                File.WriteAllText(Path.Combine(directory, "preparation-error.txt"), ex.ToString());
            }
            finally
            {
                ExperimentJson.Atomic(Path.Combine(output, "suite.json"), new ExperimentSuiteResult(1, "running", jobs.Length, runs.ToArray(), errors.ToArray()));
            }
        }
        object gate = new();
        int deferred = 0;
        try
        {
            Parallel.ForEach(prepared, new ParallelOptions { MaxDegreeOfParallelism = parallel }, item =>
            {
                if (StopRequested()) { Interlocked.Increment(ref deferred); return; }
                // An orphaned worker after a killed coordinator owns its directory until it finishes.
                while (TrialBusy(item.Directory))
                {
                    if (StopRequested()) { Interlocked.Increment(ref deferred); return; }
                    Thread.Sleep(100);
                }
                string resultPath = Path.Combine(item.Directory, "result.json");
                if (File.Exists(resultPath) && ExperimentJson.Read<ExperimentRunResult>(resultPath).Status == "completed") return;
                Console.WriteLine($"EXPERIMENT {item.Relative}");
                int exit;
                try { exit = LaunchWorker(Path.Combine(item.Directory, "job.json"), item.Directory, threads, timeout, () => { StopRequested(); return Volatile.Read(ref stopState) >= 2; }); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(item.Directory, "worker.log"), ex.ToString()); exit = -1; }
                lock (gate)
                {
                    if (exit == 130) deferred++;
                    else if (exit != 0 || !File.Exists(resultPath) || ExperimentJson.Read<ExperimentRunResult>(resultPath).Status != "completed")
                        errors.Add($"{item.Relative}: child exit {exit}; see worker.log and result.json.");
                    ExperimentJson.Atomic(Path.Combine(output, "suite.json"), new ExperimentSuiteResult(1, "running", jobs.Length, runs.ToArray(), errors.ToArray()));
                }
            });
        }
        finally { Console.CancelKeyPress -= cancel; }
        string status = errors.Count > 0 ? "failed" : deferred > 0 ? "paused" : "completed";
        ExperimentJson.Atomic(Path.Combine(output, "suite.json"), new ExperimentSuiteResult(1, status, jobs.Length, runs.ToArray(), errors.ToArray()));
        Console.WriteLine($"EXPERIMENT status={status} runs={runs.Count} errors={errors.Count} output={output}");
        return errors.Count > 0 ? 1 : deferred > 0 ? 130 : 0;
    }

    private static RuntimeOptions PortableOptions(RuntimeOptions options) => options with { PackageRoot = "", Inbox = "" };

    public static int Prepare(string[] args)
    {
        var arguments = RuntimeCommands.Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--config" or "--output"))) throw new ArgumentException("Unknown prepare option.");
        string output = Path.GetFullPath(arguments["--output"]);
        if (Directory.Exists(output)) throw new IOException("Preparation output must be a new directory.");
        var definition = ExperimentDefinition.Load(arguments["--config"]);
        var jobs = definition.Jobs().ToArray();
        Directory.CreateDirectory(output);
        foreach (var job in jobs) _ = ArchivePackages(job, Path.Combine(output, "packages"));
        var archived = ArchivePackages(jobs[0], Path.Combine(output, "packages")).Definition;
        ExperimentJson.Atomic(Path.Combine(output, "config.json"), archived);
        ExperimentJson.Atomic(Path.Combine(output, "plan.json"), new
        {
            SchemaVersion = 1, ExpectedRuns = jobs.Length, Protocols = jobs.Select(ProtocolHash).Distinct().ToArray(),
            ConfigurationHash = ExperimentJson.Hash(archived), definition.TrainingSeeds, definition.EvaluationSeeds,
            TrainingStepsPerTrial = definition.Phases.Sum(p => (long)p.Steps), PreparedUtc = DateTimeOffset.UtcNow
        });
        return 0;
    }

    private static bool TrialBusy(string directory)
    {
        try { using var lease = new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    private static int LaunchWorker(string job, string directory, int threads, double timeoutMinutes, Func<bool> force)
    {
        string entry = Assembly.GetEntryAssembly()!.Location;
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(entry);
        start.ArgumentList.Add("experiment-worker"); start.ArgumentList.Add("--job"); start.ArgumentList.Add(job);
        start.ArgumentList.Add("--output"); start.ArgumentList.Add(directory);
        foreach (string name in new[] { "AL_COMPUTE_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS", "DOTNET_PROCESSOR_COUNT" }) start.Environment[name] = threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start experiment worker.");
        ExperimentJson.Atomic(Path.Combine(directory, "process.json"), new { child.Id, StartedUtc = child.StartTime.ToUniversalTime(), Threads = threads });
        var timer = Stopwatch.StartNew();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        int? stopped = null;
        while (!child.WaitForExit(100))
            if (force() || timer.Elapsed.TotalMinutes >= timeoutMinutes)
            {
                stopped = force() ? 130 : 124;
                child.Kill(entireProcessTree: true); child.WaitForExit(); break;
            }
        File.WriteAllText(Path.Combine(directory, "worker.log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        return stopped ?? child.ExitCode;
    }

    public static int Worker(string[] args)
    {
        var arguments = RuntimeCommands.Arguments(args);
        return Execute(ExperimentJson.Read<ExperimentJob>(arguments["--job"]), arguments["--output"]).Status == "completed" ? 0 : 1;
    }

    public static ExperimentRunResult Execute(ExperimentJob job, string directory)
    {
        Directory.CreateDirectory(directory);
        using var workerLock = new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var evaluations = new List<EvaluationEpisode>();
        var points = new List<LearningPoint>();
        var scenarios = new List<(string Id, RuntimeOptions Options)>();
        var trainTime = new Stopwatch(); var evaluationTime = new Stopwatch();
        using var actionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var events = new StreamWriter(Path.Combine(directory, "events.jsonl")) { AutoFlush = true };
        long steps = 0;
        string status = "running", outcome = "not-assessed";
        string? error = null;
        ExperimentRunResult Result(string digest) => new(1, job.Variant, job.Seed, job.WorldSeed, job.ConfigurationHash, job.ProtocolHash,
            job.ExecutionHash, status, error, outcome, steps, trainTime.Elapsed.TotalSeconds, evaluationTime.Elapsed.TotalSeconds,
            digest, evaluations.ToArray(), points.ToArray()) { TreatmentHash = job.TreatmentHash };
        void WriteEvent<T>(T value) => events.WriteLine(JsonSerializer.Serialize(value, ExperimentJson.Lines));
        ExperimentJson.Atomic(Path.Combine(directory, "result.json"), Result(""));
        try
        {
            job.Definition.Validate();
            // Construct the untrained baseline first, so the training controller initializes its own RNG last.
            using var untrained = ControllerRegistry.Create(job.Runtime);
            using var runtime = new RuntimeSession(job.Runtime, ControllerRegistry.Create(job.Runtime));
            runtime.TrajectorySink = transition => HashAction(actionHash, transition.Action);
            foreach (var phase in job.Definition.Phases)
            {
                if (phase.Activate.Length > 0)
                {
                    var activation = runtime.Activate(new("experiment-" + phase.Id, phase.Activate));
                    WriteEvent(new { Kind = "activation", AtStep = steps, Result = activation });
                    if (!activation.Accepted) throw new InvalidOperationException(activation.Message);
                }
                var active = job.Runtime with
                {
                    Packages = runtime.Packages.Select(p => new PackageReference(p.Id, p.Version)).ToArray(),
                    Settings = runtime.Packages.ToDictionary(p => p.Id, p => p.Settings.Clone(), StringComparer.Ordinal)
                };
                var scenario = phase.EvaluationRuntime ?? active;
                scenarios.Add((phase.Id, scenario));
                untrained.SchemaChanged(runtime.ObservationKeys, runtime.ActionKeys);
                untrained.ConditionsChanged(runtime.Revision);
                RuntimeCheckpoint.Save(Path.Combine(directory, "policies", phase.Id + "-entry"), runtime);
                void Evaluate(string kind, IAgentController source, IEnumerable<(string Id, RuntimeOptions Options)> worlds, bool random = false)
                {
                    evaluationTime.Start();
                    try
                    {
                        foreach (var world in worlds)
                        foreach (int seed in job.Definition.EvaluationSeeds)
                        for (int episode = 0; episode < job.Definition.EpisodesPerSeed; episode++)
                        {
                            int worldSeed = checked(seed + episode * 10000);
                            var result = EvaluateEpisode(runtime, source, world.Options, job.Definition.Behavior,
                                job.Definition.EvaluationSteps, steps, phase.Id, world.Id, kind, seed, episode, worldSeed, random);
                            evaluations.Add(result); WriteEvent(new { Kind = "evaluation", Result = result });
                        }
                    }
                    finally { evaluationTime.Stop(); }
                }
                Evaluate("random", runtime.Controller, [scenarios[^1]], random: true);
                Evaluate("untrained", untrained, [scenarios[^1]]);
                Evaluate(steps == 0 ? "initial" : "frozen", runtime.Controller, [scenarios[^1]]);
                void MeasurePoint(double reward)
                {
                    Evaluate("policy", runtime.Controller, scenarios);
                    var point = new LearningPoint(steps, phase.Id, runtime.Snapshot().Age, runtime.Revision, runtime.Controller.Status,
                        reward, trainTime.Elapsed.TotalSeconds, evaluationTime.Elapsed.TotalSeconds);
                    points.Add(point); WriteEvent(new { Kind = "learning", Result = point });
                    ExperimentJson.Atomic(Path.Combine(directory, "result.json"), Result(""));
                    Console.WriteLine($"POINT variant={job.Variant} seed={job.Seed} phase={phase.Id} steps={steps} updates={point.Controller.Updates}");
                }
                MeasurePoint(0);
                double rewardSum = 0; int interval = 0;
                for (int i = 1; i <= phase.Steps; i++)
                {
                    trainTime.Start();
                    var sample = runtime.AdvanceOne();
                    trainTime.Stop();
                    steps++; rewardSum += sample.Reward; interval++;
                    if (i % job.Definition.EvaluateEvery == 0 || i == phase.Steps)
                    {
                        MeasurePoint(rewardSum / interval); rewardSum = 0; interval = 0;
                    }
                }
                RuntimeCheckpoint.Save(Path.Combine(directory, "policies", phase.Id + "-final"), runtime);
            }
            long expected = job.Definition.Phases.Sum(p => (long)p.Steps);
            if (steps != expected || runtime.Snapshot().Age != expected || runtime.Controller.Status.Steps != expected)
                throw new InvalidDataException("Incomplete training life or controller step counters.");
            outcome = BehaviorEvaluators.Assess(job.Definition.Behavior.Criteria, (scenario, metric) =>
            {
                var final = evaluations.Where(e => e.Kind == "policy" && e.AtStep == steps && e.Scenario == scenario).ToArray();
                return Mean(final, metric);
            });
            status = "completed";
        }
        catch (Exception ex) { status = "failed"; error = ex.ToString(); outcome = "not-assessed"; trainTime.Stop(); evaluationTime.Stop(); }
        var completed = Result(Convert.ToHexString(actionHash.GetHashAndReset()));
        ExperimentJson.Atomic(Path.Combine(directory, "result.json"), completed);
        return completed;
    }

    public static EvaluationEpisode EvaluateEpisode(RuntimeSession training, IAgentController source, RuntimeOptions options, BehaviorProfile profile,
        int steps, long atStep, string phase, string scenario, string kind, int seed, int episode, int worldSeed, bool random = false)
    {
        var controller = random ? new RandomEvaluationController(source.Limits, worldSeed) : source.CreateInferenceController();
        using var runtime = new RuntimeSession(options with { WorldSeed = worldSeed }, controller,
            observationKeys: training.ObservationKeys, actionKeys: training.ActionKeys);
        var evaluators = BehaviorEvaluators.Create(profile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        runtime.TrajectorySink = transition => HashAction(hash, transition.Action);
        double reward = 0;
        for (int i = 0; i < steps; i++)
        {
            var sample = runtime.AdvanceOne(false);
            foreach (var evaluator in evaluators) evaluator.Observe(runtime.Snapshot(), sample.Homeostasis);
            reward += sample.Reward;
        }
        return new(atStep, phase, scenario, kind, seed, episode, worldSeed, steps, BehaviorEvaluators.Complete(evaluators),
            reward / steps, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public static double? Mean(IEnumerable<EvaluationEpisode> episodes, string metric)
    {
        var values = episodes.Select(e => e.Metrics.GetValueOrDefault(metric)).ToArray();
        if (values.Length == 0 || values.Any(m => m?.Value is null || m.Samples == 0)) return null;
        return values.Average(m => m!.Value!.Value);
    }
    private static void HashAction(IncrementalHash hash, ActionCandidate action) =>
        hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(action, ExperimentJson.Lines) + "\n"));

    private sealed class RandomEvaluationController(ControllerLimits limits, int seed) : IAgentController
    {
        private readonly Random random = new(seed);
        public ControllerLimits Limits => limits;
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => actions[random.Next(actions.Length)];
        public void Receive(AgentTransition transition) => throw new InvalidOperationException("Evaluation only.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static IEnumerable<(RuntimeOptions Options, PackageReference Reference)> References(ExperimentJob job)
    {
        foreach (var reference in job.Runtime.Packages.Concat(job.Definition.Phases.SelectMany(p => p.Activate))) yield return (job.Runtime, reference);
        foreach (var options in job.Definition.Phases.Where(p => p.EvaluationRuntime is not null).Select(p => p.EvaluationRuntime!))
            foreach (var reference in options.Packages) yield return (options, reference);
    }
    private static object[] PackageEvidence(ExperimentJob job) => References(job).Select(p => new
    {
        p.Reference, Manifest = new PackageCatalog(p.Options.PackageRoot).Read(p.Reference)
    }).Cast<object>().ToArray();

    public static string ProtocolHash(ExperimentJob job)
    {
        string motivation = job.Definition.Motivations.Single(m => job.Variant.StartsWith(m.Id + "--", StringComparison.Ordinal)).Package.Id;
        object EnvironmentOf(RuntimeOptions options) => new
        {
            options.World, Packages = options.Packages.Where(p => p.Id != motivation && p.Id != job.Definition.MotivationSlot).ToArray(),
            Settings = options.Settings.Where(p => p.Key != motivation && p.Key != job.Definition.MotivationSlot).ToDictionary(p => p.Key, p => p.Value)
        };
        var packages = References(job).Where(p => p.Reference.Id != motivation && p.Reference.Id != job.Definition.MotivationSlot)
            .Select(p => new PackageCatalog(p.Options.PackageRoot).Read(p.Reference))
            .Select(m => new { m.Id, m.Version, m.LogicHash, m.LogicFiles, m.Settings, m.Schemas, m.Requires, m.Provides, m.Dependencies }).ToArray();
        return ExperimentJson.Hash(new
        {
            SchemaVersion = 1, Environment = EnvironmentOf(job.Runtime), job.Definition.TrainingSeeds, job.Definition.WorldSeeds,
            job.Definition.EvaluationSeeds, job.Definition.EpisodesPerSeed, job.Definition.EvaluationSteps, job.Definition.EvaluateEvery,
            Phases = job.Definition.Phases.Select(p => new { p.Id, p.Steps, p.Activate, Evaluation = p.EvaluationRuntime is null ? null : EnvironmentOf(p.EvaluationRuntime) }),
            job.Definition.Behavior, Packages = packages
        });
    }

    private static ExperimentJob ArchivePackages(ExperimentJob job, string packageRoot)
    {
        foreach (var (options, reference) in References(job))
        {
            var catalog = new PackageCatalog(options.PackageRoot);
            var manifest = catalog.Read(reference);
            string source = catalog.DirectoryFor(reference), target = new PackageCatalog(packageRoot).DirectoryFor(reference);
            if (Directory.Exists(target))
            {
                _ = new PackageCatalog(packageRoot).Read(reference);
                if (PackageCatalog.ManifestHash(source) != PackageCatalog.ManifestHash(target)) throw new InvalidDataException("Package identity collision in suite archive.");
                continue;
            }
            string stagingRoot = Path.Combine(packageRoot, ".staging", Guid.NewGuid().ToString("N"));
            var stagingCatalog = new PackageCatalog(stagingRoot);
            string staged = stagingCatalog.DirectoryFor(reference);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(staged, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            _ = stagingCatalog.Read(new(manifest.Id, manifest.Version));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staged, target); // publish a verified complete package atomically on this volume
            Directory.Delete(Path.GetDirectoryName(staged)!); Directory.Delete(stagingRoot);
        }
        return job with
        {
            Runtime = job.Runtime with { PackageRoot = packageRoot },
            Definition = job.Definition with
            {
                Runtime = job.Definition.Runtime with { PackageRoot = packageRoot },
                Phases = job.Definition.Phases.Select(p => p with
                {
                    EvaluationRuntime = p.EvaluationRuntime is null ? null : p.EvaluationRuntime with { PackageRoot = packageRoot }
                }).ToArray()
            }
        };
    }

    private static string Provenance(string output, int threads)
    {
        static string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var result = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
            return process.ExitCode == 0 ? result.TrimEnd() : "unavailable";
        }
        File.WriteAllText(Path.Combine(output, "source.patch"), Git("diff", "HEAD", "--binary"));
        File.WriteAllText(Path.Combine(output, "git-status.txt"), Git("status", "--short"));
        var assemblies = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .ToDictionary(p => Path.GetRelativePath(AppContext.BaseDirectory, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var untracked = Git("ls-files", "--others", "--exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.TrimEnd('\r')).Where(File.Exists).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        foreach (string relative in untracked.Keys)
        {
            string destination = PackageCatalog.FileWithin(Path.Combine(output, "untracked-source"), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(relative, destination, true);
        }
        var execution = new
        {
            IdentityVersion = 2, WorkerThreads = threads, Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Assemblies = assemblies,
            RuntimeMetadata = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.json").Order(StringComparer.Ordinal)
                .ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
        };
        string executionHash = ExperimentJson.Hash(execution);
        var value = new
        {
            ExecutionHash = executionHash, Execution = execution,
            GitRevision = Git("rev-parse", "HEAD"), SourceDiffHash = ExperimentJson.Hash(Git("diff", "HEAD", "--binary")),
            Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), Runtime = Environment.Version.ToString(),
            Torch = typeof(TorchSharp.torch).Assembly.GetName().Version?.ToString(), Assemblies = assemblies, UntrackedSource = untracked,
            RuntimeMetadata = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.json").Order(StringComparer.Ordinal)
                .ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
        };
        ExperimentJson.Atomic(Path.Combine(output, "provenance.json"), value);
        return executionHash;
    }
}
