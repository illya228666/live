using ArtificialLife.Core;

namespace ArtificialLife.Modules.Movement;

public sealed class RuntimeMovement : IRuntimeModule
{
    public void Register(ModuleBuilder builder)
    {
        int type = builder.Actions.Register("core.move.v1");
        (float X, float Y)[] directions = [(0,-1), (0.70710677f,-0.70710677f), (1,0), (0.70710677f,0.70710677f),
            (0,1), (-0.70710677f,0.70710677f), (-1,0), (-0.70710677f,-0.70710677f), (0,0)];
        Position Destination(RuntimeWorld world, ActionCandidate action)
        {
            double speed = world.Agent.Require("movement.control").Read<double>("speed");
            if (!double.IsFinite(speed) || speed < 0) throw new InvalidDataException("Movement speed must be finite and nonnegative.");
            return new(world.Agent.Position.X + action.Parameters[0] * speed * world.Options.TimeStep,
                world.Agent.Position.Y + action.Parameters[1] * speed * world.Options.TimeStep);
        }
        builder.ActionProviders.Add(new(type,
            world => directions.Select(d => new ActionCandidate(type, [d.X, d.Y])).Where(a => world.Contains(Destination(world, a))),
            (world, action) => world.Agent.Position = Destination(world, action)));
        builder.Components.Add(new("movement.heading", 1, (w, e) => e.Id == w.Agent.Id, (_, _, data) => data.Write("radians", 0.0)));
        builder.Components.Add(new("movement.control", 1, (w, e) => e.Id == w.Agent.Id, (w, _, data) => data.Write("speed", w.Options.MovementSpeed)));
    }
    public void Migrate(RuntimeWorld candidate, ModuleManifest? previous) { }
}
