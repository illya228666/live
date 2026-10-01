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
    private int updates;
    private bool disposed;

    public ReplayBuffer Replay { get; }
    public double LastLoss { get; private set; }

    public DqnBrain(NetworkOptions networkOptions, LearningOptions learning)
    {
        networkOptions.Validate();
        learning.Validate();
        this.networkOptions = networkOptions;
        this.learning = learning;
        random = new Random(learning.Seed);
        // Один поток уменьшает накладные расходы крошечной модели и разброс CPU-результатов.
        set_num_threads(1);
        manual_seed(learning.Seed);
        online = new CandidateQNetwork(networkOptions);
        target = new CandidateQNetwork(networkOptions);
        SynchronizeTarget();
        optimizer = optim.Adam(online.parameters(), lr: learning.LearningRate);
        Replay = new ReplayBuffer(learning.ReplayCapacity);
    }

    public double Epsilon(int step) => learning.EpsilonEnd + (learning.EpsilonStart - learning.EpsilonEnd) * Math.Max(0, 1 - (double)step / learning.EpsilonDecaySteps);

    public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, double epsilon = 0)
    {
        if (actions.Length == 0)
        {
            throw new ArgumentException("At least one legal action is required.");
        }
        if (random.NextDouble() < epsilon)
        {
            return actions[random.Next(actions.Length)];
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
        var batch = new TokenBatch([state], [actions], networkOptions);
        return online.Score(batch).data<float>().ToArray();
    }

    public float[] Encode(ObservationToken[] state)
    {
        using var scope = NewDisposeScope();
        using var noGrad = no_grad();
        var batch = new TokenBatch([state], [[new ActionCandidate(0, [])]], networkOptions);
        return online.Encode(batch).data<float>().ToArray();
    }

    public bool Learn(int step)
    {
        if (Replay.Count < learning.WarmupSteps || step % learning.TrainEvery != 0)
        {
            return false;
        }
        using var scope = NewDisposeScope();
        Experience[] samples = Replay.Sample(learning.BatchSize, random);
        var current = new TokenBatch(samples.Select(sample => sample.State).ToArray(),
            samples.Select(sample => new[] { sample.Action }).ToArray(), networkOptions);
        var next = new TokenBatch(samples.Select(sample => sample.NextState).ToArray(),
            samples.Select(sample => sample.NextActions).ToArray(), networkOptions);
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
        Tensor loss = nn.functional.smooth_l1_loss(predicted, expected);
        optimizer.zero_grad();
        loss.backward();
        nn.utils.clip_grad_norm_(online.parameters(), learning.GradientClip);
        optimizer.step();
        LastLoss = loss.item<float>();
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
