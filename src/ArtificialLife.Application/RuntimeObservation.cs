namespace ArtificialLife.Application;

/// <summary>Optional observation adapter; callbacks run only on the runtime owner thread.</summary>
public interface IRuntimeObservation : IDisposable
{
    string SessionId { get; }
    long FramesCaptured { get; }
    void BeforeStep();
    void Tick();
    void Notify(string kind);
}
public static class RuntimeObservation
{
    public static Func<RuntimeSession, IRuntimeObservation>? Attach { get; set; }
}
