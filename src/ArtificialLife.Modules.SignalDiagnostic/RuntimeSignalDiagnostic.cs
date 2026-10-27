using ArtificialLife.Core;

namespace ArtificialLife.Modules.SignalDiagnostic;

/// <summary>
/// Open diagnostic of channel/action learning, not an ecological benchmark. Exogenous
/// stimuli use separate streams so fresh and experienced agents face matching post-change
/// opportunities. The observable effect exists even when no need rewards it.
/// </summary>
public sealed class RuntimeSignalDiagnostic : IRuntimeModule
{
    private const string Owner = "signal-diagnostic";
    private string mode = "base";
    private int distractors;
    private bool needEnabled;
    public void Register(ModuleBuilder builder)
    {
        mode = builder.Settings.Text("mode", "base");
        distractors = builder.Settings.Integer("distractors", 0);
        needEnabled = builder.Settings.Boolean("needEnabled", true);
        if (mode is not ("base" or "identity" or "channel" or "action" or "distractors") || distractors is < 0 or > 128)
            throw new InvalidDataException("Invalid signal diagnostic settings.");
        builder.Components.Add(new("signal-diagnostic.body", 1, (_, e) => e.Id == 1, (_, _, _) => { }));
        int cue = builder.Observations.Register("signal-diagnostic.cue.v1");
        int effect = builder.Observations.Register("signal-diagnostic.effect.v1");
        int alternate = mode == "channel" ? builder.Observations.Register("signal-diagnostic.alternate.v1") : -1;
        int[] noise = Enumerable.Range(0, distractors).Select(i => builder.Observations.Register($"signal-diagnostic.extra-{i}.v1")).ToArray();
        int choice = builder.Actions.Register("signal-diagnostic.choice.v1");
        int added = mode == "action" ? builder.Actions.Register("signal-diagnostic.added-choice.v1") : -1;
        builder.Sensors.Add(w =>
        {
            var data = w.State(Owner);
            var tokens = new List<ObservationToken> { new(cue, [(float)data.Read<double>("cue")]), new(effect, [(float)data.Read<double>("effect")]) };
            if (alternate >= 0) tokens.Add(new(alternate, [(float)data.Read<double>("alternate")]));
            double[] extras = data.Read<double[]>("extras", []);
            for (int i = 0; i < noise.Length; i++) tokens.Add(new(noise[i], [(float)extras[i]]));
            return tokens;
        });
        void Execute(RuntimeWorld w, ActionCandidate action)
        {
            var data = w.State(Owner);
            double relevant = data.Read<double>(mode == "channel" ? "alternate" : "cue");
            bool correct = mode == "action"
                ? relevant > 0 ? action.Type == added : action.Type == choice && action.Parameters[0] < 0
                : action.Type == choice && Math.Sign(action.Parameters[0]) == Math.Sign(relevant);
            data.Write("correct", correct);
            data.Write("effect", correct ? 1.0 : -1.0);
            data.Write("error", correct ? 0.0 : 1.0);
        }
        builder.ActionProviders.Add(new(choice, _ => [new(choice, [-1]), new(choice, [1])], Execute));
        if (added >= 0) builder.ActionProviders.Add(new(added, _ => [new(added, [0.37f])], Execute));
        builder.Systems.Add(new("signal-diagnostic.stimuli", Draw, []));
        if (needEnabled)
        {
            builder.Drives.Add(w => [new("signal-outcome", w.State(Owner).Read<double>("error"), 0, 1, 1)]);
            builder.Descriptors.Add(new("signal-outcome", "Signal outcome"));
        }
    }
    public void Migrate(RuntimeWorld world, ModuleManifest? previous)
    {
        var data = world.State(Owner);
        if (previous is null)
        {
            ulong seed = world.State("rng:" + Owner).Read<ulong>("state");
            foreach (string phase in new[] { "base", "changed" })
            {
                ulong phaseSeed = seed ^ (phase == "base" ? 0x64A8971BUL : 0xBBF109D3UL);
                world.State("rng:" + Owner + "." + phase).Write("state", phaseSeed);
                world.State("rng:" + Owner + ".noise." + phase).Write("state", phaseSeed ^ 0x49DA321FUL);
            }
            data.Write("effect", 0.0); data.Write("error", 0.0);
        }
        data.Write("mode", mode); data.Write("distractors", distractors); data.Write("needEnabled", needEnabled);
        Draw(world);
    }
    private void Draw(RuntimeWorld world)
    {
        string phase = mode == "base" ? "base" : "changed";
        var data = world.State(Owner);
        data.Write("cue", world.Random(Owner + "." + phase) < 0.5 ? -1.0 : 1.0);
        data.Write("alternate", world.Random(Owner + "." + phase) < 0.5 ? -1.0 : 1.0);
        var extras = new double[distractors];
        for (int i = 0; i < extras.Length; i++) extras[i] = 2 * world.Random(Owner + ".noise." + phase) - 1;
        data.Write("extras", extras);
    }
}
