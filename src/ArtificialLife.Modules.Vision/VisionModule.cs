using ArtificialLife.Core;

namespace ArtificialLife.Modules.Vision;

/// <summary>Converts backend-independent egocentric perceptions into generic sensory tokens.</summary>
public sealed class VisionModule : IObservationProvider
{
    private readonly IVisionBackend backend;
    private readonly Dictionary<AppearanceType, int> types = new();

    public VisionModule(IVisionBackend backend, TypeRegistry registry)
    {
        this.backend = backend;
        foreach (AppearanceType appearance in Enum.GetValues<AppearanceType>())
            types.Add(appearance, registry.Register($"vision.appearance.{appearance.ToString().ToLowerInvariant()}.v1"));
    }

    public IEnumerable<ObservationToken> Observe(WorldState world, AgentState agent)
    {
        var observer = new VisionObserver(agent.Position, agent.OrientationRadians);
        foreach (VisualPerception perception in backend.Perceive(world, observer))
        {
            if (!types.ContainsKey(perception.AppearanceType) || !double.IsFinite(perception.DirectionRadians) ||
                !double.IsFinite(perception.Distance) || perception.Distance < 0 ||
                !double.IsFinite(perception.ApparentSize) || perception.ApparentSize is < 0 or > Math.PI)
                throw new InvalidOperationException("Vision backend returned an invalid relative perception.");
            yield return new ObservationToken(types[perception.AppearanceType],
                [(float)Math.Cos(perception.DirectionRadians), (float)Math.Sin(perception.DirectionRadians),
                 (float)(perception.Distance / (1 + perception.Distance)), (float)(perception.ApparentSize / Math.PI)]);
        }
    }
}
