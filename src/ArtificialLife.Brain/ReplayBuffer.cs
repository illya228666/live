using ArtificialLife.Core;

namespace ArtificialLife.Brain;

public sealed record Experience(ObservationToken[] State, ActionCandidate Action, float Reward,
    ObservationToken[] NextState, ActionCandidate[] NextActions, bool Terminal);

/// <summary>Managed ring buffer; it owns no Torch tensors or native resources.</summary>
public sealed class ReplayBuffer(int capacity)
{
    private readonly Experience?[] entries = new Experience[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int cursor;
    public int Count { get; private set; }

    public void Add(Experience experience)
    {
        // Копии предотвращают изменение истории через массивы текущего состояния.
        entries[cursor] = experience with
        {
            State = Clone(experience.State), NextState = Clone(experience.NextState),
            Action = experience.Action with { Parameters = (float[])experience.Action.Parameters.Clone() },
            NextActions = experience.NextActions.Select(action => action with { Parameters = (float[])action.Parameters.Clone() }).ToArray()
        };
        cursor = (cursor + 1) % entries.Length;
        Count = Math.Min(Count + 1, entries.Length);
    }

    public Experience[] Sample(int count, Random random)
    {
        if (Count == 0 || count < 1)
        {
            throw new InvalidOperationException("Cannot sample an empty replay buffer.");
        }
        var batch = new Experience[count];
        for (int index = 0; index < count; index++)
        {
            batch[index] = entries[random.Next(Count)]!;
        }
        return batch;
    }

    private static ObservationToken[] Clone(ObservationToken[] tokens) => tokens.Select(token => token with { Features = (float[])token.Features.Clone() }).ToArray();
}
