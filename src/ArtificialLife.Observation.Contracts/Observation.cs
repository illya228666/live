using ArtificialLife.Core;
using System.Text.Json;

namespace ArtificialLife.Observation;

public static class ObservationProtocol
{
    public const int Version = 1;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
    public static string Registry => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Live", "sessions");
}
public sealed record SessionDescriptor(string RunId, string SessionId, string WorldId, string Epoch, string Mode, string Status, string Endpoint, string? ParentId, string? TrialId = null);
public sealed record HostDescriptor(int ProcessId, long ProcessStartTicks, string Endpoint, DateTimeOffset Heartbeat);
public sealed record ResourceFile(string Path, string Hash, string Url);
public sealed record VisualPackage(ModuleManifest Manifest, ResourceFile[] Files);
public sealed record ObservationState(RuntimeFrame Frame, ModuleManifest[] Packages, VisualPackage[] Visuals,
    ControllerStatus? Controller, Dictionary<string, double> Metrics, string[] Capabilities, double Speed, bool Paused, string? Fault);
public sealed record ObservationMessage(int Version, string Kind, SessionDescriptor Session, long Sequence, long MetadataRevision,
    ObservationState? State = null, string? Event = null);
public sealed record ObservationCommand(string Id, string Epoch, string Operation, string? Value = null);
public sealed record CommandResult(string Id, bool Accepted, string Message);
public sealed record CaptureMetadata(long Age, long Revision, string SessionId, string Epoch, long Sequence);
