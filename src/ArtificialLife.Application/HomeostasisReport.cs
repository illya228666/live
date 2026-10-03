using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Copied, immutable keyed values with structural equality for snapshots and histories.</summary>
public sealed class ReportValues<T>(IEnumerable<KeyValuePair<string, T>> values) : IReadOnlyDictionary<string, T>, IEquatable<ReportValues<T>>
{
    private readonly Dictionary<string, T> items = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    public T this[string key] => items[key];
    public IEnumerable<string> Keys => items.Keys;
    public IEnumerable<T> Values => items.Values;
    public int Count => items.Count;
    public bool ContainsKey(string key) => items.ContainsKey(key);
    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value) => items.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(ReportValues<T>? other) => other is not null && Count == other.Count &&
        items.All(pair => other.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(pair.Value, value));
    public override bool Equals(object? obj) => obj is ReportValues<T> other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var pair in items.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(pair.Key); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }
}

public sealed record DriveReport(string Key, double Current, double Target, double NormalizedError,
    double Satisfaction, double Weight, DriveDescriptor? Descriptor)
{
    public string Name => Descriptor?.Name ?? Key;
    public string Unit => Descriptor?.Unit ?? "";
}

/// <summary>Current organism state, independent of transition reward.</summary>
public sealed record HomeostasisReport(double Balance, ReportValues<DriveReport> Drives)
{
    public static HomeostasisReport Measure(IEnumerable<DriveState> drives, IEnumerable<DriveDescriptor>? descriptors = null)
    {
        var metadata = (descriptors ?? []).ToDictionary(item => item.Key, StringComparer.Ordinal);
        var reports = drives.Select(drive =>
        {
            if (!double.IsFinite(drive.Current) || !double.IsFinite(drive.Target) || !double.IsFinite(drive.Scale) ||
                drive.Scale <= 0 || !double.IsFinite(drive.Weight) || drive.Weight < 0)
                throw new ArgumentException($"Invalid drive: {drive.Key}");
            double error = Math.Abs(drive.Current - drive.Target) / drive.Scale;
            return new DriveReport(drive.Key, drive.Current, drive.Target, error, Math.Clamp(1 - error, 0, 1),
                drive.Weight, metadata.GetValueOrDefault(drive.Key));
        }).ToArray();
        double weights = reports.Sum(drive => drive.Weight);
        double balance = weights == 0 ? 1 : reports.Sum(drive => drive.Satisfaction * drive.Weight) / weights;
        return new HomeostasisReport(balance, new(reports.Select(drive => KeyValuePair.Create(drive.Key, drive))));
    }

    public static HomeostasisReport Capture(Simulation simulation) => Measure(simulation.GetDrives(), simulation.GetDriveDescriptors());
}
