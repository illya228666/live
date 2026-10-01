using ArtificialLife.Core;
using TorchSharp;
using static TorchSharp.torch;

namespace ArtificialLife.Brain;

/// <summary>Short-lived padded tensors. Masks exclude padding from pooling and action selection.</summary>
internal sealed class TokenBatch
{
    public Tensor ObservationTypes { get; }
    public Tensor ObservationFeatures { get; }
    public Tensor ObservationMask { get; }
    public Tensor ActionTypes { get; }
    public Tensor ActionFeatures { get; }
    public Tensor ActionMask { get; }

    // Все tensors создаются внутри DisposeScope вызывающего кода и живут только один forward/update.
    public TokenBatch(ObservationToken[][] states, ActionCandidate[][] candidates, NetworkOptions options)
    {
        if (states.Length == 0 || states.Length != candidates.Length || states.Any(state => state.Length == 0) || candidates.Any(set => set.Length == 0))
        {
            throw new ArgumentException("Every batch row needs observations and legal actions.");
        }
        int batch = states.Length;
        int tokens = states.Max(state => state.Length);
        int actions = candidates.Max(set => set.Length);
        int width = options.FeatureWidth;
        long[] observationTypes = new long[batch * tokens];
        float[] observationFeatures = new float[batch * tokens * width];
        float[] observationMask = new float[batch * tokens];
        long[] actionTypes = new long[batch * actions];
        float[] actionFeatures = new float[batch * actions * width];
        bool[] actionMask = new bool[batch * actions];
        for (int row = 0; row < batch; row++)
        {
            for (int index = 0; index < states[row].Length; index++)
            {
                ObservationToken token = states[row][index];
                Validate(token.Type, token.Features, options.ObservationCapacity, width);
                observationTypes[row * tokens + index] = token.Type;
                observationMask[row * tokens + index] = 1;
                token.Features.CopyTo(observationFeatures, (row * tokens + index) * width);
            }
            for (int index = 0; index < candidates[row].Length; index++)
            {
                ActionCandidate candidate = candidates[row][index];
                Validate(candidate.Type, candidate.Parameters, options.ActionCapacity, width);
                actionTypes[row * actions + index] = candidate.Type;
                actionMask[row * actions + index] = true;
                candidate.Parameters.CopyTo(actionFeatures, (row * actions + index) * width);
            }
        }
        ObservationTypes = tensor(observationTypes).reshape(batch, tokens);
        ObservationFeatures = tensor(observationFeatures).reshape(batch, tokens, width);
        ObservationMask = tensor(observationMask).reshape(batch, tokens, 1);
        ActionTypes = tensor(actionTypes).reshape(batch, actions);
        ActionFeatures = tensor(actionFeatures).reshape(batch, actions, width);
        ActionMask = tensor(actionMask).reshape(batch, actions);
    }

    private static void Validate(int type, float[] features, int capacity, int width)
    {
        if (type < 0 || type >= capacity || features.Length > width || features.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1))
        {
            throw new ArgumentException("Token type/width out of capacity or features outside [-1, 1].");
        }
    }
}
