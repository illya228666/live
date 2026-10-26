using ArtificialLife.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace ArtificialLife.Brain;

/// <summary>CPU Double DQN. Public boundaries contain managed data only.</summary>
public sealed class DqnBrain : IDisposable
{
    private readonly CandidateQNetwork online;
    private readonly CandidateQNetwork target;
    private readonly torch.optim.Optimizer optimizer;
    private readonly NetworkOptions networkOptions;
    private readonly LearningOptions learning;
    private readonly Random random;
    private long updates;
    private bool disposed;

    public ReplayBuffer Replay { get; }
    public PersistentExploration Exploration { get; }
    public double LastLoss { get; private set; }
    /// <summary>Number of completed optimizer steps, independent of world-step count.</summary>
    public long OptimizationUpdates => updates;
    public long Revision { get; set; }
    public float[] ObservationWeights { get; }

    public DqnBrain(NetworkOptions networkOptions, LearningOptions learning)
    {
        networkOptions.Validate();
        learning.Validate();
        this.networkOptions = networkOptions;
        this.learning = learning;
        ObservationWeights = Enumerable.Repeat(1f, networkOptions.ObservationCapacity).ToArray();
        random = new Random(learning.Seed);
        Exploration = new(random, learning.ExplorationMaxDuration);
        // Один поток уменьшает накладные расходы крошечной модели и разброс CPU-результатов.
        int threads = int.TryParse(Environment.GetEnvironmentVariable("AL_COMPUTE_THREADS"), out int configured) && configured > 0 ? configured : 1;
        set_num_threads(threads);
        manual_seed(learning.Seed);
        online = new CandidateQNetwork(networkOptions);
        target = new CandidateQNetwork(networkOptions);
        SynchronizeTarget();
        optimizer = optim.Adam(online.parameters(), lr: learning.LearningRate);
        Replay = new ReplayBuffer(learning.ReplayCapacity, learning.PrioritizedReplayAlpha);
    }

    public double Epsilon(long step) => learning.EpsilonEnd + (learning.EpsilonStart - learning.EpsilonEnd) * Math.Max(0, 1 - (double)step / learning.EpsilonDecaySteps);

    public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, double epsilon = 0)
    {
        if (actions.Length == 0)
        {
            throw new ArgumentException("At least one legal action is required.");
        }
        return Exploration.Choose(actions, epsilon, () => ChooseGreedy(state, actions));
    }

    /// <summary>Frozen-policy selection without consuming the training exploration/replay RNG.</summary>
    public ActionCandidate ChooseGreedy(ObservationToken[] state, ActionCandidate[] actions)
    {
        if (actions.Length == 0)
        {
            throw new ArgumentException("At least one legal action is required.");
        }
        float[] scores = Scores(state, actions);
        int best = 0;
        for (int index = 1; index < scores.Length; index++)
        {
            if (scores[index] > scores[best])
            {
                best = index;
            }
        }
        return actions[best];
    }

    public float[] Scores(ObservationToken[] state, ActionCandidate[] actions)
    {
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        var batch = new TokenBatch([state], [actions], networkOptions, ObservationWeights);
        return online.Score(batch).data<float>().ToArray();
    }

    public float[] Encode(ObservationToken[] state)
    {
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        var batch = new TokenBatch([state], [[new ActionCandidate(0, [])]], networkOptions, ObservationWeights);
        return online.Encode(batch).data<float>().ToArray();
    }

    public bool Learn(long step)
    {
        if (Replay.CountFor(Revision) < learning.WarmupSteps || step % learning.TrainEvery != 0)
        {
            return false;
        }
        using var scope = NewDisposeScope();
        PrioritySample[]? prioritized = learning.PrioritizedReplayAlpha > 0 ? Replay.SamplePrioritized(learning.BatchSize, random, Revision, learning.PrioritizedReplayBeta) : null;
        Experience[] samples = prioritized?.Select(s => s.Experience).ToArray() ?? Replay.Sample(learning.BatchSize, random, Revision == 0 ? null : Revision);
        var current = new TokenBatch(samples.Select(sample => sample.State).ToArray(),
            samples.Select(sample => new[] { sample.Action }).ToArray(), networkOptions, ObservationWeights);
        var next = new TokenBatch(samples.Select(sample => sample.NextState).ToArray(),
            samples.Select(sample => sample.NextActions).ToArray(), networkOptions, ObservationWeights);
        Tensor predicted = online.Score(current).squeeze(1);
        Tensor expected;
        using (no_grad())
        {
            // Double DQN: online выбирает действие, target оценивает его; терминальные переходы без bootstrap.
            Tensor best = online.Score(next).argmax(1, keepdim: true);
            Tensor nextValue = target.Score(next).gather(1, best).squeeze(1);
            Tensor rewards = tensor(samples.Select(sample => sample.Reward).ToArray());
            Tensor continuation = tensor(samples.Select(sample => sample.Terminal ? 0f : 1f).ToArray());
            expected = rewards + learning.Gamma * nextValue * continuation;
        }
        Tensor loss;
        if (prioritized is null) loss = nn.functional.smooth_l1_loss(predicted, expected);
        else
        {
            Tensor difference = predicted - expected;
            Tensor absolute = difference.abs();
            Tensor perSample = where(absolute.lt(1), difference.square() * 0.5, absolute - 0.5);
            loss = (perSample * tensor(prioritized.Select(s => s.Weight).ToArray())).mean();
            float[] errors = absolute.detach().data<float>().ToArray();
            for (int i = 0; i < errors.Length; i++) Replay.UpdatePriority(prioritized[i].Slot, errors[i]);
        }
        optimizer.zero_grad();
        loss.backward();
        nn.utils.clip_grad_norm_(online.parameters(), learning.GradientClip);
        optimizer.step();
        LastLoss = loss.item<float>();
        if (!double.IsFinite(LastLoss)) throw new InvalidOperationException("DQN produced a non-finite loss.");
        updates++;
        if (updates % learning.TargetSyncInterval == 0)
        {
            SynchronizeTarget();
        }
        return true;
    }

    public void Save(string path) => online.save(path);
    public void Load(string path)
    {
        online.load(path);
        SynchronizeTarget();
    }

    private void SynchronizeTarget()
    {
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        target.load_state_dict(online.state_dict());
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        // Optimizer хранит ссылки на параметры, поэтому освобождается до сетей.
        optimizer.Dispose();
        target.Dispose();
        online.Dispose();
        disposed = true;
    }
}
