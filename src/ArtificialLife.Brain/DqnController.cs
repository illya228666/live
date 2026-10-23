using ArtificialLife.Core;
using System.Text.Json;

namespace ArtificialLife.Brain;

/// <summary>Algorithm-specific state stays behind the controller boundary.</summary>
public sealed class DqnController : IAgentController
{
    public const string Architecture = "deep-sets-double-dqn.v1";
    private readonly DqnBrain brain;
    private readonly LearningOptions learning;
    private long steps;
    private long changedAt = -1;
    private int observationTypes;
    private readonly Dictionary<int, long> introducedAt = [];
    public ControllerLimits Limits { get; }
    public DqnBrain Brain => brain;
    public JsonElement InferenceState => JsonSerializer.SerializeToElement(brain.ObservationWeights);
    public Random? EvaluationRandom { get; set; }
    public double Epsilon => Math.Max(brain.Epsilon(steps), changedAt < 0 ? 0 :
        learning.AdaptationEpsilon * Math.Max(0, 1 - (double)(steps - changedAt) / learning.AdaptationSteps));
    public ControllerStatus Status => new(steps, brain.OptimizationUpdates, Epsilon, brain.LastLoss,
        brain.Replay.Count, brain.Replay.CountFor(brain.Revision));
    public DqnController(NetworkOptions network, LearningOptions learning)
    {
        this.learning = learning;
        Limits = new(network.ObservationCapacity, network.ActionCapacity, network.FeatureWidth) { MinimumObservations = 1 };
        brain = new(network, learning);
    }
    public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
    {
        if (learning) IntroduceObservations();
        return !learning && EvaluationRandom is not null ? actions[EvaluationRandom.Next(actions.Length)] :
            learning ? brain.Choose(state, actions, Epsilon) : brain.ChooseGreedy(state, actions);
    }
    public void Receive(AgentTransition transition)
    {
        if (transition.Revision != brain.Revision) throw new InvalidOperationException("Stale training transition.");
        brain.Replay.Add(new(transition.State, transition.Action, (float)transition.Reward,
            transition.NextState, transition.NextActions, transition.Terminal, transition.Revision));
        ++steps;
        IntroduceObservations();
        brain.Learn(steps);
    }
    public void ConditionsChanged(long revision)
    {
        if (revision < brain.Revision) throw new InvalidOperationException("Conditions cannot go backwards.");
        if (revision != brain.Revision && steps > 0) changedAt = steps;
        brain.Revision = revision;
    }
    public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions)
    {
        if (observationTypes > 0 && steps > 0)
            for (int type = observationTypes; type < observations.Count; type++)
            {
                introducedAt[type] = steps + learning.WarmupSteps;
                brain.ObservationWeights[type] = 0;
            }
        observationTypes = observations.Count;
    }
    private void IntroduceObservations()
    {
        foreach (var pair in introducedAt)
            brain.ObservationWeights[pair.Key] = (float)Math.Clamp((double)(steps - pair.Value) / learning.ObservationIntroductionSteps, 0, 1);
    }
    public void ImportInferenceState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array) return;
        var weights = state.Deserialize<float[]>()!;
        if (weights.Length != brain.ObservationWeights.Length || weights.Any(w => !float.IsFinite(w) || w is < 0 or > 1))
            throw new InvalidDataException("Invalid observation introduction state.");
        weights.CopyTo(brain.ObservationWeights, 0);
        for (int type = 0; type < weights.Length; type++)
            if (weights[type] < 1) introducedAt[type] = steps + learning.WarmupSteps - (long)(weights[type] * learning.ObservationIntroductionSteps);
    }
    public void Export(string weightsPath) => brain.Save(weightsPath);
    public void Import(string weightsPath) => brain.Load(weightsPath);
    public IAgentController CreateInferenceController() => new FrozenInference(this);
    public void Dispose() => brain.Dispose();
    private sealed class FrozenInference(DqnController source) : IAgentController
    {
        public ControllerLimits Limits => source.Limits;
        public ControllerStatus Status => source.Status with { Epsilon = 0 };
        public JsonElement InferenceState => source.InferenceState;
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => source.brain.ChooseGreedy(state, actions);
        public void Receive(AgentTransition transition) => throw new InvalidOperationException("Frozen controller.");
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => source.Export(path);
        public void Import(string path) => throw new InvalidOperationException("Frozen controller.");
        public IAgentController CreateInferenceController() => new FrozenInference(source);
        public void Dispose() { }
    }
}
