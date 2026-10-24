using ArtificialLife.Core;
using ArtificialLife.Godot.Contracts;
using Godot;

namespace ArtificialLife.Modules.Wildlife.Presentation.Godot;

/// <summary>External PNGs are decoded at preparation; no editor import or host registration.</summary>
public sealed class BoarPresentation : IGodotPresentation
{
    private GodotPresentationContext context = null!;
    private RuntimeFrame frame = null!;
    private ImageTexture? texture;
    private double radius, diameter;

    public void Prepare(GodotPresentationContext context, RuntimeFrame initial)
    {
        this.context = context;
        frame = initial;
        radius = context.Settings.Number("dangerRadius", 10);
        diameter = context.Settings.Number("diameter", 4);
        using var image = new Image();
        var error = image.Load(System.IO.Path.Combine(context.PackageDirectory, "assets", "boar.png"));
        if (error != Error.Ok || image.IsEmpty()) throw new InvalidDataException("Cannot load wildlife boar.png: " + error);
        texture = context.OwnResource(ImageTexture.CreateFromImage(image));
    }

    public void Update(RuntimeFrame frame) => this.frame = frame;

    public void Draw()
    {
        foreach (var animal in frame.Entities)
        {
            if (!animal.Components.TryGetValue("wildlife.boar", out var state)) continue;
            var point = context.ToScreen(animal.Position);
            float reach = context.Pixels(radius), size = context.Pixels(diameter);
            context.Canvas.DrawCircle(point, reach, new Color(0.9f, 0.28f, 0.16f, 0.09f));
            context.Canvas.DrawArc(point, reach, 0, Mathf.Tau, 96, new Color(0.95f, 0.42f, 0.24f, 0.4f), 1, true);
            context.Canvas.DrawSetTransform(point, (float)state.Read<double>("heading"));
            try { context.Canvas.DrawTextureRect(texture!, new Rect2(-size / 2, -size / 2, size, size), false); }
            finally { context.Canvas.DrawSetTransform(Vector2.Zero); }
        }
    }

    public void Dispose() { texture = null; context = null!; frame = null!; }
}
