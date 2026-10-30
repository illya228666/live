using ArtificialLife.Core;

namespace ArtificialLife.Tests;

// Infrastructure fixture only: new entities and resources must not need Viewer-specific code.
public sealed class VisualProbeFixture : IRuntimeModule
{
    public void Register(ModuleBuilder builder)
    {
        builder.Components.Add(new("visual-probe.entity", 1, (_, _) => false, (_, _, _) => { }));
        builder.Systems.Add(new("visual-probe.motion", world =>
        {
            foreach (var entity in world.AllEntities.Where(e => e.Components.ContainsKey("visual-probe.entity")))
            {
                entity.Position = new(world.Options.Width * (.5 + .2 * Math.Cos(world.Time)), world.Options.Height * (.5 + .2 * Math.Sin(world.Time)));
                entity.Require("visual-probe.entity").Write("heading", world.Time);
            }
        }, []));
    }
    public void Migrate(RuntimeWorld world, ModuleManifest? previous)
    {
        if (previous is not null) return;
        var entity = world.CreateEntity(new(world.Options.Width / 2, world.Options.Height / 2));
        entity.Components["visual-probe.entity"] = new();
        entity.Require("visual-probe.entity").Write("heading", 0.0);
    }
}
