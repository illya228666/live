using ArtificialLife.Application;
using Godot;

namespace ArtificialLife.Godot;

/// <summary>Rendering/input adapter only. All policy and physical state live outside Godot.</summary>
public partial class Main : Node2D
{
    private VisualizationSession? session;
    private WorldViewModel? snapshot;
    private readonly Queue<Vector2> trail = new();
    private readonly Queue<double> history = new();
    private double accumulator;
    private double speed = 12;
    private bool paused;
    private string? error;
    private string checkpoint = "";
    private string? capture;
    private bool captureScheduled;
    private int frames;
    private int smokeSteps;
    private readonly Rect2 worldRect = new(40, 92, 700, 700);
    private static readonly Color TextColor = new("dfe7ef");
    private static readonly Color Muted = new("7f95ab");
    private static readonly Color Accent = new("68dfbe");

    public override void _Ready()
    {
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "../.."));
        try
        {
            ParseLaunchArguments(root);
            if (trainMode)
            {
                StartLiveTraining();
                QueueRedraw();
                return;
            }
            session = new VisualizationSession(checkpoint);
            snapshot = session.Snapshot();
            if (smokeSteps > 0)
            {
                for (int index = 0; index < smokeSteps; index++)
                {
                    Advance();
                }
                GD.Print($"ARTIFICIALLIFE_SMOKE_OK steps={snapshot!.Step} body={snapshot.Agent.BodyTemperature:F3}");
                if (capture is null)
                {
                    GetTree().Quit();
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            GD.PushError(error);
            if (smokeSteps > 0 || trainingSmokeSteps > 0)
            {
                GetTree().Quit(1);
            }
        }
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (trainingSession is not null)
        {
            try
            {
                ProcessTraining(delta);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                paused = true;
                trainingSession.Dispose();
                trainingSession = null;
                GD.PushError(error);
                if (trainingSmokeSteps > 0)
                {
                    GetTree().Quit(1);
                }
            }
        }
        else if (session is not null && !paused)
        {
            accumulator += delta * speed;
            int budget = 120;
            while (accumulator >= session.TimeStep && budget-- > 0)
            {
                accumulator -= session.TimeStep;
                Advance();
            }
            QueueRedraw();
        }
        frames++;
        if (capture is not null && !captureScheduled && frames >= 4 && (!trainMode || trainingSmokeSteps == 0 || trainingSmokeFinished))
        {
            captureScheduled = true;
            string path = capture;
            RenderingServer.FramePostDraw += SaveCapture;
            void SaveCapture()
            {
                RenderingServer.FramePostDraw -= SaveCapture;
                Error result = GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print($"CAPTURE {result}: {path}");
                GetTree().Quit(result == Error.Ok ? 0 : 1);
            }
        }
    }

    private void Advance()
    {
        session!.Advance();
        snapshot = session.Snapshot();
        trail.Enqueue(ToScreen(snapshot.Agent.X, snapshot.Agent.Y));
        history.Enqueue(snapshot.Agent.BodyTemperature);
        if (trail.Count > 160)
        {
            trail.Dequeue();
        }
        if (history.Count > 180)
        {
            history.Dequeue();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        if (trainMode)
        {
            HandleTrainingKey(key);
            QueueRedraw();
            return;
        }
        switch (key.Keycode)
        {
            case Key.Space: paused = !paused; break;
            case Key.Equal: speed = Math.Min(speed * 2, 96); break;
            case Key.Minus: speed = Math.Max(speed / 2, 0.75); break;
            case Key.R:
                session?.Dispose();
                session = new VisualizationSession(checkpoint);
                snapshot = session.Snapshot();
                trail.Clear(); history.Clear(); accumulator = 0;
                break;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        Label("ArtificialLife", 40, 40, 28, TextColor);
        Label("A creature learning to stay in balance", 40, 66, 15, Muted);
        Label(trainMode ? "LIVE DQN TRAINING  /  CPU" : "LOCAL NEURAL POLICY  / CPU", 830, 44, 14, Accent);
        DrawRect(worldRect, new Color("101b29"));
        DrawRect(worldRect, new Color("304356"), filled: false, width: 1);
        if (snapshot is null)
        {
            Label("Train a checkpoint to begin", 80, 150, 24, TextColor);
            Label("./tools/train.ps1", 80, 190, 19, Accent);
            Label((error ?? "No checkpoint loaded")[..Math.Min((error ?? "No checkpoint loaded").Length, 85)], 80, 230, 14, Muted);
            return;
        }
        WorldViewModel state = snapshot;
        Vector2 center = ToScreen(state.HeatSource.X, state.HeatSource.Y);
        // Кольца — только отображение поля; эти координаты никогда не передаются политике.
        for (int ring = 35; ring >= 1; ring--)
        {
            float radius = ring * 8;
            float distance = radius / worldRect.Size.X * (float)state.Width;
            float warmth = (float)(state.HeatSource.Strength * Math.Exp(-distance * distance / (2 * state.HeatSource.Radius * state.HeatSource.Radius)) / 52);
            DrawCircle(center, radius, new Color(0.96f, 0.38f, 0.15f, warmth * 0.022f));
        }
        for (int line = 1; line < 10; line++)
        {
            float offset = line * 70;
            DrawLine(new Vector2(40 + offset, 92), new Vector2(40 + offset, 792), new Color(0.45f, 0.6f, 0.7f, 0.06f));
            DrawLine(new Vector2(40, 92 + offset), new Vector2(740, 92 + offset), new Color(0.45f, 0.6f, 0.7f, 0.06f));
        }
        DrawCircle(center, 11, new Color("ff9d52"));
        DrawArc(center, 17, 0, Mathf.Tau, 64, new Color(1, 0.6f, 0.3f, 0.45f), 1, antialiased: true);
        Vector2[] points = liveSnapshot is null ? trail.ToArray() : liveSnapshot.Trail
            .Where(sample => liveSnapshot.Training.Evaluating || sample.Episode == liveSnapshot.Training.Episode)
            .Select(sample => ToScreen(sample.X, sample.Y)).ToArray();
        for (int index = 1; index < points.Length; index++)
        {
            DrawLine(points[index - 1], points[index], new Color(0.4f, 0.87f, 0.74f, 0.6f * index / points.Length), 2, antialiased: true);
        }
        Vector2 agent = ToScreen(state.Agent.X, state.Agent.Y);
        DrawCircle(agent, 8, Accent);
        DrawArc(agent, 12, 0, Mathf.Tau, 40, new Color(0.4f, 0.87f, 0.74f, 0.35f), 1, antialiased: true);
        Label($"{state.Width:0} × {state.Height:0} simulation units", 58, 772, 13, Muted);
        if (liveSnapshot is not null)
        {
            DrawTrainingHud(state);
            return;
        }
        Label("THERMAL HOMEOSTASIS", 786, 122, 15, Muted);
        Label($"{state.Agent.BodyTemperature:F2} °C", 786, 183, 42,
            Math.Abs(state.Agent.BodyTemperature - state.TargetTemperature) <= state.ComfortHalfWidth ? Accent : new Color("ffb16c"));
        Label($"Body temperature · target {state.TargetTemperature:F1} °C", 786, 213, 15, Muted);
        DrawLine(new Vector2(786, 244), new Vector2(1180, 244), new Color("304356"));
        Metric("Local environment", $"{state.Agent.LocalTemperature:F2} °C", 284);
        Metric("Current reward", $"{state.Reward:+0.000;-0.000;0.000}", 326);
        Metric("Simulation step", state.Step.ToString(System.Globalization.CultureInfo.InvariantCulture), 368);
        Metric("Elapsed world time", $"{state.SimulationTime:F0} s", 410);
        Label("BODY TEMPERATURE HISTORY", 786, 464, 13, Muted);
        DrawHistory(state);
        Label(state.Mode, 786, 635, 16, Accent);
        Label(paused ? "Paused" : $"Running at {speed:0.##}× simulated time", 786, 667, 15, TextColor);
        Label("Space   pause / resume", 786, 721, 14, Muted);
        Label("+ / −     change speed", 786, 748, 14, Muted);
        Label("R            restart world", 786, 775, 14, Muted);
    }

    private void DrawHistory(WorldViewModel state)
    {
        var rect = new Rect2(786, 486, 394, 110);
        DrawRect(rect, new Color("101b29"));
        float Y(double temperature) => rect.Position.Y + rect.Size.Y / 2 - (float)(temperature - state.TargetTemperature) * 12;
        DrawRect(new Rect2(786, Y(state.TargetTemperature + state.ComfortHalfWidth), 394, (float)state.ComfortHalfWidth * 24), new Color(0.4f, 0.87f, 0.74f, 0.1f));
        DrawLine(new Vector2(786, Y(state.TargetTemperature)), new Vector2(1180, Y(state.TargetTemperature)), new Color(0.4f, 0.87f, 0.74f, 0.35f));
        double[] temperatures = history.ToArray();
        for (int index = 1; index < temperatures.Length; index++)
        {
            float x1 = 786 + (index - 1) * 394f / 179;
            float x2 = 786 + index * 394f / 179;
            DrawLine(new Vector2(x1, Math.Clamp(Y(temperatures[index - 1]), 486, 596)),
                new Vector2(x2, Math.Clamp(Y(temperatures[index]), 486, 596)), Accent, 2, antialiased: true);
        }
        Label($"{state.TargetTemperature:0.0} ± {state.ComfortHalfWidth:0.#} °C comfort band", 786, 615, 12, Muted);
    }

    private Vector2 ToScreen(double x, double y) => worldRect.Position + new Vector2((float)(x / snapshot!.Width), (float)(y / snapshot.Height)) * worldRect.Size;
    private void Metric(string label, string value, float y)
    {
        Label(label, 786, y, 16, Muted);
        Label(value, 1040, y, 17, TextColor);
    }
    private void Label(string text, float x, float y, int size, Color color) => DrawString(ThemeDB.FallbackFont, new Vector2(x, y), text, fontSize: size, modulate: color);
    public override void _ExitTree()
    {
        trainingSession?.Dispose();
        session?.Dispose();
    }
}
