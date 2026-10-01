namespace ArtificialLife.Core;

/// <summary>One action type with nine parameterized movement candidates.</summary>
public sealed class MovementProvider(TypeRegistry registry) : IActionProvider
{
    private readonly int type = registry.Register("core.move.v1");
    private static readonly (float X, float Y)[] Directions =
    [
        (0, -1), (0.70710677f, -0.70710677f), (1, 0), (0.70710677f, 0.70710677f),
        (0, 1), (-0.70710677f, 0.70710677f), (-1, 0), (-0.70710677f, -0.70710677f), (0, 0)
    ];

    public bool Handles(int actionType) => actionType == type;

    public IEnumerable<ActionCandidate> GetLegalActions(WorldState world, AgentState agent)
    {
        foreach ((float x, float y) in Directions)
        {
            var candidate = new ActionCandidate(type, [x, y]);
            if (world.Contains(Destination(world, agent, candidate)))
            {
                yield return candidate;
            }
        }
    }

    public void Execute(WorldState world, AgentState agent, ActionCandidate action)
    {
        Position next = Destination(world, agent, action);
        if (!Handles(action.Type) || !world.Contains(next))
        {
            throw new InvalidOperationException("Illegal movement.");
        }
        agent.Position = next;
    }

    private static Position Destination(WorldState world, AgentState agent, ActionCandidate action)
    {
        double distance = world.Options.MovementSpeed * world.Options.TimeStep;
        return new Position(agent.Position.X + action.Parameters[0] * distance, agent.Position.Y + action.Parameters[1] * distance);
    }
}

public sealed class PositionObservationProvider(TypeRegistry registry) : IObservationProvider
{
    private readonly int type = registry.Register("core.position.v1");
    public IEnumerable<ObservationToken> Observe(WorldState world, AgentState agent)
    {
        yield return new ObservationToken(type,
            [(float)(2 * agent.Position.X / world.Options.Width - 1), (float)(2 * agent.Position.Y / world.Options.Height - 1)]);
    }
}
