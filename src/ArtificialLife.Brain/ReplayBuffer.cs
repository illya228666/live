using ArtificialLife.Core;

namespace ArtificialLife.Brain;

public sealed record Experience(ObservationToken[] State, ActionCandidate Action, float Reward,
    ObservationToken[] NextState, ActionCandidate[] NextActions, bool Terminal, long Revision = 0);

/// <summary>Managed ring buffer; it owns no Torch tensors or native resources.</summary>
public sealed record PrioritySample(Experience Experience, int Slot, float Weight);
public sealed class ReplayBuffer(int capacity, double priorityAlpha = 0)
{
    private readonly Experience?[] entries = new Experience[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int cursor;
    private long latestRevision;
    private int eligibleCount;
    private readonly double[] weighted = CreatePriorities(capacity, priorityAlpha);
    private readonly double[] tree = priorityAlpha > 0 ? new double[capacity + 1] : [];
    private double maximumPriority = 1;
    public int Count { get; private set; }

    public void Add(Experience experience)
    {
        if (experience.Revision < latestRevision) throw new InvalidOperationException("Replay revisions must be monotonic.");
        if (experience.Revision != latestRevision)
        {
            latestRevision = experience.Revision; eligibleCount = 0;
            Array.Clear(weighted); Array.Clear(tree); maximumPriority = 1;
        }
        eligibleCount = Math.Min(eligibleCount + 1, entries.Length);
        // Копии предотвращают изменение истории через массивы текущего состояния.
        entries[cursor] = experience with
        {
            State = Clone(experience.State), NextState = Clone(experience.NextState),
            Action = experience.Action with { Parameters = (float[])experience.Action.Parameters.Clone() },
            NextActions = experience.NextActions.Select(action => action with { Parameters = (float[])action.Parameters.Clone() }).ToArray()
        };
        if (priorityAlpha > 0) SetPriority(cursor, maximumPriority);
        cursor = (cursor + 1) % entries.Length;
        Count = Math.Min(Count + 1, entries.Length);
    }

    public int CountFor(long revision) => revision == latestRevision ? eligibleCount : 0;

    public Experience[] Sample(int count, Random random, long? revision = null)
    {
        if (Count == 0 || count < 1)
        {
            throw new InvalidOperationException("Cannot sample an empty replay buffer.");
        }
        var batch = new Experience[count];
        int available = revision.HasValue ? CountFor(revision.Value) : Count;
        if (available == 0) throw new InvalidOperationException("No experience for this revision.");
        for (int index = 0; index < count; index++)
        {
            int slot = revision.HasValue ? (cursor - 1 - random.Next(available) + entries.Length) % entries.Length : random.Next(Count);
            batch[index] = entries[slot]!;
        }
        return batch;
    }

    public PrioritySample[] SamplePrioritized(int count, Random random, long revision, double beta)
    {
        if (!double.IsFinite(beta) || beta is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(beta));
        if (priorityAlpha <= 0 || CountFor(revision) == 0 || count < 1) throw new InvalidOperationException("No prioritized experience for this revision.");
        double total = 0;
        for (int index = entries.Length; index > 0; index -= index & -index) total += tree[index];
        var result = new PrioritySample[count];
        float maximumWeight = 0;
        for (int i = 0; i < count; i++)
        {
            double target = random.NextDouble() * total;
            int slot = 0, bit = 1;
            while (bit < tree.Length) bit <<= 1;
            for (bit >>= 1; bit > 0; bit >>= 1)
            {
                int next = slot + bit;
                if (next < tree.Length && tree[next] <= target) { slot = next; target -= tree[next]; }
            }
            slot = Math.Min(slot, entries.Length - 1);
            float weight = (float)Math.Pow(eligibleCount * weighted[slot] / total, -beta);
            maximumWeight = Math.Max(maximumWeight, weight);
            result[i] = new(entries[slot]!, slot, weight);
        }
        return result.Select(s => s with { Weight = s.Weight / maximumWeight }).ToArray();
    }
    public void UpdatePriority(int slot, double error)
    {
        if (priorityAlpha <= 0 || !double.IsFinite(error) || slot < 0 || slot >= entries.Length || entries[slot]?.Revision != latestRevision)
            throw new ArgumentException("Invalid replay priority update.");
        double priority = Math.Abs(error) + 1e-5;
        maximumPriority = Math.Max(maximumPriority, priority);
        SetPriority(slot, priority);
    }
    private void SetPriority(int slot, double priority)
    {
        double value = Math.Pow(priority, priorityAlpha), delta = value - weighted[slot];
        weighted[slot] = value;
        for (int index = slot + 1; index < tree.Length; index += index & -index) tree[index] += delta;
    }
    private static double[] CreatePriorities(int capacity, double alpha)
    {
        if (!double.IsFinite(alpha) || alpha is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        return alpha > 0 ? new double[capacity] : [];
    }

    private static ObservationToken[] Clone(ObservationToken[] tokens) => tokens.Select(token => token with { Features = (float[])token.Features.Clone() }).ToArray();
}
