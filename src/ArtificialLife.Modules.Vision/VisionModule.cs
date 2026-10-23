using ArtificialLife.Core;

namespace ArtificialLife.Modules.Vision;

/// <summary>Converts backend-independent egocentric perceptions into generic sensory tokens.</summary>
public sealed class VisionModule : IObservationProvider
{
    private readonly IVisionBackend backend;
    private readonly Dictionary<AppearanceType, int> types = new();
    private readonly int maximum;

    public VisionModule(IVisionBackend backend, TypeRegistry registry, VisionOptions? options = null)
    {
        this.backend = backend;
        maximum = options?.MaxPerAppearance ?? int.MaxValue;
        if (maximum < 1) throw new ArgumentException("MaxPerAppearance must be positive.");
        foreach (AppearanceType appearance in Enum.GetValues<AppearanceType>())
            types.Add(appearance, registry.Register($"vision.appearance.{appearance.ToString().ToLowerInvariant()}.v1"));
    }

    public IEnumerable<ObservationToken> Observe(WorldState world, AgentState agent)
    {
        var observer = new VisionObserver(agent.Position, agent.OrientationRadians);
        var perceptions = backend.Perceive(world, observer);
        if (maximum != int.MaxValue) perceptions = perceptions.GroupBy(p => p.AppearanceType).SelectMany(g => g.OrderBy(p => p.Distance).Take(maximum));
        foreach (VisualPerception perception in perceptions)
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
