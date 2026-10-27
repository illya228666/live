using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Brain;

public sealed record GenericActorCriticOptions
{
    public int Features { get; init; } = 64;
    public double ActorStep { get; init; } = 0.03;
    public double CriticStep { get; init; } = 0.05;
    public double Gamma { get; init; } = 0.97;
    public double Lambda { get; init; } = 0.7;
    public double ExplorationFraction { get; init; } = 0.1;
    public string Exploration { get; init; } = "uniform";
    public int CountCapacity { get; init; } = 16384;
    public long FreezeAfterSteps { get; init; } = -1;
    public void Validate()
    {
        if (Features is < 8 or > 512 || !double.IsFinite(ActorStep) || ActorStep is <= 0 or > 1 ||
            !double.IsFinite(CriticStep) || CriticStep is <= 0 or > 1 || !double.IsFinite(Gamma) || Gamma is < 0 or >= 1 ||
            !double.IsFinite(Lambda) || Lambda is < 0 or > 1 || !double.IsFinite(ExplorationFraction) ||
            ExplorationFraction is < 0 or > 1 || Exploration is not ("uniform" or "coverage") ||
            CountCapacity is < 1 or > 1000000 || FreezeAfterSteps < -1)
            throw new InvalidDataException("Invalid generic actor-critic options.");
    }
}

/// <summary>
/// Linear softmax actor and TD critic over fixed, typed random features. No world names,
/// motor geometry, replay, neural optimizer or mechanic-specific prediction models.
/// Coverage is a separate action-selection mechanism, not a bonus from the world.
/// </summary>
public sealed class GenericActorCriticController : IAgentController
{
    public const string Architecture = "generic-random-features-actor-critic.v1";
    private readonly GenericActorCriticOptions options;
    private readonly Random random;
    private readonly Dictionary<(int Type, bool Action), double[]> bases = [];
    private readonly Dictionary<ulong, int> visits = [];
    private readonly Queue<ulong> order = [];
    private readonly bool inferenceOnly;
    private double[] actor, critic, trace;
    private long steps, updates, revision, exploratoryChoices;
    private double loss;
    private Pending? pending;
    private sealed record Pending(double[] State, double[][] Candidates, double[] Policy,
        double[] Behavior, int Selected, ActionCandidate Action, ulong Context);
    public ControllerLimits Limits => new(256, 256, 4);
    public ControllerStatus Status => new(steps, updates, inferenceOnly ? 0 : options.ExplorationFraction, loss, 0, 0);
    public JsonElement InferenceState => JsonSerializer.SerializeToElement(new
    { steps, updates, loss, exploratoryChoices, coverageEntries = visits.Count, revision });

    public GenericActorCriticController(GenericActorCriticOptions options, int seed) : this(options, seed, false) { }
    private GenericActorCriticController(GenericActorCriticOptions options, int seed, bool inferenceOnly)
    {
        options.Validate(); this.options = options; random = new(seed); this.inferenceOnly = inferenceOnly;
        actor = new double[options.Features * 4 + 1];
        critic = new double[options.Features * 2 + 2]; trace = new double[critic.Length];
    }

    public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions)
    {
        if (observations.Count > Limits.ObservationTypes || actions.Count > Limits.ActionTypes)
            throw new InvalidDataException("Controller vocabulary capacity exceeded.");
        // Keys are opaque identities. Existing feature coordinates and learned weights never move.
    }
    public void ConditionsChanged(long value)
    {
        if (value != revision) { Array.Clear(trace); pending = null; }
        revision = value;
    }

    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
    private double[] Basis(int type, bool action)
    {
        if (type is < 0 or >= 256) throw new InvalidDataException("Invalid type slot.");
        if (bases.TryGetValue((type, action), out var cached)) return cached;
        var values = new double[options.Features * 15];
        ulong seed = (ulong)(type + 1) * 7919 + (action ? 104729UL : 13007UL);
        for (int i = 0; i < values.Length; i++) values[i] = (Mix(seed + (ulong)i * 65537) % 20001) / 10000.0 - 1;
        bases[(type, action)] = values;
        return values;
    }
    private double[] EncodeToken(int type, float[] features, bool action)
    {
        if (features.Length > 4 || features.Any(f => !float.IsFinite(f) || f is < -1 or > 1))
            throw new InvalidDataException("Invalid normalized feature.");
        var coefficients = Basis(type, action); var values = new double[options.Features];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = i * 15;
            double value = coefficients[offset];
            for (int j = 0; j < features.Length; j++)
            {
                value += coefficients[offset + 1 + j] * features[j];
                value += coefficients[offset + 5 + j] * features[j] * features[j];
            }
            int pair = 9;
            for (int j = 0; j < 4; j++) for (int k = j + 1; k < 4; k++, pair++)
                if (k < features.Length) value += coefficients[offset + pair] * features[j] * features[k];
            values[i] = Math.Tanh(value);
        }
        return values;
    }
    private double[] Encode(ObservationToken[] state)
    {
        int n = options.Features; double scale = 1 / Math.Sqrt(n);
        var result = new double[critic.Length]; result[0] = 1;
        result[1] = Math.Log(1 + state.Length) / 10;
        // Stable summation order: token permutation is not a behavioral input.
        foreach (var token in state.OrderBy(t => t.Type).ThenBy(t => string.Join(',', t.Features.Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))))
        {
            var value = EncodeToken(token.Type, token.Features, false);
            for (int i = 0; i < n; i++)
            {
                result[2 + i] += value[i] / Math.Max(1, state.Length) * scale;
                if (state.Length > 0) result[2 + n + i] = Math.Max(result[2 + n + i], Math.Abs(value[i]) * scale);
            }
        }
        return result;
    }
    private double[] Candidate(double[] state, ActionCandidate action)
    {
        int n = options.Features; double scale = 1 / Math.Sqrt(n);
        var a = EncodeToken(action.Type, action.Parameters, true); var result = new double[actor.Length]; result[0] = 1;
        for (int i = 0; i < n; i++)
        {
            double s = state[2 + i] / scale;
            result[1 + i] = a[i] * scale;
            result[1 + n + i] = s * a[i] * scale;
            result[1 + 2 * n + i] = Math.Tanh(s + a[i]) * scale;
            result[1 + 3 * n + i] = state[2 + n + i] * a[i];
        }
        return result;
    }
    private static double Dot(double[] a, double[] b)
    { double value = 0; for (int i = 0; i < a.Length; i++) value += a[i] * b[i]; return value; }
    private double[] Policy(double[][] features)
    {
        var values = features.Select(f => Dot(actor, f)).ToArray(); double maximum = values.Max(), sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i] = Math.Exp(values[i] - maximum);
        for (int i = 0; i < values.Length; i++) values[i] /= sum;
        return values;
    }
    private static ulong TokenKey(int type, float[] features)
    {
        ulong key = Mix((ulong)(type + 1));
        for (int i = 0; i < features.Length; i++)
            key = Mix(key ^ ((ulong)(Math.Clamp((int)Math.Floor((features[i] + 1) * 4), 0, 7) + 1) << (i * 8)));
        return key;
    }
    private static ulong Context(ObservationToken[] state)
    {
        ulong sum = 0; foreach (var token in state) sum = unchecked(sum + TokenKey(token.Type, token.Features));
        return Mix(sum ^ (ulong)state.Length);
    }
    private static ulong VisitKey(ulong context, ActionCandidate action) => Mix(context ^ TokenKey(action.Type, action.Parameters));
    private int Sample(double[] distribution)
    {
        double draw = random.NextDouble();
        for (int i = 0; i < distribution.Length; i++) if ((draw -= distribution[i]) <= 0) return i;
        return distribution.Length - 1;
    }
    public double[] ActionProbabilities(ObservationToken[] state, ActionCandidate[] actions)
    {
        if (actions.Length == 0) throw new InvalidDataException("No legal candidates.");
        var encoded = Encode(state); return Policy(actions.Select(a => Candidate(encoded, a)).ToArray());
    }
    public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
    {
        if (actions.Length == 0) throw new InvalidDataException("No legal candidates.");
        var encoded = Encode(state); var candidates = actions.Select(a => Candidate(encoded, a)).ToArray();
        var policy = Policy(candidates);
        if (!learning || inferenceOnly) return actions[Array.IndexOf(policy, policy.Max())];
        ulong context = Context(state);
        int[] counts = actions.Select(a => options.Exploration == "coverage" ? visits.GetValueOrDefault(VisitKey(context, a)) : 0).ToArray();
        int minimum = counts.Min(), ties = counts.Count(c => c == minimum);
        double[] exploration = counts.Select(c => c == minimum ? 1.0 / ties : 0).ToArray();
        double e = options.ExplorationFraction;
        var behavior = policy.Select((p, i) => (1 - e) * p + e * exploration[i]).ToArray();
        bool explore = random.NextDouble() < e;
        if (explore) exploratoryChoices++;
        int selected = Sample(explore ? exploration : policy);
        pending = new(encoded, candidates, policy, behavior, selected,
            actions[selected] with { Parameters = (float[])actions[selected].Parameters.Clone() }, context);
        return actions[selected];
    }
    public void Receive(AgentTransition transition)
    {
        if (inferenceOnly) throw new InvalidOperationException("Inference controller cannot learn.");
        if (transition.Revision != revision || pending is null || transition.Action.Type != pending.Action.Type ||
            !transition.Action.Parameters.SequenceEqual(pending.Action.Parameters)) throw new InvalidOperationException("Unmatched actor-critic transition.");
        var choice = pending; pending = null;
        ulong key = VisitKey(choice.Context, transition.Action);
        if (!visits.ContainsKey(key))
        {
            if (visits.Count >= options.CountCapacity) visits.Remove(order.Dequeue());
            visits[key] = 0; order.Enqueue(key);
        }
        visits[key] = Math.Min(int.MaxValue - 1, visits[key]) + 1;
        double value = Dot(critic, choice.State), next = transition.Terminal ? 0 : Dot(critic, Encode(transition.NextState));
        double delta = transition.Reward + options.Gamma * next - value;
        if (!double.IsFinite(delta)) throw new InvalidOperationException("Non-finite actor-critic error.");
        loss = delta * delta;
        if (options.FreezeAfterSteps < 0 || steps < options.FreezeAfterSteps)
        {
            // The critic evaluates the behavior mixture. Only its policy-dependent part
            // contributes to the actor gradient: d log(mu) = (1-epsilon)*pi/mu*d log(pi).
            double correction = (1 - options.ExplorationFraction) * choice.Policy[choice.Selected] / choice.Behavior[choice.Selected];
            double boundedError = Math.Clamp(delta, -5, 5);
            for (int i = 0; i < trace.Length; i++) trace[i] = options.Gamma * options.Lambda * trace[i] + choice.State[i];
            double criticStep = options.CriticStep * boundedError / Math.Max(1, Dot(trace, trace));
            for (int i = 0; i < critic.Length; i++) critic[i] += criticStep * trace[i];
            var gradient = new double[actor.Length];
            for (int i = 0; i < gradient.Length; i++)
            {
                gradient[i] = choice.Candidates[choice.Selected][i];
                for (int a = 0; a < choice.Candidates.Length; a++) gradient[i] -= choice.Policy[a] * choice.Candidates[a][i];
            }
            double actorStep = options.ActorStep * correction * boundedError / Math.Max(1, Dot(gradient, gradient));
            for (int i = 0; i < actor.Length; i++) actor[i] += actorStep * gradient[i];
            updates++;
        }
        steps++;
        if (transition.Terminal) Array.Clear(trace);
    }
    public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new Weights(actor, critic)));
    public void Import(string path)
    {
        var saved = JsonSerializer.Deserialize<Weights>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing actor-critic weights.");
        if (saved.Actor.Length != actor.Length || saved.Critic.Length != critic.Length || saved.Actor.Concat(saved.Critic).Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Incompatible actor-critic weights.");
        actor = saved.Actor; critic = saved.Critic; Array.Clear(trace); pending = null;
    }
    public void ImportInferenceState(JsonElement state)
    {
        if (state.TryGetProperty("steps", out var s)) steps = s.GetInt64();
        if (state.TryGetProperty("updates", out var u)) updates = u.GetInt64();
        if (state.TryGetProperty("loss", out var l)) loss = l.GetDouble();
    }
    public IAgentController CreateInferenceController() => new GenericActorCriticController(options, 0, true)
    { actor = (double[])actor.Clone(), critic = (double[])critic.Clone(), steps = steps, updates = updates, loss = loss, revision = revision };
    public void Dispose() { }
    public sealed record Weights(double[] Actor, double[] Critic);
}
