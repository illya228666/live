using ArtificialLife.Application;
using ArtificialLife.Observation;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class ObservationLearningTests
{
    private sealed record Result(string World, string Weights, string Actions, long Updates, double Seconds, long Frames);
    [Fact]
    public async Task FastAndUnreadClientsPreserveLearningAndBoundedPublication()
    {
        var rows = new List<object>();
        Result? reference = null;
        for (int repetition = 0; repetition < 3; repetition++)
            foreach (string kind in new[] { "none", "fast", "unread" })
            {
                using var host = new ObservationHost();
                using var ready = new ManualResetEventSlim();
                using var go = new ManualResetEventSlim();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var owner = Task.Run(() =>
                {
                    var options = RuntimeDefaults.FromLegacy(new() { Learning = new() { WarmupSteps = 64, BatchSize = 16, ReplayCapacity = 512 } });
                    using var runtime = new RuntimeSession(options, ControllerRegistry.Create(options));
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    runtime.TrajectorySink = t => hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(t.Action));
                    ready.Set(); go.Wait(timeout.Token);
                    var timer = Stopwatch.StartNew();
                    for (int i = 0; i < 6000; i++) runtime.Step();
                    timer.Stop();
                    string path = Path.GetTempFileName();
                    try { runtime.Controller.Export(path); return new Result(JsonSerializer.Serialize(runtime.Snapshot()), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), Convert.ToHexString(hash.GetHashAndReset()), runtime.Controller.Status.Updates, timer.Elapsed.TotalSeconds, runtime.ObservationFramesCaptured); }
                    finally { File.Delete(path); }
                }, timeout.Token);
                ready.Wait(timeout.Token);
                using var socket = new ClientWebSocket();
                Task? read = null;
                if (kind != "none")
                {
                    using var http = new HttpClient();
                    var sessions = JsonSerializer.Deserialize<SessionDescriptor[]>(await http.GetStringAsync(host.Endpoint + "/sessions", timeout.Token), ObservationProtocol.Json)!;
                    await socket.ConnectAsync(new Uri(host.Endpoint.Replace("http://", "ws://") + "/observe/" + sessions[0].SessionId + "?version=1"), timeout.Token);
                    if (kind == "fast") read = Task.Run(async () => { var buffer = new byte[16384]; try { while (!timeout.IsCancellationRequested) await socket.ReceiveAsync(buffer, timeout.Token); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { } }, timeout.Token);
                }
                go.Set();
                Result result = await owner.WaitAsync(timeout.Token);
                if (kind == "none") Assert.Equal(0, result.Frames);
                else Assert.True(result.Frames > 0);
                if (reference is null) reference = result;
                else { Assert.Equal(reference.World, result.World); Assert.Equal(reference.Weights, result.Weights); Assert.Equal(reference.Actions, result.Actions); Assert.Equal(reference.Updates, result.Updates); }
                rows.Add(new { Repetition = repetition, Client = kind, result.Seconds, StepsPerSecond = 6000 / result.Seconds, result.Actions, result.Weights });
                timeout.Cancel(); socket.Abort(); if (read is not null) await read;
            }
        string output = Path.Combine(RuntimeDefaults.RepositoryRoot(), "artifacts", "observation-learning-check.json");
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
