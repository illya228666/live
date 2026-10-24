using ArtificialLife.Application;
using Godot;

namespace ArtificialLife.Godot;

/// <summary>Rendering/input adapter only. All policy and physical state live outside Godot.</summary>
public partial class Main : Node2D
{
    private VisualizationSession? session;
    private PresentationHost? presentations;
    private ActivationInbox? activationInbox;
    private double activationElapsed;
    private string? lastActivationNotice;
    private WorldViewModel? snapshot;
    private readonly Queue<Vector2> trail = new();
    private readonly Queue<TrainingSample> history = new();
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
            InitializePackageUi(root);
            if (trainMode)
            {
                StartLiveTraining();
                QueueRedraw();
                return;
            }
            session = new VisualizationSession(checkpoint);
            snapshot = session.Snapshot();
            AttachPresentations(session.Runtime);
            if (smokeSteps > 0)
            {
                for (int index = 0; index < smokeSteps; index++)
                {
                    Advance();
                }
                GD.Print($"ARTIFICIALLIFE_SMOKE_OK steps={snapshot!.Step} balance={snapshot.Homeostasis.Balance:P1}");
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
        activationElapsed += delta;
        if (activationElapsed >= 1)
        {
            activationElapsed = 0;
            if (trainingSession is { Evaluating: false }) trainingSession.Runtime.Poll(activationInbox!);
            else if (session is not null) session.Runtime.Poll(activationInbox!);
            var runtime = trainingSession?.Runtime ?? session?.Runtime;
            if (runtime?.LastActivation is { Id: not "initial" } outcome && outcome.Id != lastActivationNotice)
            {
                lastActivationNotice = outcome.Id;
                ShowNotice($"Package {(outcome.Accepted ? "accepted" : "rejected")}: {outcome.Message}", 8);
                RefreshPackageList();
            }
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
        history.Enqueue(new TrainingSample(snapshot.Step, 1, snapshot.Step, snapshot.Agent.X, snapshot.Agent.Y,
            snapshot.Homeostasis, snapshot.Reward, false));
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
        if (packageDialog?.Visible == true) return;
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
            AttachPresentations(session.Runtime);
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
        presentations?.Draw(state.Frame);
        Vector2[] points = liveSnapshot is null ? trail.ToArray() : liveSnapshot.Trail
            .Select(sample => ToScreen(sample.X, sample.Y)).ToArray();
        for (int index = 1; index < points.Length; index++)
        {
            DrawLine(points[index - 1], points[index], new Color(0.4f, 0.87f, 0.74f, 0.6f * index / points.Length), 2, antialiased: true);
        }
        Label($"{state.Width:0} x {state.Height:0} / entities {state.Frame.Entities.Count} / revision {state.Frame.Revision}", 58, 772, 12, Muted);
        if (liveSnapshot is not null)
        {
            DrawTrainingHud(state);
            return;
        }
        DrawHomeostasisHud(state);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mouse && mouse.Position.X >= 760)
        {
            if (mouse.ButtonIndex == MouseButton.WheelDown) needsOffset++;
            if (mouse.ButtonIndex == MouseButton.WheelUp) needsOffset = Math.Max(0, needsOffset - 1);
            QueueRedraw();
        }
    }

    private void AttachPresentations(RuntimeSession runtime)
    {
        presentations ??= new PresentationHost(this, worldRect);
        activationInbox = new(runtime.Options.Inbox);
        runtime.PresentationHost = presentations;
        runtime.PreparePresentation();
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
        DisposePackageUi();
        presentations?.Dispose();
        trainingSession?.Dispose();
        session?.Dispose();
    }
}
