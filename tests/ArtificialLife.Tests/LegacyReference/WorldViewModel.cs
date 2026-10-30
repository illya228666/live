using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record AgentViewModel(long Id, double X, double Y);
public sealed record WorldViewModel(RuntimeFrame Frame, double Reward, string Mode, HomeostasisReport Homeostasis)
{
    public double Width => Frame.Width;
    public double Height => Frame.Height;
    public AgentViewModel Agent => new(Frame.Agent.Id, Frame.Agent.Position.X, Frame.Agent.Position.Y);
    public long Step => Frame.Age;
    public double SimulationTime => Frame.Time;
}

public sealed class VisualizationSession : IDisposable
{
    private readonly RuntimeSession runtime;
    public double TimeStep => runtime.Options.World.TimeStep;
    public RuntimeSession Runtime => runtime;
    public VisualizationSession(string checkpointDirectory) => runtime = RuntimeCheckpoint.Open(checkpointDirectory);
    public void Advance() => runtime.AdvanceOne(learning: false);
    public WorldViewModel Snapshot() => new(runtime.Snapshot(), runtime.LastReward, "Evaluation · ε = 0", runtime.Report());
    public void Dispose() => runtime.Dispose();
}
