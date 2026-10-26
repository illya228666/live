using System.Text.Json;
using ArtificialLife.Core;

namespace ArtificialLife.Brain;

public sealed record RelationalSarsaOptions
{
    public double Alpha { get; init; } = 0.03;
    public double Gamma { get; init; } = 0.97;
    public double Lambda { get; init; } = 0.9;
    public double EpsilonStart { get; init; } = 1;
    public double EpsilonEnd { get; init; } = 0.05;
    public int EpsilonDecaySteps { get; init; } = 50000;
    public double MovementScale { get; init; } = 1.5;
    public string Normalization { get; init; } = "l2";
    public bool UseThermalModel { get; init; }
    public bool LearnThermalModel { get; init; } = true;
    public int WarmupSteps { get; init; }
    public double SensorDistance { get; init; } = 3;
    public int ExplorationMaxDuration { get; init; } = 1;
    public void Validate()
    {
        if (!double.IsFinite(Alpha) || Alpha <= 0 || Alpha > 1 || !double.IsFinite(Gamma) || Gamma is < 0 or > 1 ||
            !double.IsFinite(Lambda) || Lambda is < 0 or > 1 || !double.IsFinite(EpsilonStart) || !double.IsFinite(EpsilonEnd) ||
            EpsilonEnd is < 0 or > 1 || EpsilonStart < EpsilonEnd || EpsilonStart > 1 || EpsilonDecaySteps < 1 ||
            !double.IsFinite(MovementScale) || MovementScale <= 0 || Normalization is not ("l2" or "fixed") ||
            WarmupSteps < 0 || UseThermalModel && LearnThermalModel && WarmupSteps == 0 ||
            !double.IsFinite(SensorDistance) || SensorDistance <= 0 || ExplorationMaxDuration is < 1 or > 10000) throw new InvalidDataException("Invalid relational Sarsa options.");
    }
}

/// <summary>Linear action values with generic visual distance kernels and true-online Dutch traces.</summary>
public sealed class RelationalSarsaController : IAgentController
{
    public const string Architecture = "relational-true-online-sarsa.v1";
    private readonly RelationalSarsaOptions options;
    private readonly Random random;
    private readonly PersistentExploration explorer;
    private readonly Dictionary<int, int> visualBlocks = [];
    private string[] keys = [];
    private double[] weights = new double[32], trace = new double[32];
    private double oldQ, loss;
    private long steps, updates, revision;
    private ActionCandidate? pending;
    private int pendingHash;
    public LearnedThermalTransition ThermalModel { get; } = new();
    public ControllerLimits Limits => new(256, 256, 4);
    public double Epsilon => options.EpsilonEnd + (options.EpsilonStart - options.EpsilonEnd) * Math.Max(0, 1 - (double)steps / options.EpsilonDecaySteps);
    public ControllerStatus Status => new(steps, updates, Epsilon, loss, 0, 0);
    public JsonElement InferenceState => options.UseThermalModel || options.ExplorationMaxDuration > 1
        ? JsonSerializer.SerializeToElement(new { steps, updates, loss, thermalModel = new
        { ThermalModel.Coefficients, ThermalModel.Samples, ThermalModel.SquaredError }, exploration = new
        { explorer.Choices, explorer.ExploratoryActions, explorer.Bursts, explorer.IllegalTerminations, explorer.MeanDuration } })
        : JsonSerializer.SerializeToElement(new { steps, updates, loss });

    public RelationalSarsaController(RelationalSarsaOptions options, int seed)
    { options.Validate(); this.options = options; random = new(seed); explorer = new(random, options.ExplorationMaxDuration); }

    public void SchemaChanged(IReadOnlyList<string> observations, IReadOnlyList<string> actions)
    {
        keys = observations.ToArray();
        for (int i = 0; i < keys.Length; i++)
            if (keys[i].StartsWith("vision.appearance.", StringComparison.Ordinal) && !visualBlocks.ContainsKey(i))
                visualBlocks[i] = 32 + visualBlocks.Count * 16;
        int size = 32 + visualBlocks.Count * 16;
        Array.Resize(ref weights, size); Array.Resize(ref trace, size);
        ResetTrace();
    }

    public double[] Features(ObservationToken[] state, ActionCandidate action)
    {
        var x = new double[weights.Length];
        double Read(string key) => state.FirstOrDefault(t => t.Type < keys.Length && keys[t.Type] == key)?.Features[0] ?? 0;
        double body = Read("temperature.body.v1"), hunger = Read("hunger.body.v1"), safety = Read("safety.threat.v1");
        double ax = action.Parameters.Length > 0 ? action.Parameters[0] : 0, ay = action.Parameters.Length > 1 ? action.Parameters[1] : 0;
        double dx = ax * options.MovementScale, dy = ay * options.MovementScale;
        x[0] = 1; x[1] = body; x[2] = Math.Abs(body); x[3] = body * body;
        x[4] = hunger; x[5] = hunger * hunger; x[6] = safety; x[7] = safety * safety;
        x[8] = ax; x[9] = ay; x[10] = Math.Abs(ax); x[11] = Math.Abs(ay); x[12] = ax * ay;
        var sensors = state.Where(t => t.Type < keys.Length && keys[t.Type] == "temperature.sensor.v1" && t.Features.Length >= 3).ToArray();
        double Sensor(float sx, float sy) => sensors.FirstOrDefault(t => t.Features[1] == sx && t.Features[2] == sy)?.Features[0] ?? 0;
        double field = Sensor(0, 0), gx = Sensor(1, 0) - Sensor(-1, 0), gy = Sensor(0, 1) - Sensor(0, -1);
        double along = gx * ax + gy * ay;
        double curvature = (Sensor(1, 0) + Sensor(-1, 0) - 2 * field) * ax * ax +
            (Sensor(0, 1) + Sensor(0, -1) - 2 * field) * ay * ay;
        x[13] = field; x[14] = field * field; x[15] = body * field;
        x[16] = along; x[17] = along * body * 5; x[18] = along * field; x[19] = curvature;
        x[20] = Math.Abs(body) * along; x[21] = hunger * body; x[22] = safety * hunger;
        if (options.UseThermalModel)
        {
            double ratio = options.MovementScale / options.SensorDistance;
            double predictedField = field + 0.5 * ratio * along + 0.5 * ratio * ratio * curvature;
            double predictedBody = ThermalModel.Predict(body, predictedField);
            double stayBody = ThermalModel.Predict(body, field);
            x[23] = 4 * Math.Abs(predictedBody);
            x[24] = 16 * predictedBody * predictedBody;
            x[25] = 4 * predictedBody;
            x[26] = 40 * (Math.Abs(predictedBody) - Math.Abs(stayBody));
            x[27] = 100 * (predictedBody * predictedBody - stayBody * stayBody);
        }
        foreach (var block in visualBlocks)
        {
            var objects = state.Where(t => t.Type == block.Key && t.Features.Length >= 3).ToArray();
            if (objects.Length == 0) continue;
            double[] scales = [2, 6, 12, 24];
            for (int k = 0; k < scales.Length; k++)
            {
                double current = 0, moved = 0;
                foreach (var token in objects)
                {
                    double distance = token.Features[2] / Math.Max(1e-6, 1 - token.Features[2]);
                    double px = token.Features[0] * distance, py = token.Features[1] * distance;
                    current = Math.Max(current, Math.Exp(-distance / scales[k]));
                    moved = Math.Max(moved, Math.Exp(-Math.Sqrt((px - dx) * (px - dx) + (py - dy) * (py - dy)) / scales[k]));
                }
                int b = block.Value + k * 4;
                double progress = 6 * (moved - current);
                x[b] = current; x[b + 1] = progress; x[b + 2] = progress * hunger; x[b + 3] = progress * body * 3;
            }
        }
        if (options.Normalization == "fixed")
        {
            // Keep the intercept constant: normalizing it by an action-dependent norm
            // prevents a single intercept weight from representing the reward offset.
            for (int i = 1; i < x.Length; i++) x[i] = Math.Clamp(x[i], -1, 1) / 4;
        }
        else
        {
            double norm = Math.Max(1, Math.Sqrt(x.Sum(v => v * v)));
            for (int i = 0; i < x.Length; i++) x[i] /= norm;
        }
        return x;
    }

    private static double Dot(double[] a, double[] b) { double sum = 0; for (int i = 0; i < a.Length; i++) sum += a[i] * b[i]; return sum; }
    private ActionCandidate Select(ObservationToken[] state, ActionCandidate[] actions, double epsilon)
    {
        if (actions.Length == 0) throw new ArgumentException("No legal actions.");
        if (epsilon > 0 && options.ExplorationMaxDuration > 1 && steps >= options.WarmupSteps)
            return explorer.Choose(actions, epsilon, () => actions.MaxBy(a => Dot(weights, Features(state, a)))!);
        if (epsilon > 0 && random.NextDouble() < epsilon) return actions[random.Next(actions.Length)];
        return actions.MaxBy(a => Dot(weights, Features(state, a)))!;
    }
    public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning)
    {
        if (learning && pending is not null && pendingHash == StateHash(state))
        {
            var legal = actions.FirstOrDefault(a => a.Type == pending.Type && a.Parameters.SequenceEqual(pending.Parameters));
            pending = null;
            if (legal is not null) return legal;
        }
        pending = null;
        return Select(state, actions, learning ? Epsilon : 0);
    }
    public void Receive(AgentTransition transition)
    {
        if (transition.Revision != revision) throw new InvalidOperationException("Stale transition.");
        if (steps < options.WarmupSteps)
        {
            if (options.UseThermalModel && options.LearnThermalModel)
            {
                ObservationToken? Token(ObservationToken[] state, string key) => state.FirstOrDefault(t => t.Type < keys.Length && keys[t.Type] == key);
                var beforeBody = Token(transition.State, "temperature.body.v1");
                var afterBody = Token(transition.NextState, "temperature.body.v1");
                var afterField = transition.NextState.FirstOrDefault(t => t.Type < keys.Length && keys[t.Type] == "temperature.sensor.v1" && t.Features.Length >= 3 && t.Features[1] == 0 && t.Features[2] == 0);
                if (beforeBody is not null && afterBody is not null && afterField is not null)
                    ThermalModel.Observe(beforeBody.Features[0], afterField.Features[0], afterBody.Features[0]);
            }
            steps++; ResetTrace(); return;
        }
        var x = Features(transition.State, transition.Action);
        double q = Dot(weights, x), nextQ = 0;
        if (!transition.Terminal)
        {
            pending = Select(transition.NextState, transition.NextActions, options.EpsilonEnd + (options.EpsilonStart - options.EpsilonEnd) * Math.Max(0, 1 - (double)(steps + 1) / options.EpsilonDecaySteps));
            pendingHash = StateHash(transition.NextState);
            nextQ = Dot(weights, Features(transition.NextState, pending));
        }
        double delta = transition.Reward + options.Gamma * nextQ - q;
        double correction = 1 - options.Alpha * options.Gamma * options.Lambda * Dot(trace, x);
        for (int i = 0; i < weights.Length; i++)
        {
            trace[i] = options.Gamma * options.Lambda * trace[i] + correction * x[i];
            weights[i] += options.Alpha * (delta + q - oldQ) * trace[i] - options.Alpha * (q - oldQ) * x[i];
            if (!double.IsFinite(weights[i])) throw new InvalidOperationException("Non-finite Sarsa weight.");
        }
        oldQ = nextQ; loss = delta * delta; steps++; updates++;
        if (transition.Terminal) ResetTrace();
    }
    private void ResetTrace() { Array.Clear(trace); oldQ = 0; pending = null; explorer.Reset(); }
    public void ConditionsChanged(long value) { if (value != revision) ResetTrace(); revision = value; }
    private static int StateHash(ObservationToken[] state)
    {
        var hash = new HashCode();
        foreach (var token in state) { hash.Add(token.Type); foreach (float f in token.Features) hash.Add(f); }
        return hash.ToHashCode();
    }
    public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(weights));
    public void Import(string path)
    {
        var values = JsonSerializer.Deserialize<double[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing weights.");
        if (values.Length != weights.Length || values.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Incompatible Sarsa weights.");
        weights = values; ResetTrace();
    }
    public void ImportInferenceState(JsonElement state)
    {
        if (state.TryGetProperty("steps", out var s)) steps = s.GetInt64();
        if (state.TryGetProperty("updates", out var u)) updates = u.GetInt64();
        if (state.TryGetProperty("loss", out var l)) loss = l.GetDouble();
        if (state.TryGetProperty("thermalModel", out var m))
            ThermalModel.Import(m.GetProperty("Coefficients").Deserialize<double[]>()!, m.GetProperty("Samples").GetInt64(), m.GetProperty("SquaredError").GetDouble());
    }
    public IAgentController CreateInferenceController()
    {
        var clone = new RelationalSarsaController(options, 0);
        clone.SchemaChanged(keys, []); clone.weights = (double[])weights.Clone();
        clone.steps = steps; clone.updates = updates; clone.loss = loss; clone.revision = revision;
        clone.ThermalModel.Import(ThermalModel.Coefficients, ThermalModel.Samples, ThermalModel.SquaredError);
        return clone;
    }
    public void Dispose() { }
}
