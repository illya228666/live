using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Hosting;

public sealed record WorldCommand(string Id, string Operation, string[] Sources, string? Target);
public sealed record WorldCommandResult(string Id, bool Accepted, string Message, long Age, long Revision, string? Artifact = null);
public sealed record WorldObservation(RuntimeFrame Frame, RuntimeMode Mode, ModuleManifest[] Packages, string PackageRoot,
    string? Operation, string? Fault, string[] Checkpoints, double StepsPerSecond, double StepMilliseconds,
    IReadOnlyDictionary<string, double> Systems, IReadOnlyDictionary<string, double> Metrics);
public sealed record WorldRecovery(string Current, string? Previous = null, string? Pending = null, string[]? Rejected = null, string? Candidate = null);

/// <summary>File control plane and process supervision around the single existing RuntimeSession.</summary>
public static class WorldCommands
{
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), PackageCatalog.Json) ?? throw new InvalidDataException("Empty " + path);
    private static string State(string root) => Path.Combine(root, "recovery.json");
    private static void Log(string root, string kind, object detail)
    {
        string file = Path.Combine(root, "events.jsonl");
        if (File.Exists(file) && new FileInfo(file).Length > 10_000_000)
        {
            for (int i = 3; i >= 1; i--) if (File.Exists(file + "." + i)) File.Move(file + "." + i, file + "." + (i + 1), true);
            File.Move(file, file + ".1", true);
        }
        File.AppendAllText(file, JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Kind = kind, Detail = detail }) + "\n");
    }
    public static int Run(string[] args)
    {
        string operation = args.FirstOrDefault() ?? "help";
        if (operation == "help") { Console.WriteLine("world run --directory DIR [--config configs/world-only.json] [--steps N]\nworld status|snapshot|checkpoint|rollback|screenshot|stop --directory DIR\nworld update --directory DIR --sources PATH1;PATH2 [--id ID]\nworld screenshot --directory DIR [--target PNG]\nrun options: --checkpoint-seconds 30 --heartbeat-seconds 60 --validation-seconds 60 --probation-steps 1000"); return 0; }
        var a = RuntimeCommands.Arguments(args.Skip(1).ToArray());
        string[] allowed = ["--speed", "--trajectory", "--mode", "--policy", "--restore", "--session", "--value", "--directory", "--config", "--steps", "--checkpoint-seconds", "--heartbeat-seconds", "--validation-seconds", "--probation-steps", "--id", "--sources", "--target", "--checkpoint", "--sources-file", "--output"];
        if (a.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown world option.");
        if (operation == "list") { using var http = new HttpClient(); var available = new List<ArtificialLife.Observation.SessionDescriptor>(); foreach (var host in ObservationHost.Discover()) try { available.AddRange(JsonSerializer.Deserialize<ArtificialLife.Observation.SessionDescriptor[]>(http.GetStringAsync(host.Endpoint + "/sessions").GetAwaiter().GetResult(), ArtificialLife.Observation.ObservationProtocol.Json) ?? []); } catch (HttpRequestException) { } Console.WriteLine(JsonSerializer.Serialize(available, PackageCatalog.Json)); return 0; }
        if (a.ContainsKey("--session")) return SessionCommand(operation, a);
        string root = Path.GetFullPath(a.GetValueOrDefault("--directory", "artifacts/worlds/default"));
        Directory.CreateDirectory(root);
        if (operation is "run" or "worker") { string identity = Path.Combine(root, "session-id"); if (!File.Exists(identity)) { string temporary = identity + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(temporary, Guid.NewGuid().ToString("N")); try { File.Move(temporary, identity); } catch (IOException) when (File.Exists(identity)) { File.Delete(temporary); } } Environment.SetEnvironmentVariable("LIVE_WORLD_SESSION", File.ReadAllText(identity).Trim()); Environment.SetEnvironmentVariable("LIVE_RUN_ID", File.ReadAllText(identity).Trim()); }
        if (operation == "verify") return Verify(a);
        if (operation == "worker") return Worker(root, a);
        if (operation == "run") return Supervise(root, a);
        if (operation is "status" or "snapshot") { Console.WriteLine(File.ReadAllText(Path.Combine(root, "observation.json"))); return 0; }
        if (operation is not ("checkpoint" or "update" or "rollback" or "screenshot" or "stop")) throw new ArgumentException("Unknown world command.");
        string id = a.GetValueOrDefault("--id", Guid.NewGuid().ToString("N"));
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Invalid request id.");
        string result = Path.Combine(root, "commands", id + ".result.json");
        if (File.Exists(result)) { Console.WriteLine(File.ReadAllText(result)); return 0; }
        var command = new WorldCommand(id, operation, a.GetValueOrDefault("--sources", "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath).ToArray(), a.TryGetValue("--target", out var target) ? Path.GetFullPath(target) : null);
        WorldCheckpoint.Atomic(Path.Combine(root, "commands", id + ".ready.json"), command);
        Console.WriteLine(result);
        return 0;
    }
    private static int SessionCommand(string operation, Dictionary<string,string> arguments)
    {
        using var http = new HttpClient();
        foreach (var host in ObservationHost.Discover())
        {
            var sessions = JsonSerializer.Deserialize<ArtificialLife.Observation.SessionDescriptor[]>(http.GetStringAsync(host.Endpoint + "/sessions").GetAwaiter().GetResult(), ArtificialLife.Observation.ObservationProtocol.Json)!;
            var session = sessions.FirstOrDefault(s => s.SessionId == arguments["--session"]);
            if (session is null) continue;
            if (operation == "status") { Console.WriteLine(JsonSerializer.Serialize(session)); return 0; }
            if (operation == "screenshot")
            {
                string target = Path.GetFullPath(arguments.GetValueOrDefault("--target", Path.Combine("artifacts", "captures", Guid.NewGuid().ToString("N") + ".png")));
                var captured = Capture(session.SessionId, target);
                Console.WriteLine(JsonSerializer.Serialize(new { Artifact = target, Capture = captured })); return 0;
            }
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            socket.ConnectAsync(new Uri(host.Endpoint.Replace("http://", "ws://") + "/observe/" + session.SessionId + "?version=1"), timeout.Token).GetAwaiter().GetResult();
            if (operation == "snapshot")
            {
                var frameBuffer = new byte[16384]; using var frameData = new MemoryStream(); System.Net.WebSockets.WebSocketReceiveResult fragment;
                do { fragment = socket.ReceiveAsync(frameBuffer, timeout.Token).GetAwaiter().GetResult(); frameData.Write(frameBuffer, 0, fragment.Count); } while (!fragment.EndOfMessage);
                Console.WriteLine(System.Text.Encoding.UTF8.GetString(frameData.ToArray())); return 0;
            }
            string? value = arguments.GetValueOrDefault("--value") ?? (operation switch
            {
                "update" => arguments.GetValueOrDefault("--sources"),
                "speed" => arguments.GetValueOrDefault("--speed"),
                "checkpoint" => arguments.GetValueOrDefault("--checkpoint") ?? arguments.GetValueOrDefault("--target"),
                _ => null
            });
            var command = new ArtificialLife.Observation.ObservationCommand(arguments.GetValueOrDefault("--id", Guid.NewGuid().ToString("N")), session.Epoch, operation, value);
            socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(command, ArtificialLife.Observation.ObservationProtocol.Json), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
            var buffer = new byte[16384];
            while (true)
            {
                using var data = new MemoryStream(); System.Net.WebSockets.WebSocketReceiveResult part;
                do { part = socket.ReceiveAsync(buffer, timeout.Token).GetAwaiter().GetResult(); data.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(data.ToArray());
                if (json.RootElement.TryGetProperty("id", out var id) && id.GetString() == command.Id) { Console.WriteLine(json.RootElement); return json.RootElement.GetProperty("accepted").GetBoolean() ? 0 : 1; }
            }
        }
        throw new InvalidOperationException("Session not found.");
    }
    private static double Positive(Dictionary<string, string> a, string key, double fallback)
    {
        double value = a.TryGetValue(key, out var text) ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        return double.IsFinite(value) && value > 0 ? value : throw new ArgumentException(key + " must be positive.");
    }
    internal static Process StartCli(params string[] args)
    {
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (string argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Cannot start CLI worker.");
    }
    private static int Supervise(string root, Dictionary<string, string> a)
    {
        using var ownership = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        double timeout = Positive(a, "--heartbeat-seconds", 60);
        if (!File.Exists(State(root)))
        {
            var options = RuntimeCommands.ReadOptions(a.GetValueOrDefault("--config", a.GetValueOrDefault("--mode") == "learning" ? "configs/runtime.json" : "configs/world-only.json"));
            if (a.TryGetValue("--mode", out var mode)) options = options with { Mode = mode.ToLowerInvariant() switch { "world-only" => RuntimeMode.WorldOnly, "learning" => RuntimeMode.Learning, "inference" => RuntimeMode.Inference, "evaluation" => RuntimeMode.Evaluation, _ => throw new ArgumentException("Unknown runtime mode.") } };
            options = options with { Inbox = Path.Combine(root, "activation"), PackageRoot = Path.GetFullPath(options.PackageRoot) };
            using var initial = a.TryGetValue("--restore", out var restore) ? RuntimeSessionFactory.RestoreWorld(restore, options.Mode == RuntimeMode.WorldOnly ? null : ControllerRegistry.Create(options)) : a.TryGetValue("--policy", out var policy) ? RuntimeSessionFactory.LoadPolicy(policy, options.Mode) : RuntimeSessionFactory.Create(options);
            if (!initial.WorldOnly) RuntimeCheckpoint.Save(Path.Combine(root, "policy"), initial);
            string path = WorldCheckpoint.Save(Path.Combine(root, "checkpoints"), initial);
            WorldCheckpoint.Atomic(State(root), new WorldRecovery(path));
        }
        var interrupted = Read<WorldRecovery>(State(root));
        var savedStart = WorldCheckpoint.Read(interrupted.Current);
        if (savedStart.Options.Mode == RuntimeMode.Learning && savedStart.World.Age > 0) throw new InvalidOperationException("Learning optimizer/replay cannot be resumed from this checkpoint. Start a new directory with --policy for an explicit warm start.");
        if (interrupted.Pending is not null)
        {
            interrupted = interrupted with { Current = interrupted.Pending, Pending = null, Rejected = (interrupted.Rejected ?? []).Append(interrupted.Candidate ?? "unknown").Distinct().ToArray(), Candidate = null };
            WorldCheckpoint.Atomic(State(root), interrupted); Log(root, "interrupted-update-recovery", interrupted);
        }
        int failures = 0;
        string? failedGeneration = null;
        bool stop = false;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop = true; };
        Console.CancelKeyPress += cancel;
        try
        {
            while (true)
            {
                string heartbeat = Path.Combine(root, "heartbeat.json");
                File.Delete(heartbeat);
                var arguments = new List<string> { "world", "worker", "--directory", root };
                foreach (var key in new[] { "--steps", "--checkpoint-seconds", "--validation-seconds", "--probation-steps", "--checkpoint", "--trajectory", "--speed" })
                    if (a.TryGetValue(key, out var value)) { arguments.Add(key); arguments.Add(value); }
                using var child = StartCli(arguments.ToArray());
                var startup = Stopwatch.StartNew();
                while (!child.WaitForExit(100))
                {
                    if (stop)
                    {
                        string stopId = "supervisor-stop-" + Guid.NewGuid().ToString("N");
                        WorldCheckpoint.Atomic(Path.Combine(root, "commands", stopId + ".ready.json"), new WorldCommand(stopId, "stop", [], null));
                        if (child.WaitForExit(5000)) return child.ExitCode;
                        child.Kill(true); child.WaitForExit(); return 0;
                    }
                    double silence = File.Exists(heartbeat) ? (DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat)).TotalSeconds : startup.Elapsed.TotalSeconds;
                    if (silence > timeout) { child.Kill(true); child.WaitForExit(); Log(root, "watchdog", new { Silence = silence }); break; }
                }
                if (child.ExitCode == 0) return 0;
                if (WorldCheckpoint.Read(Read<WorldRecovery>(State(root)).Current).Options.Mode == RuntimeMode.Learning) { Log(root, "learning-state-lost", "Optimizer/replay cannot be restored; refusing automatic continuation."); return 1; }
                if (child.ExitCode == 20) { failures = 0; continue; }
                var state = Read<WorldRecovery>(State(root));
                if (state.Pending is not null)
                {
                    state = state with { Current = state.Pending, Pending = null, Rejected = (state.Rejected ?? []).Append(state.Candidate ?? "unknown").Distinct().ToArray(), Candidate = null };
                    failures = 0;
                }
                else
                {
                    failures = failedGeneration == state.Current ? failures + 1 : 1;
                    failedGeneration = state.Current;
                    if (failures >= 2)
                    {
                        if (state.Previous is null) { Log(root, "recovery-failed", state); return 1; }
                        state = state with { Current = state.Previous, Previous = null };
                    }
                }
                WorldCheckpoint.Atomic(State(root), state);
                Log(root, "recovery", state);
            }
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    private sealed record Validation(string Catalog, PackageReference[] Packages, string Fingerprint, string Observation, string BaselineFingerprint, long BaselineRevision, string Checkpoint);
    private static Validation Validate(string root, string checkpoint, string[] sources, double timeout, CancellationToken cancellation)
    {
        string work = Path.Combine(root, "validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        WorldCheckpoint.Atomic(Path.Combine(work, "sources.json"), sources);
        var verifyArguments = new List<string> { "world", "verify", "--checkpoint", checkpoint, "--sources-file", Path.Combine(work, "sources.json"), "--output", work };
        if (File.Exists(Path.Combine(root, "policy", "current"))) verifyArguments.AddRange(["--policy", Path.Combine(root, "policy")]);
        using var child = StartCli(verifyArguments.ToArray());
        var elapsed = Stopwatch.StartNew();
        while (!child.WaitForExit(100))
            if (cancellation.IsCancellationRequested || elapsed.Elapsed.TotalSeconds > timeout)
            { child.Kill(true); child.WaitForExit(); throw new TimeoutException(cancellation.IsCancellationRequested ? "Validation cancelled." : "Package validation timed out."); }
        if (child.ExitCode != 0) throw new InvalidDataException(File.Exists(Path.Combine(work, "error.txt")) ? File.ReadAllText(Path.Combine(work, "error.txt")) : "Validation process crashed.");
        return Read<Validation>(Path.Combine(work, "validated.json"));
    }
    private static IAgentController? ValidationController(RuntimeOptions options, string? policy)
    {
        if (options.Mode == RuntimeMode.WorldOnly) return null;
        var controller = ControllerRegistry.Create(options);
        try
        {
            if (policy is not null) { var manifest = RuntimeCheckpoint.Read(policy); controller.Import(Path.Combine(RuntimeCheckpoint.Resolve(policy), "weights.bin")); controller.ImportInferenceState(manifest.ControllerState); }
            return controller;
        }
        catch { controller.Dispose(); throw; }
    }
    private static int Verify(Dictionary<string, string> a)
    {
        string output = a["--output"];
        try
        {
            var saved = WorldCheckpoint.Read(a["--checkpoint"]);
            string catalogRoot = Path.Combine(output, "packages");
            var importer = new RuntimePackageImporter(catalogRoot);
            foreach (var package in saved.Modules) importer.Import([Path.Combine(saved.Options.PackageRoot, package.Id, package.Version)]);
            var imported = importer.Import(Read<string[]>(a["--sources-file"]));
                var stagingOptions = saved.Options with { PackageRoot = catalogRoot };
            // Restore directly into the merged immutable catalogue, using the same runtime implementation.
            using var candidate = RuntimeSessionFactory.Create(stagingOptions, ValidationController(saved.Options, a.GetValueOrDefault("--policy")), observationKeys: saved.ObservationKeys, actionKeys: saved.ActionKeys, restoredWorld: saved.World, restoredModules: saved.Modules);
            candidate.RestoreState(saved.World, saved.Revision, saved.ObservationKeys, saved.ActionKeys, saved.LastReward);
            var refs = imported.Select(i => i.Reference).ToArray();
            var outcome = candidate.Activate(new("validation", refs));
            if (!outcome.Accepted) throw new InvalidDataException(outcome.Message);
            for (int i = 0; i < 1000; i++) candidate.AdvanceWorld();
            string checkpoint = WorldCheckpoint.Save(Path.Combine(output, "checkpoint"), candidate);
            using var reopened = WorldCheckpoint.Open(checkpoint, ValidationController(saved.Options, a.GetValueOrDefault("--policy")));
            if (candidate.Snapshot() != reopened.Snapshot()) throw new InvalidDataException("Validation restore mismatch.");
            string observation = Path.Combine(output, "observation.json");
            WriteObservation(observation, candidate, null, [], 0, 0);
            // Visual resources are verified and loaded by the independent Viewer.
            var catalog = new PackageCatalog(catalogRoot);
            string fingerprint = string.Join(";", refs.OrderBy(p => p.Id).Select(p => p.Id + "@" + p.Version + ":" + PackageCatalog.ManifestHash(catalog.DirectoryFor(p))));
            candidate.AdvanceWorld(); reopened.AdvanceWorld();
            if (candidate.Snapshot() != reopened.Snapshot()) throw new InvalidDataException("Validation continuation mismatch.");
            WorldCheckpoint.Atomic(Path.Combine(output, "validated.json"), new Validation(catalogRoot, refs, fingerprint, observation, string.Join(";", saved.Modules.Select(p => p.Id + "@" + p.Version + ":" + p.LogicHash)), saved.Revision, a["--checkpoint"]));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); return 1; }
    }
    private static void WriteObservation(string path, RuntimeSession runtime, string? operation, string[] checkpoints, double rate, double step)
    {
        var frame = runtime.Snapshot();
        WorldCheckpoint.Atomic(path, new WorldObservation(frame, runtime.Options.Mode, runtime.Packages.ToArray(), runtime.Options.PackageRoot,
            operation, runtime.Fault, checkpoints, rate, step, runtime.SystemMilliseconds, runtime.CaptureMetrics(message => Log(Path.GetDirectoryName(path)!, "metric-error", message), frame)));
    }
    private static int Worker(string root, Dictionary<string, string> a)
    {
        Environment.SetEnvironmentVariable("LIVE_SPEED", a.GetValueOrDefault("--speed", "Max"));
        var recovery = Read<WorldRecovery>(State(root));
        using var workerOwnership = new FileStream(Path.Combine(root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var saved = WorldCheckpoint.Read(recovery.Current);
        string commands = Path.Combine(root, "commands"); Directory.CreateDirectory(commands);
        string catalogRoot = Path.Combine(root, "packages");
        // Restored archives stay immutable. Live upgrades use a separate catalogue containing the active versions.
        var importer = new RuntimePackageImporter(catalogRoot);
        foreach (var package in saved.Modules) importer.Import([new PackageCatalog(saved.Options.PackageRoot).DirectoryFor(new(package.Id, package.Version))]);
        using var live = RuntimeSessionFactory.Create(saved.Options with { PackageRoot = catalogRoot, Inbox = Path.Combine(root, "activation") }, saved.Options.Mode == RuntimeMode.WorldOnly ? null : ControllerRegistry.Create(saved.Options), observationKeys: saved.ObservationKeys, actionKeys: saved.ActionKeys,
            restoredWorld: saved.World, restoredModules: saved.Modules);
        if (!live.WorldOnly) { var policyPath = Path.Combine(root, "policy"); var manifest = RuntimeCheckpoint.Read(policyPath); live.Controller.Import(Path.Combine(RuntimeCheckpoint.Resolve(policyPath), "weights.bin")); live.Controller.ImportInferenceState(manifest.ControllerState); }
        live.RestoreState(saved.World, saved.Revision, saved.ObservationKeys, saved.ActionKeys, saved.LastReward);
        using var trajectory = a.TryGetValue("--trajectory", out var trajectoryPath) ? new StreamWriter(trajectoryPath) : null;
        long loggedRevision = -1;
        if (trajectory is not null) live.TrajectorySink = transition =>
        {
            if (loggedRevision != transition.Revision) { trajectory.WriteLine(JsonSerializer.Serialize(new { Kind = "schema", transition.Revision, live.ObservationKeys, live.ActionKeys, Modules = live.Packages })); loggedRevision = transition.Revision; }
            trajectory.WriteLine(JsonSerializer.Serialize(new { Kind = "transition", Transition = transition }));
        };
        var timer = Stopwatch.StartNew(); double nextObservation = 0, nextSave = Positive(a, "--checkpoint-seconds", 30), nextCommands = 0;
        double validationTimeout = Positive(a, "--validation-seconds", 60);
        long probation = (long)Positive(a, "--probation-steps", 1000);
        long limit = a.TryGetValue("--steps", out var text) ? long.Parse(text) : saved.Options.Mode == RuntimeMode.Evaluation ? saved.Options.World.EpisodeSteps : long.MaxValue;
        if (limit < 1) throw new ArgumentException("Steps must be positive.");
        using var validationCancellation = new CancellationTokenSource();
        Task<Validation>? validation = null; WorldCommand? updating = null; string? updateFile = null; string? baseFingerprint = null;
        long probationEnd = recovery.Pending is null ? long.MaxValue : live.Age + probation;
        double stepMs = 0; long initialAge = live.Age;
        string observation = Path.Combine(root, "observation.json");
        var captures = new Dictionary<string, Task>();
        void Complete(string file, WorldCommand command, bool accepted, string message, string? artifact = null)
        {
            WorldCheckpoint.Atomic(Path.Combine(commands, command.Id + ".result.json"), new WorldCommandResult(command.Id, accepted, message, live.Age, live.Revision, artifact));
            File.Delete(file); Log(root, "command", new { command.Id, command.Operation, accepted, message });
            live.Notify("command:" + command.Id + ":" + (accepted ? "accepted:" : "rejected:") + message);
        }
        string Save()
        {
            if (!live.WorldOnly) { RuntimeCheckpoint.Save(Path.Combine(root, "policy"), live); if (a.TryGetValue("--checkpoint", out var export)) RuntimeCheckpoint.Save(export, live); }
            string generation = WorldCheckpoint.Save(Path.Combine(root, "checkpoints"), live);
            recovery = recovery with { Current = generation }; WorldCheckpoint.Atomic(State(root), recovery);
            Log(root, "checkpoint", generation); return generation;
        }
        string Fingerprint() => string.Join(";", live.Packages.Select(p => p.Id + "@" + p.Version + ":" + p.LogicHash));
        live.ExternalCommand = command =>
        {
            if (command.Operation is not ("stop" or "update" or "rollback")) return new(command.Id, false, "Unsupported command.");
            if (!live.WorldOnly && command.Operation == "rollback") return new(command.Id, false, "No exact training-state rollback is available.");
            WorldCheckpoint.Atomic(Path.Combine(commands, command.Id + ".ready.json"), new WorldCommand(command.Id, command.Operation, command.Value?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [], null));
            return new(command.Id, true, "Queued; completion is recorded by the protected command pipeline.");
        };
        double nextIdleHeartbeat = 0;
        live.IdleHeartbeat = () =>
        {
            if (timer.Elapsed.TotalSeconds < nextIdleHeartbeat) return;
            nextIdleHeartbeat = timer.Elapsed.TotalSeconds + .5;
            WorldCheckpoint.Atomic(Path.Combine(root, "heartbeat.json"), new { At = DateTimeOffset.UtcNow, live.Age, ProcessId = Environment.ProcessId });
            foreach (string file in Directory.EnumerateFiles(commands, "*.ready.json"))
            {
                var command = Read<WorldCommand>(file);
                if (command.Operation == "stop") { Complete(file, command, true, "Stopped.", Save()); throw new OperationCanceledException("Host stop requested."); }
            }
        };
        Log(root, "started", new { recovery.Current, Age = initialAge });
        try
        {
            for (long i = 0; i < limit; i++)
            {
                if (validation?.IsCompleted == true)
                {
                    var beforeUpdate = recovery;
                    try
                    {
                        var result = validation.GetAwaiter().GetResult();
                        if (Fingerprint() != baseFingerprint || result.BaselineFingerprint != baseFingerprint) throw new InvalidDataException("Active packages changed during validation.");
                        if ((recovery.Rejected ?? []).Contains(result.Fingerprint)) throw new InvalidDataException("This candidate previously failed in the live world.");
                        string actualFingerprint = string.Join(";", result.Packages.OrderBy(p => p.Id).Select(p => p.Id + "@" + p.Version + ":" + PackageCatalog.ManifestHash(new PackageCatalog(result.Catalog).DirectoryFor(p))));
                        if (actualFingerprint != result.Fingerprint) throw new InvalidDataException("Candidate changed after isolated validation.");
                        string before = Save();
                        foreach (var reference in result.Packages) importer.Import([new PackageCatalog(result.Catalog).DirectoryFor(reference)]);
                        recovery = recovery with { Pending = before, Previous = before, Candidate = result.Fingerprint };
                        WorldCheckpoint.Atomic(State(root), recovery); // intent precedes arbitrary plugin execution
                        Log(root, "activation-start", new { updating!.Id, result.Fingerprint, Before = before });
                        var outcome = live.Activate(new(updating!.Id, result.Packages));
                        if (!outcome.Accepted) throw new InvalidDataException(outcome.Message);
                        probationEnd = live.Age + probation;
                        Complete(updateFile!, updating, true, "Activated; probation in progress.", before);
                    }
                    catch (Exception ex)
                    {
                        Complete(updateFile!, updating!, false, ex.Message);
                        if (live.Fault is not null) throw;
                        recovery = beforeUpdate; WorldCheckpoint.Atomic(State(root), recovery);
                    }
                    validation = null; updating = null;
                }
                if (live.Age >= probationEnd)
                {
                    recovery = recovery with { Pending = null, Candidate = null }; Save(); probationEnd = long.MaxValue;
                    Log(root, "version-working", new { live.Revision });
                }
                if (timer.Elapsed.TotalSeconds >= nextCommands)
                {
                    nextCommands = timer.Elapsed.TotalSeconds + 0.1;
                    foreach (var key in captures.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray()) captures.Remove(key);
                    foreach (string file in Directory.EnumerateFiles(commands, "*.ready.json").Order(StringComparer.Ordinal))
                    {
                        if (file == updateFile && validation is not null) continue;
                        WorldCommand? command = null;
                        try
                        {
                            command = Read<WorldCommand>(file);
                            if (Path.GetFileName(file) != command.Id + ".ready.json") throw new InvalidDataException("Request id must match filename.");
                            if (File.Exists(Path.Combine(commands, command.Id + ".result.json"))) { File.Delete(file); continue; }
                            if (captures.ContainsKey(command.Id)) { File.Delete(file); continue; }
                            if (live.Options.Mode is RuntimeMode.Evaluation or RuntimeMode.Research && command.Operation is not ("stop" or "checkpoint" or "screenshot")) throw new InvalidOperationException("This mode forbids external intervention.");
                            switch (command.Operation)
                            {
                                case "checkpoint": Complete(file, command, true, "Saved.", Save()); break;
                                case "stop": validationCancellation.Cancel(); if (validation is not null) { try { validation.GetAwaiter().GetResult(); } catch { /* The ready request is retained for resumption. */ } } Task.WaitAll(captures.Values.ToArray()); Complete(file, command, true, "Stopped.", Save()); WriteObservation(observation, live, null, [recovery.Current], 0, stepMs); return 0;
                                case "rollback":
                                    if (!live.WorldOnly) throw new InvalidOperationException("Exact controller state rollback is unavailable.");
                                    if (validation is not null) throw new InvalidOperationException("Wait for validation before rollback.");
                                    if (recovery.Previous is null) throw new InvalidOperationException("No previous deployment checkpoint.");
                                    recovery = recovery with { Current = recovery.Previous, Previous = null, Pending = null, Candidate = null }; WorldCheckpoint.Atomic(State(root), recovery);
                                    Complete(file, command, true, "Restarting from previous deployment.", recovery.Current); return 20;
                                case "screenshot":
                                    string png = command.Target ?? Path.Combine(root, "captures", command.Id + ".png");
                                    // Rendering is outside the simulation owner thread.
                                    string observedSession = File.ReadAllText(Path.Combine(root, "session-id")).Trim();
                                    captures[command.Id] = Task.Run(() => { try { var captured = Capture(observedSession, png); WorldCheckpoint.Atomic(Path.Combine(commands, command.Id + ".result.json"), new WorldCommandResult(command.Id, true, "Captured.", captured.Age, captured.Revision, png)); } catch (Exception ex) { WorldCheckpoint.Atomic(Path.Combine(commands, command.Id + ".result.json"), new WorldCommandResult(command.Id, false, ex.Message, 0, 0)); } });
                                    File.Delete(file); break;
                                case "update":

                                    if (validation is not null || recovery.Pending is not null) throw new InvalidOperationException("Another update is being validated or observed.");
                                    string checkpoint = Save();
                                    updating = command; updateFile = file; baseFingerprint = Fingerprint();
                                    Log(root, "validation-start", command);
                                    validation = Task.Run(() => Validate(root, checkpoint, command.Sources, validationTimeout, validationCancellation.Token)); break;
                                default: throw new InvalidDataException("Unknown command operation.");
                            }
                        }
                        catch (Exception ex)
                        {
                            command ??= new WorldCommand(Path.GetFileName(file)[..^11], "invalid", [], null);
                            Complete(file, command, false, ex.Message);
                        }
                    }
                    // Legacy activation producers use the same protected update pipeline.
                    string inbox = live.Options.Inbox;
                    foreach (var item in new ActivationInbox(inbox).ReadReady())
                    {
                        if (item.Request is null) { ActivationInbox.Complete(item.Path, new(Path.GetFileName(item.Path), false, item.Error!, live.Revision)); continue; }
                        string id = item.Request.Id;
                        if (id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) { ActivationInbox.Complete(item.Path, new(id, false, "Invalid request id.", live.Revision)); continue; }
                        string result = Path.Combine(commands, id + ".result.json");
                        if (File.Exists(result)) { var done = Read<WorldCommandResult>(result); ActivationInbox.Complete(item.Path, new(id, done.Accepted, done.Message, done.Revision)); }
                        else if (!File.Exists(Path.Combine(commands, id + ".ready.json"))) WorldCheckpoint.Atomic(Path.Combine(commands, id + ".ready.json"), new WorldCommand(id, "update", item.Request.Packages.Select(p => new PackageCatalog(catalogRoot).DirectoryFor(p)).ToArray(), null));
                    }
                }
                if (timer.Elapsed.TotalSeconds >= nextObservation)
                {
                    nextObservation = timer.Elapsed.TotalSeconds + 1;
                    WriteObservation(observation, live, updating?.Id, new[] { recovery.Current, recovery.Previous, recovery.Pending }.OfType<string>().Distinct().ToArray(), (live.Age - initialAge) / Math.Max(0.001, timer.Elapsed.TotalSeconds), stepMs);
                    WorldCheckpoint.Atomic(Path.Combine(root, "heartbeat.json"), new { At = DateTimeOffset.UtcNow, live.Age, ProcessId = Environment.ProcessId });
                }
                if (timer.Elapsed.TotalSeconds >= nextSave) { Save(); Prune(root, recovery); nextSave = timer.Elapsed.TotalSeconds + Positive(a, "--checkpoint-seconds", 30); }
                long started = Stopwatch.GetTimestamp(); live.Step(live.Options.Mode == RuntimeMode.Learning); stepMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            if (validation is not null) throw new InvalidOperationException("Step limit reached during validation; update remains pending for retry.");
            Task.WaitAll(captures.Values.ToArray());
            WorldCheckpoint.Atomic(Path.Combine(root, "performance.json"), new { Steps = live.Age - initialAge, Seconds = timer.Elapsed.TotalSeconds, StepsPerSecond = (live.Age - initialAge) / Math.Max(.001, timer.Elapsed.TotalSeconds), ObservationFrames = live.ObservationFramesCaptured, ManagedBytes = GC.GetTotalMemory(false), PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
            Save(); WriteObservation(observation, live, null, [recovery.Current], 0, stepMs); return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Log(root, "fault", ex.ToString()); try { WriteObservation(observation, live, updating?.Id, [recovery.Current], 0, stepMs); } catch { /* Keep the original fault if diagnostics also fail. */ } return 1; }
    }
    private static void Prune(string root, WorldRecovery recovery)
    {
        string directory = Path.Combine(root, "checkpoints");
        var keep = new[] { recovery.Current, recovery.Previous, recovery.Pending }.OfType<string>().Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetDirectories(directory, "world-*").OrderByDescending(Directory.GetCreationTimeUtc).Skip(3))
            if (!keep.Contains(Path.GetFullPath(path))) Directory.Delete(path, true);
    }
    public static ArtificialLife.Observation.CaptureMetadata Capture(string session, string target)
    {
        string root = RuntimeDefaults.RepositoryRoot();
        string godot = Directory.GetFiles(Path.Combine(root, ".tools", "godot"), "*console.exe", SearchOption.AllDirectories).Single();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var start = new ProcessStartInfo(godot) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string value in new[] { "--audio-driver", "Dummy", "--path", Path.Combine(root, "src", "ArtificialLife.Godot"), "--", "--session", session, "--capture", target }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(30_000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Rendering timed out."); }
        if (process.ExitCode != 0 || !File.Exists(target)) throw new InvalidDataException("Godot rendering failed.");
        return Read<ArtificialLife.Observation.CaptureMetadata>(target + ".json");
    }
}
