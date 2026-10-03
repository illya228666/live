using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ArtificialLife.Application;
using Godot;

namespace ArtificialLife.Godot;

public partial class Main
{
    private bool trainMode;
    private TrainingVisualizationSession? trainingSession;
    private TrainingVisualizationSnapshot? liveSnapshot;
    private TrainingVisualizationOptions visualOptions = new();
    private string experimentConfig = "";
    private string visualConfig = "";
    private string? initialTrainingSpeed;
    private TrainingSpeed trainingSpeed;
    private double trainingCredits;
    private double snapshotElapsed;
    private double rateElapsed;
    private int rateSteps;
    private double actualStepsPerSecond;
    private string notice = "";
    private double noticeSeconds;
    private int trainingSmokeSteps;
    private bool trainingSmokeFinished;
    private int nextTrainingLog = 5000;
    private string? progressCaptureDirectory;
    private readonly Queue<int> captureStages = new();
    private readonly List<TrainingViewModel> observationStages = new();
    private readonly List<double> workDurations = new();
    private readonly List<double> frameDurations = new();
    private readonly Stopwatch trainingWallClock = new();
    private ExperimentOptions? smokeOptions;
    private WorldViewModel? initialSmokeWorld;
    private TrainingSample? previousSmokeSample;

    private void ParseLaunchArguments(string root)
    {
        experimentConfig = System.IO.Path.Combine(root, "configs/default.json");
        visualConfig = System.IO.Path.Combine(root, "configs/live-training.json");
        string? selectedCheckpoint = null;
        string[] arguments = OS.GetCmdlineUserArgs();
        for (int index = 0; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (option == "--train")
            {
                trainMode = true;
                continue;
            }
            if (++index >= arguments.Length)
            {
                throw new ArgumentException($"Missing value for {option}.");
            }
            string value = arguments[index];
            switch (option)
            {
                case "--checkpoint": selectedCheckpoint = value; break;
                case "--config": experimentConfig = value; break;
                case "--visual-config": visualConfig = value; break;
                case "--training-speed": initialTrainingSpeed = value; break;
                case "--capture": capture = value; break;
                case "--capture-training-progress": progressCaptureDirectory = value; break;
                case "--smoke-steps": smokeSteps = PositiveInteger(value); break;
                case "--training-smoke-steps":
                    trainingSmokeSteps = PositiveInteger(value);
                    trainMode = true;
                    break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }
        }
        checkpoint = selectedCheckpoint ?? System.IO.Path.Combine(root,
            trainMode ? "artifacts/checkpoints/live-thermal" : "artifacts/checkpoints/thermal");
    }

    private static int PositiveInteger(string value)
    {
        int number = int.Parse(value, CultureInfo.InvariantCulture);
        return number > 0 ? number : throw new ArgumentException("Smoke step count must be positive.");
    }

    private void StartLiveTraining()
    {
        visualOptions = TrainingVisualizationOptions.Load(visualConfig);
        ExperimentOptions options = ExperimentOptions.Load(experimentConfig);
        if (progressCaptureDirectory is not null && trainingSmokeSteps == 0)
        {
            throw new ArgumentException("Progress capture requires a finite --training-smoke-steps run.");
        }
        if (trainingSmokeSteps > 0 && trainingSmokeSteps < options.Learning.WarmupSteps)
        {
            throw new ArgumentException($"Training smoke must reach warm-up: at least {options.Learning.WarmupSteps} steps.");
        }
        trainingSpeed = initialTrainingSpeed is null ? visualOptions.DefaultTrainingSpeed : initialTrainingSpeed.ToLowerInvariant() switch
        {
            "1x" => TrainingSpeed.Normal,
            "10x" => TrainingSpeed.TenTimes,
            "100x" => TrainingSpeed.HundredTimes,
            "max" => TrainingSpeed.Max,
            _ => throw new ArgumentException("Training speed must be 1x, 10x, 100x or Max.")
        };
        if (trainingSmokeSteps > 0)
        {
            trainingSpeed = TrainingSpeed.Max;
        }
        trainingSession = new TrainingVisualizationSession(options, visualOptions);
        RefreshTrainingSnapshot();
        if (trainingSmokeSteps > 0)
        {
            smokeOptions = options;
            initialSmokeWorld = liveSnapshot!.World;
            previousSmokeSample = new TrainingSample(0, 1, 0, initialSmokeWorld.Agent.X, initialSmokeWorld.Agent.Y,
                initialSmokeWorld.Agent.BodyTemperature, 0, 0, false);
        }
        trainingWallClock.Start();
        ShowNotice($"Fresh weights · warm-up {options.Learning.WarmupSteps:N0} steps", 6);
        if (progressCaptureDirectory is not null)
        {
            System.IO.Directory.CreateDirectory(progressCaptureDirectory);
            // Снимки разных возрастов одной непрерывной жизни.
            foreach (int stage in new[] { 1000, 20100, 50100, 99900, trainingSmokeSteps }.Distinct().Order())
            {
                if (trainingSmokeSteps == 0 || stage <= trainingSmokeSteps)
                {
                    captureStages.Enqueue(stage);
                }
            }
        }
    }

    private void ProcessTraining(double delta)
    {
        if (progressCaptureDirectory is not null)
        {
            frameDurations.Add(delta * 1000);
        }
        snapshotElapsed += delta;
        rateElapsed += delta;
        noticeSeconds = Math.Max(0, noticeSeconds - delta);
        if (!paused)
        {
            bool max = trainingSpeed == TrainingSpeed.Max;
            if (!max)
            {
                trainingCredits = Math.Min(visualOptions.MaxBatchSize, trainingCredits + delta * visualOptions.StepsPerSecond(trainingSpeed));
            }
            int requested = max ? visualOptions.MaxBatchSize : (int)trainingCredits;
            int currentStep = liveSnapshot!.Training.Step;
            if (trainingSmokeSteps > 0)
            {
                // Точное окончание smoke; снимок шага берём у Application, а не у кадрового счётчика.
                currentStep = trainingSession!.Snapshot().Training.Step;
                requested = Math.Min(requested, trainingSmokeSteps - currentStep);
                // Retain every transition for smoke continuity checks, with one overlapping trail sample.
                requested = Math.Min(requested, visualOptions.TrailLength - 1);
            }
            if (captureStages.TryPeek(out int captureAt))
            {
                currentStep = trainingSession!.Snapshot().Training.Step;
                requested = Math.Min(requested, captureAt - currentStep);
            }
            if (requested > 0)
            {
                var timer = Stopwatch.StartNew();
                TimeSpan budget = TimeSpan.FromMilliseconds(visualOptions.FrameBudgetMilliseconds);
                int completed = trainingSession!.Evaluating
                    ? trainingSession.AdvanceEvaluation(requested, budget)
                    : trainingSession.AdvanceTraining(requested, budget);
                if (progressCaptureDirectory is not null)
                {
                    workDurations.Add(timer.Elapsed.TotalMilliseconds);
                }
                if (!max)
                {
                    trainingCredits -= completed;
                }
                rateSteps += completed;
            }
        }
        if (snapshotElapsed >= 1.0 / visualOptions.VisualTicksPerSecond || trainingSmokeSteps > 0 || progressCaptureDirectory is not null)
        {
            snapshotElapsed = 0;
            RefreshTrainingSnapshot();
            if (trainingSmokeSteps > 0 && !trainingSmokeFinished) ValidateContinuousSmoke();
        }
        if (rateElapsed >= 1)
        {
            actualStepsPerSecond = rateSteps / rateElapsed;
            rateSteps = 0;
            rateElapsed = 0;
        }
        if (trainingSmokeSteps > 0 && !trainingSmokeFinished && liveSnapshot!.Training.Step == trainingSmokeSteps)
        {
            FinishTrainingSmoke();
        }
        if (progressCaptureDirectory is not null || trainingSmokeSteps > 0)
        {
            ObserveTrainingProgress();
        }
        QueueRedraw();
    }

    private void RefreshTrainingSnapshot()
    {
        liveSnapshot = trainingSession!.Snapshot();
        snapshot = liveSnapshot.World;
    }

    private void FinishTrainingSmoke()
    {
        TrainingViewModel state = liveSnapshot!.Training;
        if (state.OptimizationUpdates == 0 || state.ReplayCount == 0 || !double.IsFinite(state.LastLoss) ||
            !double.IsFinite(snapshot!.Agent.BodyTemperature) || !double.IsFinite(state.RecentMetrics.MeanAbsoluteError))
        {
            throw new InvalidOperationException("Live training smoke did not produce valid optimized state.");
        }
        trainingSession!.SaveCheckpoint(checkpoint);
        GD.Print($"ARTIFICIALLIFE_TRAINING_SMOKE_OK life={state.Life} age={state.Age} steps={state.Step} updates={state.OptimizationUpdates} replay={state.ReplayCount} loss={state.LastLoss:F4} apples={snapshot.Apples.Count} spawned={snapshot.TotalApplesSpawned} eaten={snapshot.TotalApplesSpawned - snapshot.Apples.Count} hunger={snapshot.Agent.Hunger:F3} continuity=verified");
        trainingSmokeFinished = true;
        paused = true;
        ShowNotice($"Saved at step {state.Step:N0}", 10);
        if (capture is null && progressCaptureDirectory is null)
        {
            // Exercise the actual Godot input path, including the modifier guard.
            _UnhandledKeyInput(new InputEventKey { PhysicalKeycode = Key.R, Pressed = true });
            if (trainingSession.Snapshot().Training.Step != state.Step) throw new InvalidOperationException("Unmodified R reset the life.");
            _UnhandledKeyInput(new InputEventKey { PhysicalKeycode = Key.R, Pressed = true, CtrlPressed = true, ShiftPressed = true });
            TrainingVisualizationSnapshot reset = trainingSession.Snapshot();
            if (reset.World != initialSmokeWorld || reset.Training.Life != state.Life + 1 || reset.Training.Step != 0 ||
                reset.Training.Age != 0 || reset.Training.ReplayCount != 0 || reset.Training.OptimizationUpdates != 0 ||
                reset.Training.LastLoss != 0 || reset.Training.Epsilon != smokeOptions!.Learning.EpsilonStart ||
                reset.Training.RecentMetrics.Steps != 0 || reset.History.Count != 0 || reset.Trail.Count != 0)
                throw new InvalidOperationException("Ctrl+Shift+R did not create a fresh life.");
            GD.Print("ARTIFICIALLIFE_NEW_LIFE_SMOKE_OK Ctrl+Shift+R body/world/counters/replay/optimizer reset");
            GetTree().Quit();
        }
    }

    private void ValidateContinuousSmoke()
    {
        ExperimentOptions options = smokeOptions!;
        foreach (TrainingSample sample in liveSnapshot!.Trail)
        {
            TrainingSample previous = previousSmokeSample!;
            if (sample.Step <= previous.Step) continue;
            double distance = Math.Sqrt(Math.Pow(sample.X - previous.X, 2) + Math.Pow(sample.Y - previous.Y, 2));
            double time = initialSmokeWorld!.SimulationTime + sample.Step * options.World.TimeStep;
            double strength = options.Temperature.FireStrength * (1 + options.Temperature.OscillationAmplitude *
                Math.Sin(2 * Math.PI * time / options.Temperature.OscillationPeriod));
            double radiusSquared = Math.Pow(sample.X - options.World.Width / 2, 2) + Math.Pow(sample.Y - options.World.Height / 2, 2);
            double equilibrium = options.Temperature.Ambient + strength * Math.Exp(-radiusSquared / (2 * Math.Pow(options.Temperature.FireRadius, 2)))
                + options.Temperature.HeatProduction / options.Temperature.HeatTransfer;
            double body = equilibrium + (previous.BodyTemperature - equilibrium) * Math.Exp(-options.Temperature.HeatTransfer * options.World.TimeStep);
            if (sample.Step != previous.Step + 1 || sample.Terminal || sample.Episode != 1 ||
                distance > options.World.MovementSpeed * options.World.TimeStep + 1e-6 || Math.Abs(sample.BodyTemperature - body) > 1e-6)
                throw new InvalidOperationException($"Continuous life broke at step {sample.Step}.");
            previousSmokeSample = sample;
        }
        TrainingViewModel state = liveSnapshot.Training;
        if (state.Life != 1 || state.Age != state.Step || previousSmokeSample!.Step != state.Step ||
            state.ReplayCount != Math.Min(state.Step, options.Learning.ReplayCapacity) ||
            Math.Abs(liveSnapshot.World.SimulationTime - initialSmokeWorld!.SimulationTime - state.Step * options.World.TimeStep) > 1e-6)
            throw new InvalidOperationException("Continuous life counters/time/replay diverged.");
    }

    private void HandleTrainingKey(InputEventKey key)
    {
        if (trainingSession is null)
        {
            return;
        }
        try
        {
            switch (key.PhysicalKeycode == Key.None ? key.Keycode : key.PhysicalKeycode)
            {
                case Key.Space: paused = !paused; break;
                case Key.Equal:
                case Key.Plus:
                case Key.KpAdd:
                    trainingSpeed = (TrainingSpeed)Math.Min((int)trainingSpeed + 1, (int)TrainingSpeed.Max);
                    trainingCredits = 0;
                    break;
                case Key.Minus:
                case Key.KpSubtract:
                    trainingSpeed = (TrainingSpeed)Math.Max((int)trainingSpeed - 1, 0);
                    trainingCredits = 0;
                    break;
                case Key.E:
                    trainingSession.ToggleEvaluation();
                    trainingCredits = 0;
                    RefreshTrainingSnapshot();
                    ShowNotice(trainingSession.Evaluating ? "Frozen policy · no replay or optimizer updates" : "Training resumed from the suspended state", 4);
                    break;
                case Key.S:
                    trainingSession.SaveCheckpoint(checkpoint);
                    RefreshTrainingSnapshot();
                    ShowNotice($"Saved {System.IO.Path.GetFileName(checkpoint)} · step {liveSnapshot!.Training.Step:N0}", 4);
                    break;
                case Key.R when key.CtrlPressed && key.ShiftPressed:
                    trainingSession.ResetTraining();
                    trainingCredits = 0;
                    actualStepsPerSecond = 0;
                    rateSteps = 0;
                    rateElapsed = 0;
                    RefreshTrainingSnapshot();
                    ShowNotice("New life · fresh brain, replay, body and world", 4);
                    break;
                case Key.R: ShowNotice("Hold Ctrl + Shift + R to reset all learning", 3); break;
            }
        }
        catch (Exception exception)
        {
            ShowNotice($"Operation failed: {exception.Message}", 6);
            GD.PushError(exception.Message);
        }
    }

    private void ShowNotice(string text, double seconds)
    {
        notice = text;
        noticeSeconds = seconds;
    }

    private static string SpeedLabel(TrainingSpeed value) => value switch
    {
        TrainingSpeed.Normal => "1×",
        TrainingSpeed.TenTimes => "10×",
        TrainingSpeed.HundredTimes => "100×",
        TrainingSpeed.Max => "Max",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private void DrawTrainingHud(WorldViewModel world)
    {
        TrainingViewModel state = liveSnapshot!.Training;
        EvaluationMetrics recent = state.RecentMetrics;
        Label(state.Evaluating ? "EVALUATION · FROZEN POLICY" : "LIVE TRAINING", 786, 115, 15, Accent);
        Label($"{world.Agent.BodyTemperature:F2} °C", 786, 163, 36,
            Math.Abs(world.Agent.BodyTemperature - world.TargetTemperature) <= world.ComfortHalfWidth ? Accent : new Color("ffb16c"));
        Label($"Body temperature · target {world.TargetTemperature:F1} °C", 786, 190, 15, Muted);
        Label($"Life {state.Life:N0} · Age {state.Age:N0} · TrainingStep {state.Step:N0}", 786, 218, 15, TextColor);
        Label($"Environment {world.Agent.LocalTemperature:F2} °C   ·   Reward {world.Reward:+0.000;-0.000;0.000}", 786, 246, 14, Muted);
        DrawLine(new Vector2(786, 263), new Vector2(1180, 263), new Color("304356"));
        TrainingMetric("Recent mean error", $"{recent.MeanAbsoluteError:F3} °C", 289, Accent);
        TrainingMetric("Comfort time", $"{recent.ComfortPercent:F1}%", 317, Accent);
        TrainingMetric("Recent mean reward", $"{recent.MeanReward:+0.000;-0.000;0.000}", 345, TextColor);
        TrainingMetric("Epsilon", $"{state.Epsilon:F3}", 373, TextColor);
        TrainingMetric("Last loss", state.OptimizationUpdates == 0 ? "warm-up" : $"{state.LastLoss:F4}", 401, TextColor);
        TrainingMetric("Replay", $"{state.ReplayCount:N0} / {state.ReplayCapacity:N0}", 429, TextColor);
        Label($"BODY TEMPERATURE · {recent.Steps:N0}-step metric window", 786, 456, 12, Muted);
        DrawTrainingGraphs(world);
        Label($"{(paused ? "Paused" : "Running")} · {SpeedLabel(trainingSpeed)} · {actualStepsPerSecond:N0} steps/s", 786, 680, 15, TextColor);
        Label("Space  pause / resume    + / −  speed", 786, 711, 14, Muted);
        Label("E  frozen evaluation       S  save checkpoint", 786, 738, 14, Muted);
        Label("Ctrl + Shift + R  start a new life", 786, 765, 14, Muted);
        if (error is not null || noticeSeconds > 0)
        {
            string message = error ?? notice;
            Label(message[..Math.Min(message.Length, 58)], 786, 792, 12, error is null ? Accent : new Color("ffb16c"));
        }
    }

    private void TrainingMetric(string title, string value, float y, Color color)
    {
        Label(title, 786, y, 15, Muted);
        float width = ThemeDB.FallbackFont.GetStringSize(value, fontSize: 16).X;
        Label(value, 1180 - width, y, 16, color);
    }

    private void DrawTrainingGraphs(WorldViewModel world)
    {
        TrainingSample[] samples = liveSnapshot!.History.ToArray();
        double span = samples.Length == 0 ? 2 : Math.Max(2, samples.Max(sample => sample.AbsoluteError) * 1.15);
        var bodyRect = new Rect2(786, 466, 394, 81);
        DrawGraph(samples, bodyRect, world.TargetTemperature - span, world.TargetTemperature + span,
            sample => sample.BodyTemperature, world.TargetTemperature, world.ComfortHalfWidth);
        Label("ABSOLUTE BODY ERROR · °C", 786, 575, 12, Muted);
        var errorRect = new Rect2(786, 585, 394, 70);
        DrawGraph(samples, errorRect, 0, Math.Max(1.5, span), sample => sample.AbsoluteError, 0, world.ComfortHalfWidth);
    }

    private void DrawGraph(TrainingSample[] samples, Rect2 rect, double minimum, double maximum,
        Func<TrainingSample, double> value, double target, double band)
    {
        DrawRect(rect, new Color("101b29"));
        float Y(double number) => rect.End.Y - (float)((Math.Clamp(number, minimum, maximum) - minimum) / (maximum - minimum)) * rect.Size.Y;
        float bandTop = Y(target + band);
        float bandBottom = Y(target - band);
        DrawRect(new Rect2(rect.Position.X, bandTop, rect.Size.X, bandBottom - bandTop), new Color(0.4f, 0.87f, 0.74f, 0.1f));
        DrawLine(new Vector2(rect.Position.X, Y(target)), new Vector2(rect.End.X, Y(target)), new Color(0.4f, 0.87f, 0.74f, 0.5f));
        for (int index = 1; index < samples.Length; index++)
        {
            float x1 = rect.Position.X + (index - 1) * rect.Size.X / (visualOptions.HistoryLength - 1);
            float x2 = rect.Position.X + index * rect.Size.X / (visualOptions.HistoryLength - 1);
            DrawLine(new Vector2(x1, Y(value(samples[index - 1]))), new Vector2(x2, Y(value(samples[index]))), Accent, 1.5f, antialiased: true);
        }
        Label($"{maximum:F1}", rect.Position.X + 4, rect.Position.Y + 12, 10, Muted);
        Label($"{minimum:F1}", rect.Position.X + 4, rect.End.Y - 3, 10, Muted);
    }

    // Диагностика smoke снимает настоящий viewport в нескольких точках одного непрерывного обучения.
    private void ObserveTrainingProgress()
    {
        TrainingViewModel state = liveSnapshot!.Training;
        if (state.Step >= nextTrainingLog)
        {
            GD.Print($"LIVE life={state.Life} age={state.Age} step={state.Step} epsilon={state.Epsilon:F3} error={state.RecentMetrics.MeanAbsoluteError:F3} comfort={state.RecentMetrics.ComfortPercent:F1}% loss={state.LastLoss:F4} apples={liveSnapshot!.World.Apples.Count} spawned={liveSnapshot.World.TotalApplesSpawned} eaten={liveSnapshot.World.TotalApplesSpawned - liveSnapshot.World.Apples.Count} hunger={liveSnapshot.World.Agent.Hunger:F3}");
            nextTrainingLog = state.Step + 5000;
        }
        if (progressCaptureDirectory is null) return;
        if (captureStages.TryPeek(out int stage) && state.Step == stage)
        {
            captureStages.Dequeue();
            observationStages.Add(state);
            string path = System.IO.Path.Combine(progressCaptureDirectory!, $"training-{stage:D6}.png");
            RenderingServer.FramePostDraw += CaptureStage;
            void CaptureStage()
            {
                RenderingServer.FramePostDraw -= CaptureStage;
                Error result = GetViewport().GetTexture().GetImage().SavePng(path);
                if (result != Error.Ok)
                {
                    throw new IOException($"Cannot capture live stage: {result}");
                }
                GD.Print($"LIVE_CAPTURE step={stage}: {path}");
            }
        }
        if (trainingSmokeFinished && captureStages.Count == 0)
        {
            double[] durations = workDurations.Order().ToArray();
            double[] frameTimes = frameDurations.Order().ToArray();
            var report = new
            {
                Stages = observationStages,
                TrainingSeconds = trainingWallClock.Elapsed.TotalSeconds,
                WorkBatchCount = durations.Length,
                MeanWorkMilliseconds = durations.Length == 0 ? 0 : durations.Average(),
                P95WorkMilliseconds = durations.Length == 0 ? 0 : durations[(int)((durations.Length - 1) * 0.95)],
                MaximumWorkMilliseconds = durations.Length == 0 ? 0 : durations[^1],
                P95FrameMilliseconds = frameTimes.Length == 0 ? 0 : frameTimes[(int)((frameTimes.Length - 1) * 0.95)]
            };
            System.IO.File.WriteAllText(System.IO.Path.Combine(progressCaptureDirectory!, "observation.json"), JsonSerializer.Serialize(report, ExperimentOptions.Json));
            // Выход после последнего нарисованного кадра, иначе финальный screenshot может не успеть сохраниться.
            if (capture is null)
            {
                RenderingServer.FramePostDraw += FinishObservation;
                void FinishObservation()
                {
                    RenderingServer.FramePostDraw -= FinishObservation;
                    GetTree().Quit();
                }
            }
            progressCaptureDirectory = null;
        }
    }
}
