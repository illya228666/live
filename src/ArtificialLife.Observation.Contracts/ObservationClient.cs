using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace ArtificialLife.Observation;

public sealed class ObservationClient : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly System.Threading.Channels.Channel<ObservationCommand> commands = System.Threading.Channels.Channel.CreateBounded<ObservationCommand>(64);
    private ClientWebSocket? connection;
    private string? activeEpoch;
    public SessionDescriptor[] Sessions { get; private set; } = [];
    public string Status { get; private set; } = "Discovering local sessions";
    public string? Selected { get; private set; }
    private string? selectedRun;
    public ObservationMessage? Latest;
    public void Select(string id) { activeEpoch = null; Selected = id; selectedRun = Sessions.FirstOrDefault(s => s.SessionId == id)?.RunId; connection?.Abort(); }
    public void Send(string operation, string? value = null)
    {
        if (Latest is { } state && state.Session.SessionId == Selected && state.Session.Epoch == activeEpoch && connection?.State == WebSocketState.Open)
            commands.Writer.TryWrite(new(Guid.NewGuid().ToString("N"), state.Session.Epoch, operation, value));
    }
    public async Task Run(string? session = null, string? endpoint = null)
    {
        Selected = session;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Refresh(endpoint, stop.Token);
                var found = Sessions;
                var target = found.FirstOrDefault(s => s.SessionId == Selected) ?? (Selected is null ? found.FirstOrDefault() : found.FirstOrDefault(s => s.RunId == selectedRun));
                if (target is null) { Status = "Waiting for selected session"; await Task.Delay(500, stop.Token); continue; }
                Selected = target.SessionId; selectedRun = target.RunId;
                using var socket = new ClientWebSocket(); connection = socket;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await socket.ConnectAsync(new Uri(target.Endpoint.Replace("http://", "ws://") + "/observe/" + target.SessionId + "?version=1"), linked.Token);
                Status = "Connected";
                var discovery = Task.Run(async () => { try { while (!linked.IsCancellationRequested) { await Task.Delay(1000, linked.Token); await Refresh(endpoint, linked.Token); } } catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException) { } }, linked.Token);
                var sender = Task.Run(async () =>
                {
                    try { await foreach (var command in commands.Reader.ReadAllAsync(linked.Token)) await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(command, ObservationProtocol.Json), WebSocketMessageType.Text, true, linked.Token); }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                }, linked.Token);
                try
                {
                    var buffer = new byte[16384];
                    while (!linked.IsCancellationRequested)
                    {
                        using var data = new MemoryStream(); ValueWebSocketReceiveResult result;
                        do { result = await socket.ReceiveAsync(buffer.AsMemory(), linked.Token); if (result.MessageType == WebSocketMessageType.Close) break; data.Write(buffer, 0, result.Count); if (data.Length > 64 * 1024 * 1024) throw new IOException("Observation exceeds size limit."); } while (!result.EndOfMessage);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        using var json = JsonDocument.Parse(data.ToArray());
                        if (json.RootElement.TryGetProperty("version", out _))
                        {
                            var frame = JsonSerializer.Deserialize<ObservationMessage>(data.ToArray(), ObservationProtocol.Json)!;
                            if (frame.Version != 1) throw new IOException("Unsupported observation protocol.");
                            Volatile.Write(ref Latest, frame);
                            activeEpoch = frame.Session.Epoch;
                            if (frame.Event is not null) Status = frame.Event;
                        }
                        else Status = json.RootElement.ToString();
                    }
                }
                finally { activeEpoch = null; connection = null; linked.Cancel(); await Task.WhenAll(sender, discovery); while (commands.Reader.TryRead(out _)) { } }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or WebSocketException or JsonException or OperationCanceledException) { Status = "Reconnecting: " + ex.Message; }
            if (!stop.IsCancellationRequested) await Task.Delay(500, stop.Token).ConfigureAwait(false);
        }
    }
    public async Task Refresh(string? endpoint, CancellationToken cancellation)
    {
        var urls = new List<string>();
        if (endpoint is not null) urls.Add(endpoint);
        else if (Directory.Exists(ObservationProtocol.Registry)) foreach (string file in Directory.GetFiles(ObservationProtocol.Registry, "*.json"))
            try
            {
                var host = JsonSerializer.Deserialize<HostDescriptor>(File.ReadAllText(file), ObservationProtocol.Json)!;
                using var process = Process.GetProcessById(host.ProcessId);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == host.ProcessStartTicks && DateTimeOffset.UtcNow - host.Heartbeat < TimeSpan.FromSeconds(5)) urls.Add(host.Endpoint);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException) { }
        var found = new List<SessionDescriptor>();
        foreach (string url in urls) try { found.AddRange(JsonSerializer.Deserialize<SessionDescriptor[]>(await http.GetStringAsync(url + "/sessions", cancellation), ObservationProtocol.Json) ?? []); } catch (HttpRequestException) { }
        Sessions = found.ToArray();
    }
    public void Dispose() { stop.Cancel(); connection?.Abort(); http.Dispose(); }
}
