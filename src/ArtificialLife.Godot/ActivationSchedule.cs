using System.Runtime.CompilerServices;
using System.Text.Json;
using ArtificialLife.Application;
using ArtificialLife.Core;
using Godot;

namespace ArtificialLife.Godot;

/// <summary>Generic integration harness: scheduled package requests, without mechanic knowledge in the view.</summary>
public sealed record ScheduledActivation(long AtAge, ActivationRequest Request, bool ChangesLearning);
public sealed class ActivationSchedule(string path)
{
    private readonly Queue<ScheduledActivation> stages = new((JsonSerializer.Deserialize<ScheduledActivation[]>(File.ReadAllText(path), PackageCatalog.Json)
        ?? throw new InvalidDataException("Missing activation schedule.")).OrderBy(s => s.AtAge));
    public int LimitBatch(long age, int requested) => stages.TryPeek(out var stage) ? (int)Math.Min(requested, Math.Max(0, stage.AtAge - age)) : requested;
    public void Apply(RuntimeSession runtime)
    {
        if (!stages.TryPeek(out var stage) || runtime.Snapshot().Age != stage.AtAge) return;
        stages.Dequeue();
        var before = runtime.Snapshot();
        var status = runtime.Controller.Status;
        var result = runtime.Activate(stage.Request);
        if (!result.Accepted) throw new InvalidOperationException("Scheduled activation failed: " + result.Message);
        var after = runtime.Snapshot();
        var updated = runtime.Controller.Status;
        if (before.Age != after.Age || before.Time != after.Time || before.Agent.Id != after.Agent.Id || before.Agent.Position != after.Agent.Position ||
            status.Steps != updated.Steps || status.Updates != updated.Updates || status.ReplayCount != updated.ReplayCount ||
            after.Revision != before.Revision + (stage.ChangesLearning ? 1 : 0)) throw new InvalidOperationException("Scheduled activation broke continuity.");
        GD.Print($"ARTIFICIALLIFE_ACTIVATION_SMOKE_OK id={stage.Request.Id} age={after.Age} revision={after.Revision} updates={updated.Updates}");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void VerifyUnloaded(RuntimeSession runtime, PresentationHost presentations)
    {
        if (stages.Count != 0) throw new InvalidOperationException("Activation schedule did not finish.");
        var retired = runtime.RetiredContexts.Concat(presentations.RetiredContexts).ToArray();
        for (int i = 0; i < 10 && retired.Any(r => r.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        if (retired.Any(r => r.IsAlive)) throw new InvalidOperationException("Retired simulation or Godot assembly context remains alive.");
        GD.Print($"ARTIFICIALLIFE_UNLOAD_SMOKE_OK contexts={retired.Length}");
    }
}
