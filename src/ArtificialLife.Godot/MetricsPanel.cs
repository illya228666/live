using Godot;

namespace ArtificialLife.Godot;

public partial class MetricsPanel : Control
{
    private readonly Queue<(double Balance, double Reward)> samples = new();
    public void ClearHistory() { samples.Clear(); QueueRedraw(); }
    public void Add(IReadOnlyDictionary<string, double> metrics)
    {
        samples.Enqueue((metrics.GetValueOrDefault("balance"), metrics.GetValueOrDefault("reward")));
        while (samples.Count > 240) samples.Dequeue();
        QueueRedraw();
    }
    public override void _Draw()
    {
        var values = samples.ToArray();
        Graph("Баланс", 0, 0, 1, p => p.Balance);
        Graph("Награда", 80, -1, 1, p => p.Reward);
        void Graph(string name, float top, double min, double max, Func<(double Balance, double Reward), double> value)
        {
            var rect = new Rect2(0, top + 18, 300, 48);
            DrawString(ThemeDB.FallbackFont, new(0, top + 12), name, fontSize: 12, modulate: new Color("9fb3c8"));
            DrawRect(rect, new Color(.04f, .08f, .13f, .85f));
            for (int i = 1; i < values.Length; i++)
            {
                Vector2 Point(int index) => new(index * rect.Size.X / Math.Max(1, values.Length - 1), rect.End.Y - (float)((Math.Clamp(value(values[index]), min, max) - min) / (max - min)) * rect.Size.Y);
                DrawLine(Point(i - 1), Point(i), new Color("68dfbe"), 1.5f, true);
            }
        }
    }
}
