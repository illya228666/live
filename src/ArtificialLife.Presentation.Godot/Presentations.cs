using ArtificialLife.Core;
using ArtificialLife.Godot.Contracts;
using Godot;

namespace ArtificialLife.Presentation.Godot;

public abstract class WorldPresentation : IGodotPresentation
{
    protected GodotPresentationContext Context = null!;
    protected RuntimeFrame Frame = null!;
    public virtual void Prepare(GodotPresentationContext context, RuntimeFrame initial) { Context = context; Frame = initial; }
    public virtual void Update(RuntimeFrame frame) => Frame = frame;
    public abstract void Draw();
    public void Dispose() { Frame = null!; Context = null!; }
}
public sealed class AgentPresentation : WorldPresentation
{
    public override void Draw()
    {
        var point = Context.ToScreen(Frame.Agent.Position);
        Context.Canvas.DrawCircle(point, 8, new Color("68dfbe"));
        Context.Canvas.DrawArc(point, 12, 0, Mathf.Tau, 40, new Color(0.4f, 0.87f, 0.74f, 0.35f), 1, true);
    }
}
public sealed class TemperaturePresentation : WorldPresentation
{
    public override void Draw()
    {
        foreach (var entity in Frame.Entities)
        {
            if (!entity.Components.TryGetValue("temperature.emitter", out var data)) continue;
            double strength = data.Read<double>("strength") * (1 + data.Read<double>("amplitude") * Math.Sin(2 * Math.PI * Frame.Time / data.Read<double>("period")));
            double radius = data.Read<double>("radius");
            double ambient = Frame.ModuleState["temperature"].Read<double>("ambient");
            var center = Context.ToScreen(entity.Position);
            for (int ring = 35; ring >= 1; ring--)
            {
                float pixels = ring * 8;
                double distance = pixels / Context.Viewport.Size.X * Frame.Width;
                float warmth = (float)(strength * Math.Exp(-distance * distance / (2 * radius * radius)) / Math.Max(1, ambient + strength));
                Context.Canvas.DrawCircle(center, pixels, new Color(0.96f, 0.38f, 0.15f, warmth * 0.11f));
            }
            Context.Canvas.DrawCircle(center, 11, new Color("ff9d52"));
            Context.Canvas.DrawArc(center, 17, 0, Mathf.Tau, 64, new Color(1, 0.6f, 0.3f, 0.45f), 1, true);
        }
    }
}
public sealed class FoodPresentation : WorldPresentation
{
    public override void Draw()
    {
        foreach (var entity in Frame.Entities)
        {
            if (!entity.Components.ContainsKey("food.apple")) continue;
            Vector2 point = Context.ToScreen(entity.Position);
            Context.Canvas.DrawColoredPolygon([point + new Vector2(0,-7), point + new Vector2(7,0), point + new Vector2(0,7), point + new Vector2(-7,0)], new Color("ef6672"));
            Context.Canvas.DrawLine(point + new Vector2(0,-7), point + new Vector2(3,-10), new Color("68dfbe"), 2);
        }
    }
}
