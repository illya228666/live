using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace ArtificialLife.Brain;

/// <summary>Trainable Deep Sets state encoder and shared scalar action scorer.</summary>
internal sealed class CandidateQNetwork : Module
{
    private readonly Module<Tensor, Tensor> observationEmbedding;
    private readonly Module<Tensor, Tensor> actionEmbedding;
    private readonly Module<Tensor, Tensor> tokenEncoder;
    private readonly Module<Tensor, Tensor> scorer;

    public CandidateQNetwork(NetworkOptions options) : base("CandidateQNetwork")
    {
        observationEmbedding = Embedding(options.ObservationCapacity, options.TypeEmbeddingSize);
        actionEmbedding = Embedding(options.ActionCapacity, options.TypeEmbeddingSize);
        tokenEncoder = Sequential(
            Linear(options.TypeEmbeddingSize + options.FeatureWidth, options.StateSize), Tanh(),
            Linear(options.StateSize, options.StateSize), Tanh());
        scorer = Sequential(
            Linear(options.StateSize + options.TypeEmbeddingSize + options.FeatureWidth, options.HiddenSize), ReLU(),
            Linear(options.HiddenSize, options.HiddenSize), ReLU(), Linear(options.HiddenSize, 1));
        RegisterComponents();
    }

    public Tensor Encode(TokenBatch batch)
    {
        // [B,T,E+F] -> [B,T,S] -> masked mean [B,S]; число токенов T не меняет веса.
        Tensor embedded = observationEmbedding.forward(batch.ObservationTypes);
        Tensor encoded = tokenEncoder.forward(cat([embedded, batch.ObservationFeatures], dim: -1));
        return (encoded * batch.ObservationMask).sum(1) / batch.ObservationMask.sum(1);
    }

    public Tensor Score(TokenBatch batch)
    {
        Tensor state = Encode(batch);
        long actionCount = batch.ActionTypes.shape[1];
        Tensor repeatedState = state.unsqueeze(1).expand(-1, actionCount, -1);
        Tensor action = actionEmbedding.forward(batch.ActionTypes);
        // [B,A,S+E+F] -> [B,A,1] -> [B,A]. Единственный выход на каждый кандидат.
        return scorer.forward(cat([repeatedState, action, batch.ActionFeatures], dim: -1)).squeeze(-1)
            .masked_fill(batch.ActionMask.logical_not(), -1e9);
    }
}
