using ArtificialLife.Application;
using ArtificialLife.Observation;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class ObservationTests
{
    [Theory]
    [InlineData(RuntimeMode.WorldOnly, true)]
    [InlineData(RuntimeMode.Evaluation, false)]
    [InlineData(RuntimeMode.Research, false)]
    public async Task ProtectedModesRejectExternalCommandsOverRealWebSocket(RuntimeMode mode, bool readOnly)
    {
        using var host = new ObservationHost(readOnly);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var owner = Task.Run(() =>
        {
            var options = mode == RuntimeMode.WorldOnly ? new RuntimeOptions() : RuntimeDefaults.FromLegacy(new());
            using var runtime = RuntimeSessionFactory.Create(options with { Mode = mode });
            while (!stop.IsCancellationRequested) { runtime.Step(); Thread.Sleep(1); }
        }, stop.Token);
        try
        {
            using var http = new HttpClient();
            SessionDescriptor[] sessions = [];
            while (sessions.Length == 0) { sessions = JsonSerializer.Deserialize<SessionDescriptor[]>(await http.GetStringAsync(host.Endpoint + "/sessions", timeout.Token), ObservationProtocol.Json)!; await Task.Delay(20, timeout.Token); }
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(host.Endpoint.Replace("http://", "ws://") + "/observe/" + sessions[0].SessionId + "?version=1"), timeout.Token);
            var initial = await Frame(client, timeout.Token);
            Assert.Empty(initial.State!.Capabilities);
            await Command(client, new("forbidden", initial.Session.Epoch, "pause"), timeout.Token);
            var buffer = new byte[65536];
            while (true)
            {
                using var data = new MemoryStream(); WebSocketReceiveResult part;
                do { part = await client.ReceiveAsync(buffer, timeout.Token); data.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(data.ToArray());
                if (!json.RootElement.TryGetProperty("id", out var id) || id.GetString() != "forbidden") continue;
                Assert.False(json.RootElement.GetProperty("accepted").GetBoolean());
                Assert.Contains("forbids", json.RootElement.GetProperty("message").GetString());
                break;
            }
            Assert.False((await Frame(client, timeout.Token)).State!.Paused);
        }
        finally { stop.Cancel(); host.Dispose(); await owner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
    }
    [Fact]
    public async Task IndependentWorldsAndExplicitEvaluationChildrenHaveDistinctIdentities()
    {
        using var host = new ObservationHost();
        using var first = RuntimeSessionFactory.Create(new() { Mode = RuntimeMode.WorldOnly }, trialId: "fixture/trial-1");
        using var second = new RuntimeSession(new() { Mode = RuntimeMode.WorldOnly });
        using var http = new HttpClient();
        async Task<SessionDescriptor[]> Sessions() => JsonSerializer.Deserialize<SessionDescriptor[]>(
            await http.GetStringAsync(host.Endpoint + "/sessions", TestContext.Current.CancellationToken), ObservationProtocol.Json)!;
        var roots = await Sessions();
        Assert.Equal(2, roots.Length);
        Assert.All(roots, s => { Assert.Null(s.ParentId); Assert.Equal("running", s.Status); });
        using (var child = new RuntimeSession(new() { Mode = RuntimeMode.WorldOnly }, parentSessionId: first.ObservationSessionId))
        {
            var active = await Sessions();
            Assert.Equal(3, active.Length);
            Assert.Equal("suspended", active.Single(s => s.SessionId == first.ObservationSessionId).Status);
            Assert.Equal("running", active.Single(s => s.SessionId == second.ObservationSessionId).Status);
            Assert.Equal(first.ObservationSessionId, active.Single(s => s.SessionId == child.ObservationSessionId).ParentId);
            Assert.Equal("fixture/trial-1", active.Single(s => s.SessionId == child.ObservationSessionId).TrialId);
            Assert.Equal(3, active.Select(s => s.WorldId).Distinct().Count());
        }
        Assert.All(await Sessions(), s => Assert.Equal("running", s.Status));
    }
    [Fact]
    public async Task RealWebSocketSupportsLateJoinPauseCommandsAndReconnect()
    {
        using var host = new ObservationHost();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var owner = Task.Run(() => { using var runtime = new RuntimeSession(new() { Mode = RuntimeMode.WorldOnly }); while (!stop.IsCancellationRequested) { runtime.Step(); Thread.Sleep(1); } }, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var http = new HttpClient();
            SessionDescriptor[] sessions = [];
            while (sessions.Length == 0) { sessions = JsonSerializer.Deserialize<SessionDescriptor[]>(await http.GetStringAsync(host.Endpoint + "/sessions", timeout.Token), ObservationProtocol.Json)!; await Task.Delay(20, timeout.Token); }
            var session = sessions[0];
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await http.GetAsync(host.Endpoint + "/observe/" + session.SessionId + "?version=99", timeout.Token)).StatusCode);
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(host.Endpoint.Replace("http://", "ws://") + "/observe/" + session.SessionId + "?version=1"), timeout.Token);
            var initial = await Frame(client, timeout.Token);
            Assert.True(initial.State!.Frame.Age > 0);
            Assert.False(initial.State.Frame.HasAgent);
            await Command(client, new(null!, session.Epoch, "pause"), timeout.Token);
            Assert.False((await Frame(client, timeout.Token)).State!.Paused);
            await Command(client, new("pause", session.Epoch, "pause"), timeout.Token);
            var paused = await Frame(client, timeout.Token);
            while (!paused.State!.Paused) paused = await Frame(client, timeout.Token);
            long age = paused.State.Frame.Age;
            await Task.Delay(120, timeout.Token);
            Assert.Equal(age, (await Frame(client, timeout.Token)).State!.Frame.Age);
            using (var concurrent = new ClientWebSocket())
            {
                await concurrent.ConnectAsync(new Uri(host.Endpoint.Replace("http://", "ws://") + "/observe/" + session.SessionId + "?version=1"), timeout.Token);
                Assert.Equal(age, (await Frame(concurrent, timeout.Token)).State!.Frame.Age);
                concurrent.Abort();
            }
            await Command(client, new("stale", "old-generation", "resume"), timeout.Token);
            Assert.True((await Frame(client, timeout.Token)).State!.Paused);
            await Command(client, new("resume", session.Epoch, "resume"), timeout.Token);
            client.Abort();
            using var second = new ClientWebSocket();
            await second.ConnectAsync(new Uri(host.Endpoint.Replace("http://", "ws://") + "/observe/" + session.SessionId + "?version=1"), timeout.Token);
            var resumed = await Frame(second, timeout.Token);
            while (resumed.State!.Paused || resumed.State.Frame.Age <= age) resumed = await Frame(second, timeout.Token);
            Assert.Equal(initial.Session.WorldId, resumed.Session.WorldId);
            await Command(second, new("reset", resumed.Session.Epoch, "reset"), timeout.Token);
            var reset = await Frame(second, timeout.Token);
            while (reset.Session.Epoch == resumed.Session.Epoch) reset = await Frame(second, timeout.Token);
            Assert.NotEqual(resumed.Session.WorldId, reset.Session.WorldId);
            // Even a previously accepted id must be rejected when its target generation is stale.
            await Command(second, new("resume", resumed.Session.Epoch, "resume"), timeout.Token);
            var staleBuffer = new byte[65536];
            while (true)
            {
                using var data = new MemoryStream(); WebSocketReceiveResult part;
                do { part = await second.ReceiveAsync(staleBuffer, timeout.Token); data.Write(staleBuffer, 0, part.Count); } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(data.ToArray());
                if (!json.RootElement.TryGetProperty("id", out var id) || id.GetString() != "resume") continue;
                Assert.False(json.RootElement.GetProperty("accepted").GetBoolean());
                break;
            }
        }
        finally { stop.Cancel(); host.Dispose(); await owner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
    }
    private static async Task Command(ClientWebSocket socket, ObservationCommand command, CancellationToken cancellation) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(command, ObservationProtocol.Json), WebSocketMessageType.Text, true, cancellation);
    private static async Task<ObservationMessage> Frame(ClientWebSocket socket, CancellationToken cancellation)
    {
        while (true)
        {
            using var stream = new MemoryStream(); var buffer = new byte[16384]; WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(buffer, cancellation); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
            using var json = JsonDocument.Parse(stream.ToArray());
            if (json.RootElement.TryGetProperty("version", out _)) return JsonSerializer.Deserialize<ObservationMessage>(stream.ToArray(), ObservationProtocol.Json)!;
        }
    }
}
