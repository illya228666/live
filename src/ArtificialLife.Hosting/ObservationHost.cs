using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using ArtificialLife.Core;
using ArtificialLife.Observation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ArtificialLife.Hosting;

/// <summary>One loopback endpoint per process. Socket tasks never access a live runtime.</summary>
public sealed class ObservationHost : IDisposable
{
    private readonly WebApplication app;
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly Timer heartbeat;
    private readonly string registryFile;
    private readonly string runId = Environment.GetEnvironmentVariable("LIVE_RUN_ID") ?? Guid.NewGuid().ToString("N");
    public string Endpoint { get; }
    public bool ReadOnly { get; }
    private volatile bool disposed;
    public ObservationHost(bool readOnly = false)
    {
        ReadOnly = readOnly;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/sessions", () => sessions.Values.Select(s => s.Descriptor).ToArray());
        app.MapGet("/observe/{id}", async (HttpContext context, string id) =>
        {
            if (!sessions.TryGetValue(id, out var session)) { context.Response.StatusCode = 404; return; }
            if (context.Request.Query["version"] != "1") { context.Response.StatusCode = 400; await context.Response.WriteAsync("Unsupported observation protocol; expected version=1."); return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await session.Connect(socket, context.RequestAborted);
        });
        app.MapGet("/resources/{id}/{**path}", (string id, string path) =>
        {
            if (!sessions.TryGetValue(id, out var session) || !session.Resources.TryGetValue(path, out var file)) return Results.NotFound();
            return File.Exists(file) ? Results.File(file, "application/octet-stream") : Results.NotFound();
        });
        app.StartAsync().GetAwaiter().GetResult();
        Endpoint = app.Urls.Single();
        Directory.CreateDirectory(ObservationProtocol.Registry);
        registryFile = Path.Combine(ObservationProtocol.Registry, Environment.ProcessId + ".json");
        heartbeat = new Timer(_ => { try { WorldCheckpoint.Atomic(registryFile, new HostDescriptor(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Endpoint, DateTimeOffset.UtcNow)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }, null, 0, 1000);
        RuntimeObservation.Attach = Attach;
    }
    private IRuntimeObservation Attach(RuntimeSession runtime)
    {
        string? parentId = runtime.ParentSessionId;
        if (parentId is not null && !sessions.ContainsKey(parentId)) throw new InvalidOperationException("Observation parent session is unavailable.");
        string? trialId = runtime.TrialId ?? (parentId is not null && sessions.TryGetValue(parentId, out var origin) ? origin.Descriptor.TrialId : null);
        string id = parentId is null ? Environment.GetEnvironmentVariable("LIVE_WORLD_SESSION") ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        while (true)
        {
            var session = new Session(this, runtime, new(runId, id, id, Guid.NewGuid().ToString("N"), ReadOnly && runtime.Options.Mode != RuntimeMode.Evaluation ? "Research" : runtime.Options.Mode.ToString(), "running", Endpoint, parentId, trialId));
            if (sessions.TryAdd(id, session))
            {
                if (parentId is not null && sessions.TryGetValue(parentId, out var parent)) parent.Suspend();
                return session;
            }
            id = Guid.NewGuid().ToString("N");
        }
    }
    public static HostDescriptor[] Discover()
    {
        if (!Directory.Exists(ObservationProtocol.Registry)) return [];
        var result = new List<HostDescriptor>();
        foreach (var file in Directory.GetFiles(ObservationProtocol.Registry, "*.json"))
            try
            {
                var host = JsonSerializer.Deserialize<HostDescriptor>(File.ReadAllText(file), PackageCatalog.Json)!;
                using var process = Process.GetProcessById(host.ProcessId);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == host.ProcessStartTicks && DateTimeOffset.UtcNow - host.Heartbeat < TimeSpan.FromSeconds(5)) result.Add(host);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException) { }
        return result.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RuntimeObservation.Attach = null;
        heartbeat.Dispose();
        File.Delete(registryFile);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        app.StopAsync(stop.Token).GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private sealed class Session(ObservationHost host, RuntimeSession runtime, SessionDescriptor descriptor) : IRuntimeObservation
    {
        public SessionDescriptor Descriptor = descriptor;
        public string SessionId => Descriptor.SessionId;
        public ConcurrentDictionary<string, string> Resources { get; } = new();
        private readonly ConcurrentQueue<ObservationCommand> commands = new();
        private int pendingCommands;
        private readonly ConcurrentDictionary<(string Epoch, string Id), CommandResult> results = new();
        private readonly ConcurrentDictionary<Guid, System.Threading.Channels.Channel<object>> clients = new();
        private sealed record Publication(ObservationMessage Message) { public Lazy<byte[]> Bytes { get; } = new(() => JsonSerializer.SerializeToUtf8Bytes(Message, ObservationProtocol.Json), true); }
        private Publication? latest;
        private long sequence, metadata;
        public long FramesCaptured => Volatile.Read(ref sequence);
        private long packageRevision = -1;
        private string? nextEvent;
        public void Notify(string kind) { if (kind is "restored" or "rollback") Descriptor = Descriptor with { Epoch = Guid.NewGuid().ToString("N") }; nextEvent = kind; nextFrame = 0; Tick(); }
        private long nextFrame;
        private double speed = Environment.GetEnvironmentVariable("LIVE_SPEED")?.ToLowerInvariant() switch { "1x" => 1, "10x" => 10, "100x" => 100, _ => double.PositiveInfinity };
        private bool paused, disposed;
        private RuntimeSession? evaluation;
        private readonly Stopwatch pacing = Stopwatch.StartNew();
        private double nextStep;
        private VisualPackage[] visuals = [];
        private ModuleManifest[] packages = [];
        public void BeforeStep()
        {
            if (Descriptor.ParentId is string parentId && host.sessions.TryGetValue(parentId, out var suspendedParent)) suspendedParent.Tick();
            do
            {
                runtime.IdleHeartbeat?.Invoke();
                while (commands.TryDequeue(out var command))
                {
                    Interlocked.Decrement(ref pendingCommands);
                    if (command.Epoch != Descriptor.Epoch) { Broadcast(new CommandResult(command.Id, false, "Stale session generation.")); continue; }
                    var resultKey = (command.Epoch, command.Id);
                    if (results.TryGetValue(resultKey, out var prior)) { Broadcast(prior); continue; }
                    CommandResult result;
                    try
                    {
                        if (host.ReadOnly || runtime.Options.Mode is RuntimeMode.Evaluation or RuntimeMode.Research) throw new InvalidOperationException("This run forbids external intervention.");
                        if (command.Operation == "new-life" && (runtime.Options.Mode != RuntimeMode.Learning || !runtime.OwnsController) || command.Operation == "reset" && runtime.Options.Mode == RuntimeMode.Learning || command.Operation == "evaluation" && runtime.Options.Mode != RuntimeMode.Learning) throw new InvalidOperationException("Command is not permitted in this mode.");
                        if (results.Count >= 1024) results.Clear();
                        switch (command.Operation)
                        {
                            case "pause": paused = true; break;
                            case "resume": paused = false; nextStep = pacing.Elapsed.TotalSeconds; break;
                            case "speed": speed = command.Value?.ToLowerInvariant() switch { "1x" => 1, "10x" => 10, "100x" => 100, "max" => double.PositiveInfinity, _ => throw new ArgumentException("Speed must be 1x, 10x, 100x or Max.") }; nextStep = pacing.Elapsed.TotalSeconds; break;
                            case "reset":
                            case "new-life": evaluation?.Dispose(); evaluation = null; runtime.ResetLife(command.Operation == "new-life"); Descriptor = Descriptor with { Epoch = Guid.NewGuid().ToString("N"), WorldId = Guid.NewGuid().ToString("N") }; packageRevision = -1; break;
                            case "evaluation": if (runtime.WorldOnly) throw new InvalidOperationException("No agent to evaluate."); if (evaluation is not null) { evaluation.Dispose(); evaluation = null; } else { var options = runtime.Options with { Mode = RuntimeMode.Evaluation, Packages = runtime.Packages.Select(p => new PackageReference(p.Id, p.Version)).ToArray(), Settings = runtime.Packages.ToDictionary(p => p.Id, p => p.Settings.Clone()) }; evaluation = RuntimeSessionFactory.Create(options, runtime.Controller.CreateInferenceController(), observationKeys: runtime.ObservationKeys, actionKeys: runtime.ActionKeys, parentSessionId: Descriptor.SessionId); } break;
                            case "checkpoint": if (runtime.WorldOnly) WorldCheckpoint.Save(command.Value ?? "artifacts/checkpoints/world", runtime); else RuntimeCheckpoint.Save(command.Value ?? "artifacts/checkpoints/live-thermal", runtime); break;
                            default: if (runtime.ExternalCommand is not null) { var external = runtime.ExternalCommand(command); results[resultKey] = external; Broadcast(external); paused = false; continue; } throw new ArgumentException("Unsupported command.");
                        }
                        result = new(command.Id, true, "Applied.");
                    }
                    catch (Exception ex) { result = new(command.Id, false, ex.Message); }
                    if (results.Count >= 1024) results.Clear();
                    results[resultKey] = result; Broadcast(result); nextFrame = 0;
                }
                Tick();
                if (evaluation is not null) evaluation.Step(false);
                if (paused) Thread.Sleep(10);
            } while ((paused || evaluation is not null) && !disposed && !host.disposed);
            if (!double.IsPositiveInfinity(speed))
            {
                double rate = (runtime.Options.Mode is RuntimeMode.Learning or RuntimeMode.Research ? 60 : 1 / runtime.Options.World.TimeStep) * speed;
                double wait = nextStep - pacing.Elapsed.TotalSeconds;
                while (wait > 0 && commands.IsEmpty && !host.disposed) { Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, .01))); runtime.IdleHeartbeat?.Invoke(); wait = nextStep - pacing.Elapsed.TotalSeconds; }
                nextStep = Math.Max(nextStep, pacing.Elapsed.TotalSeconds) + 1 / rate;
            }
        }
        public void Tick()
        {
            try { Capture(); } catch (Exception ex) { nextFrame = Stopwatch.GetTimestamp() + Stopwatch.Frequency; Console.Error.WriteLine("Observation capture failed: " + ex.Message); }
        }
        public void Suspend() { Descriptor = Descriptor with { Status = "suspended" }; Notify("suspended"); }
        private void Capture()
        {
            if (clients.IsEmpty || Stopwatch.GetTimestamp() < nextFrame) return;
            nextFrame = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20;
            string? change = null;
            if (packageRevision != runtime.MetadataRevision)
            {
                packageRevision = runtime.MetadataRevision; metadata = packageRevision; packages = runtime.Packages.ToArray(); change = "modules";
                Resources.Clear();
                visuals = runtime.PresentationPackages.Select(p =>
                {
                    var files = new Dictionary<string, string>(p.Manifest.PresentationFiles);
                    if (p.Manifest.PresentationAssembly is string assembly) files[assembly] = p.Manifest.PresentationHash ?? "";
                    var resources = new List<ResourceFile>();
                    foreach (var file in files)
                    {
                        try
                        {
                            string resource = p.Manifest.Id + "/" + p.Manifest.Version + "/" + file.Key;
                            Resources[resource] = PackageCatalog.FileWithin(p.Directory, file.Key);
                            resources.Add(new(file.Key, file.Value, host.Endpoint + "/resources/" + Descriptor.SessionId + "/" + string.Join("/", resource.Split('/').Select(Uri.EscapeDataString))));
                        }
                        catch (InvalidDataException) { /* Invalid visual paths use Viewer fallback. */ }
                    }
                    return new VisualPackage(p.Manifest, resources.ToArray());
                }).ToArray();
            }
            var frame = runtime.Snapshot();
            var metrics = runtime.CaptureMetrics(frame: frame).ToDictionary();
            metrics["reward"] = runtime.LastReward;
            if (!runtime.WorldOnly) { var report = runtime.Report(); metrics["balance"] = report.Balance; foreach (var drive in report.Drives) metrics["drive:" + drive.Key + ":satisfaction"] = drive.Value.Satisfaction; }
            string[] capabilities = host.ReadOnly || runtime.Options.Mode is RuntimeMode.Evaluation or RuntimeMode.Research ? [] : runtime.Options.Mode == RuntimeMode.Learning ? ["pause", "resume", "speed", "checkpoint", "new-life", "evaluation"] : ["pause", "resume", "speed", "checkpoint", "reset"];
            if (!runtime.OwnsController) capabilities = capabilities.Where(c => c != "new-life").ToArray();
            if (capabilities.Length > 0 && runtime.ExternalCommand is not null) capabilities = capabilities.Concat(runtime.WorldOnly ? new[] { "stop", "update", "rollback" } : new[] { "stop", "update" }).ToArray();
            ControllerStatus? controller = runtime.WorldOnly ? null : runtime.Controller.Status;
            if (controller is not null && runtime.Options.Mode is RuntimeMode.Inference or RuntimeMode.Evaluation) controller = controller with { Epsilon = 0 };
            var state = new ObservationState(frame, packages, visuals, controller,
                metrics, capabilities, speed == double.PositiveInfinity ? 0 : speed, paused, runtime.Fault);
            var message = new ObservationMessage(1, "full", Descriptor, ++sequence, metadata, state, nextEvent ?? change);
            nextEvent = null;
            latest = new(message);
            if (message.Event is not null) Broadcast(latest);
        }
        private void Broadcast(object value)
        {
            foreach (var pair in clients) if (!pair.Value.Writer.TryWrite(value)) pair.Value.Writer.TryComplete(new IOException("Event queue overflow; reconnect for full state."));
        }
        public async Task Connect(WebSocket socket, CancellationToken cancellation)
        {
            var id = Guid.NewGuid();
            var events = System.Threading.Channels.Channel.CreateBounded<object>(64);
            clients[id] = events; Interlocked.Exchange(ref nextFrame, 0);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var receive = Task.Run(async () =>
            {
                try
                {
                var buffer = new byte[8192];
                while (!stop.IsCancellationRequested)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (!received.EndOfMessage || received.MessageType != WebSocketMessageType.Text || commands.Count >= 128) break;
                    var command = JsonSerializer.Deserialize<ObservationCommand>(buffer.AsSpan(0, received.Count), ObservationProtocol.Json);
                    if (command is not null)
                    {
                        if (string.IsNullOrEmpty(command.Id) || command.Id.Length > 128 || command.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                        {
                            Broadcast(new CommandResult(command.Id ?? "", false, "Invalid command id."));
                            continue;
                        }
                    if (Interlocked.Increment(ref pendingCommands) > 128) { Interlocked.Decrement(ref pendingCommands); break; }
                    commands.Enqueue(command);
                    }
                }
                }
                finally { stop.Cancel(); }
            }, stop.Token);
            try
            {
                long sent = -1;
                while (!stop.IsCancellationRequested)
                {
                    if (events.Reader.Completion.IsFaulted) break;
                    var frame = Volatile.Read(ref latest);
                    while (events.Reader.TryRead(out var value)) { await Send(value); if (value is Publication published) sent = Math.Max(sent, published.Message.Sequence); }
                    if (frame is not null && frame.Message.Sequence > sent)
                    {
                        await Send(frame); sent = frame.Message.Sequence;
                        if (frame.Message.Session.Status is "completed" or "faulted") break;
                    }
                    await Task.Delay(10, stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
            finally { clients.TryRemove(id, out _); stop.Cancel(); try { await receive; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException) { } }
            async Task Send(object value)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(2000);
                await socket.SendAsync(value is Publication publication ? publication.Bytes.Value : JsonSerializer.SerializeToUtf8Bytes(value, ObservationProtocol.Json), WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        public void Dispose()
        {
            evaluation?.Dispose(); evaluation = null;
            Descriptor = Descriptor with { Status = runtime.Fault is null ? "completed" : "faulted" };
            nextEvent = runtime.Fault is null ? "completed" : "error";
            nextFrame = 0; Tick(); disposed = true;
            host.sessions.TryRemove(Descriptor.SessionId, out _);
            if (Descriptor.ParentId is string parentId && !host.sessions.Values.Any(s => s.Descriptor.ParentId == parentId) && host.sessions.TryGetValue(parentId, out var parent))
            {
                parent.Descriptor = parent.Descriptor with { Status = "running" };
                parent.Notify("resumed");
            }
        }
    }
}
