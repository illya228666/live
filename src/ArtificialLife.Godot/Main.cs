using ArtificialLife.Packages;
using ArtificialLife.Core;
using ArtificialLife.Observation;
using Godot;
using System.Text.Json;
using System.Security.Cryptography;

namespace ArtificialLife.Godot;

public partial class Main : Node2D
{
    private readonly ObservationClient client = new();
    private ObservationMessage? current, previous;
    private PresentationPackage[]? pending;
    private PresentationHost? presentations;
    private Camera2D camera = null!;
    private OptionButton selector = null!;
    private global::Godot.Label hud = null!, inspector = null!;
    private string? capture;
    private bool visualsReady;
    private Button packageButton = null!;
    private MetricsPanel metricsPanel = null!;
    private readonly Queue<Vector2> trail = new();
    private string cache = "", loading = "";
    private string sessionListKey = "";
    private SessionDescriptor[] listedSessions = [];
    private string? evaluationParent, evaluationParentEpoch;
    private bool followEvaluation, leaveEvaluation;
    private double elapsed, refresh;
    private int frames, smokeFrames;
    private long smokeMetadata;
    private long selectedEntity;
    private readonly Rect2 bounds = new(0, 0, 700, 700);
    public override void _Ready()
    {
        if (VisualPackTool.TryRun(OS.GetCmdlineUserArgs(), GetTree()))
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            return;
        }
        Engine.MaxFps = 60;
        string? session = null, endpoint = null;
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Missing option value.");
            switch (args[i]) { case "--session": session = args[i+1]; break; case "--endpoint": endpoint = args[i+1]; break; case "--capture": capture = args[i+1]; break; case "--smoke-frames": smokeFrames = int.Parse(args[i+1]); break; case "--smoke-metadata": smokeMetadata = long.Parse(args[i+1]); break; default: throw new ArgumentException("Unknown viewer option."); }
        }
        cache = Path.Combine(OS.GetUserDataDir(), "packages");
        camera = new Camera2D { Position = new(350, 350) }; AddChild(camera);
        var ui = new CanvasLayer(); AddChild(ui);
        selector = new OptionButton { Position = new(16, 12), Size = new(650, 32) }; ui.AddChild(selector);
        selector.ItemSelected += i => { if (i < listedSessions.Length) client.Select(listedSessions[(int)i].SessionId); };
        packageButton = new Button { Text = "Packages…", Position = new(690,12) }; ui.AddChild(packageButton);
        var files = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFiles, Filters = ["*.zip;Packages", "module.json;Module manifest"] }; AddChild(files);
        packageButton.Pressed += () => files.PopupCentered(new(900,600));
        files.FilesSelected += paths => client.Send("update", string.Join(";", paths));
        GetWindow().FilesDropped += paths => client.Send("update", string.Join(";", paths));
        hud = new global::Godot.Label { Position = new(16, 54), MouseFilter = Control.MouseFilterEnum.Ignore }; ui.AddChild(hud);
        inspector = new global::Godot.Label { Position = new(850, 80), MouseFilter = Control.MouseFilterEnum.Ignore }; ui.AddChild(inspector);
        metricsPanel = new MetricsPanel { Position = new(16, 300), MouseFilter = Control.MouseFilterEnum.Ignore }; ui.AddChild(metricsPanel);
        _ = client.Run(session, endpoint);
    }
    private async Task LoadVisuals(ObservationMessage message)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var packages = new List<PresentationPackage>();
        foreach (var visual in message.State!.Visuals)
        {
            string resourceHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                visual.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { f.Path, f.Hash }))));
            string root = Path.Combine(cache, visual.Manifest.Id, visual.Manifest.Version, resourceHash);
            foreach (var resource in visual.Files) try
            {
                string path = PackageCatalog.FileWithin(root, resource.Path);
                if (File.Exists(path) && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).Equals(resource.Hash, StringComparison.OrdinalIgnoreCase)) continue;
                byte[] bytes = await http.GetByteArrayAsync(resource.Url);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(resource.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Resource checksum mismatch.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes);
                    try { File.Move(temporary, path, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(resource.Hash, StringComparison.OrdinalIgnoreCase)) { }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException) { /* Fallback rendering remains available. */ }
            packages.Add(new(visual.Manifest, root));
        }
        if (current?.Session.Epoch == message.Session.Epoch && current.MetadataRevision == message.MetadataRevision) Volatile.Write(ref pending, packages.ToArray());
    }
    public override void _Process(double delta)
    {
        elapsed += delta; refresh += delta;
        if (followEvaluation && evaluationParent is not null)
        {
            var child = client.Sessions.FirstOrDefault(s => s.ParentId == evaluationParent && s.Mode == "Evaluation");
            if (child is not null) { followEvaluation = false; client.Select(child.SessionId); }
        }
        if (refresh >= 1 || selector.ItemCount == 0 && client.Sessions.Length > 0)
        {
            refresh = 0;
            var list = client.Sessions;
            string key = string.Join(";", list.Select(s => s.SessionId + ":" + s.Mode));
            if (key != sessionListKey)
            {
                sessionListKey = key; listedSessions = list; selector.Clear();
                foreach (var s in list) { selector.AddItem(s.Mode + " · " + s.SessionId[..8]); if (s.SessionId == client.Selected) selector.Select(selector.ItemCount - 1); }
            }
            int selected = Array.FindIndex(listedSessions, s => s.SessionId == client.Selected);
            if (selected >= 0 && selector.Selected != selected) selector.Select(selected);
        }
        var message = Volatile.Read(ref client.Latest);
        if (message?.State is not null && (current is null || message.Session.Epoch != current.Session.Epoch || message.Sequence > current.Sequence))
        {
            if (current?.Session.Epoch != message.Session.Epoch) { trail.Clear(); metricsPanel.ClearHistory(); selectedEntity = 0; }
            previous = current?.Session.Epoch == message.Session.Epoch ? current : null;
            current = message; elapsed = 0;
            if (leaveEvaluation && message.Session.SessionId == evaluationParent)
            {
                leaveEvaluation = false;
                if (message.Session.Epoch == evaluationParentEpoch && message.State.Capabilities.Contains("evaluation")) client.Send("evaluation");
                evaluationParent = evaluationParentEpoch = null;
            }
            if (message.State.Frame.HasAgent)
            {
                var agent = message.State.Frame.Agent;
                trail.Enqueue(new((float)(agent.Position.X / message.State.Frame.Width * 700), (float)(agent.Position.Y / message.State.Frame.Height * 700)));
                while (trail.Count > 180) trail.Dequeue();
            }
            metricsPanel.Add(message.State.Metrics);
            string key = message.Session.Epoch + ":" + message.MetadataRevision;
            if (key != loading) { loading = key; visualsReady = false; Interlocked.Exchange(ref pending, null); presentations?.Dispose(); presentations = null; _ = LoadVisuals(message); }
        }
        var packages = Interlocked.Exchange(ref pending, null);
        if (packages is not null && current?.State is {} ready) { presentations ??= new(this, bounds); using var prepared = presentations.Prepare(packages, ready.Frame); prepared.Commit(); visualsReady = true; }
        packageButton.Disabled = current?.State?.Capabilities.Contains("update") != true;
        hud.Text = client.Status;
        if (presentations?.LastError is string visualError) hud.Text += "\nГрафический fallback: " + visualError;
        if (current?.State is {} state)
        {
            hud.Text += $"\n{current.Session.Mode} · возраст {state.Frame.Age:N0} · версия {state.Frame.Revision}\n{(state.Paused ? "Пауза" : "Работа")} · {(state.Speed == 0 ? "Max" : state.Speed + "×")}\n";
            if (state.Capabilities.Contains("pause")) hud.Text += "Пробел: пауза · +/− или 1–4: скорость · S: checkpoint\n";
            if (state.Capabilities.Contains("evaluation")) hud.Text += "E: оценка · Ctrl+Shift+R: новая жизнь\n";
            if (evaluationParent is not null && current.Session.ParentId == evaluationParent) hud.Text += "E: вернуться к обучению\n";
            if (state.Capabilities.Contains("reset")) hud.Text += "R: перезапустить мир\n";
            hud.Text += "Колесо: масштаб · средняя кнопка: перемещение · клик: компоненты · F12: PNG";
            metricsPanel.Visible = state.Controller is not null;
            if (state.Controller is {} c && current.Session.Mode is "Learning" or "Research") hud.Text += $"\nTraining {c.Steps:N0} · updates {c.Updates:N0} · replay {c.ReplayCount:N0} · epsilon {c.Epsilon:F3} · loss {c.Loss:F4}";
            hud.Text += "\n" + string.Join(" · ", state.Metrics.Select(m => m.Key + " " + m.Value.ToString("F3")));
            var entity = state.Frame.Entities.Append(state.Frame.Agent).FirstOrDefault(e => e?.Id == selectedEntity);
            inspector.Text = entity is null ? "Модули\n" + string.Join("\n", state.Packages.Select(p => p.Id + " @ " + p.Version)) : $"Entity {entity.Id}\n" + JsonSerializer.Serialize(entity.Components, new JsonSerializerOptions { WriteIndented = true });
        }
        QueueRedraw();
        if (current is not null) frames++;
        if (smokeFrames > 0 && frames >= smokeFrames && visualsReady && current!.MetadataRevision >= smokeMetadata)
        {
            try { VerifyUiSmoke(); }
            catch (Exception ex) { GD.PushError(ex.Message); SetProcess(false); GetTree().Quit(1); return; }
            GD.Print("VIEWER_SMOKE_OK age=" + current!.State!.Frame.Age + " metadata=" + current.MetadataRevision);
            GetTree().Quit();
        }
        if (capture is not null && frames >= 5 && visualsReady)
        {
            string path = capture; capture = null;
            RenderingServer.FramePostDraw += Save;
            void Save()
            {
                RenderingServer.FramePostDraw -= Save;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var result = GetViewport().GetTexture().GetImage().SavePng(path);
                if (result == Error.Ok)
                {
                    var seen = current!;
                    File.WriteAllText(path + ".json", JsonSerializer.Serialize(new CaptureMetadata(seen.State!.Frame.Age, seen.State.Frame.Revision, seen.Session.SessionId, seen.Session.Epoch, seen.Sequence), ObservationProtocol.Json));
                }
                GetTree().Quit(result == Error.Ok ? 0 : 1);
            }
        }
    }
    public override void _Draw()
    {
        if (current?.State is not {} state) return;
        DrawRect(bounds, new Color("101b29"));
        var frame = state.Frame;
        if (previous?.State is {} before)
        {
            var old = before.Frame.Entities.Append(before.Frame.Agent).Where(e => e is not null).ToDictionary(e => e.Id);
            EntitySnapshot Blend(EntitySnapshot e) { if (!old.TryGetValue(e.Id, out var p) || Math.Abs(p.Position.X-e.Position.X)+Math.Abs(p.Position.Y-e.Position.Y)>10 || e.Components.ContainsKey("visual.teleport")) return e; double t=Math.Clamp(elapsed/.05,0,1); return e with { Position=new(p.Position.X+(e.Position.X-p.Position.X)*t,p.Position.Y+(e.Position.Y-p.Position.Y)*t) }; }
            frame = frame with { Agent = frame.HasAgent ? Blend(frame.Agent) : null!, Entities = frame.Entities.Select(Blend).ToArray() };
        }
        foreach (var e in frame.Entities.Append(frame.Agent).Where(e => e is not null)) DrawCircle(new((float)(e.Position.X/frame.Width*700),(float)(e.Position.Y/frame.Height*700)),5,e.Id==selectedEntity ? Colors.Yellow : new Color("68dfbe"));
        var points = trail.ToArray();
        for (int i = 1; i < points.Length; i++) if (points[i].DistanceTo(points[i - 1]) < 100) DrawLine(points[i - 1], points[i], new Color(.4f, .87f, .74f, .25f), 1.5f, true);
        presentations?.Draw(frame);
    }
    public override void _UnhandledInput(InputEvent input)
    {
        if (input is InputEventMouseButton { Pressed:true } mouse)
        {
            if (mouse.ButtonIndex == MouseButton.WheelUp) camera.Zoom *= 1.1f;
            if (mouse.ButtonIndex == MouseButton.WheelDown) camera.Zoom /= 1.1f;
            if (mouse.ButtonIndex == MouseButton.Left && current?.State is {} state) { var p=GetGlobalMousePosition(); selectedEntity=state.Frame.Entities.Append(state.Frame.Agent).Where(e=>e is not null).OrderBy(e=>new Vector2((float)(e.Position.X/state.Frame.Width*700),(float)(e.Position.Y/state.Frame.Height*700)).DistanceSquaredTo(p)).FirstOrDefault()?.Id ?? 0; }
        }
        if (input is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Middle) != 0) camera.Position -= motion.Relative/camera.Zoom;
        if (input is not InputEventKey { Pressed:true, Echo:false } key) return;
        if (key.Keycode == Key.F12) capture=Path.Combine(OS.GetUserDataDir(),"capture-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".png");
        if (key.Keycode == Key.E && evaluationParent is not null && current?.Session.ParentId == evaluationParent)
        {
            leaveEvaluation = true;
            client.Select(evaluationParent);
            return;
        }
        if (current?.State is not {} active || active.Capabilities.Length==0) return;
        if (key.Keycode == Key.E && active.Capabilities.Contains("evaluation"))
        {
            followEvaluation = current.Session.Status != "suspended" && !followEvaluation;
            evaluationParent = followEvaluation ? current.Session.SessionId : null;
            evaluationParentEpoch = followEvaluation ? current.Session.Epoch : null;
            client.Send("evaluation");
            return;
        }
        if (key.Keycode is Key.Equal or Key.Plus or Key.KpAdd or Key.Minus or Key.KpSubtract)
        {
            string[] speeds = ["1x", "10x", "100x", "Max"];
            int index = active.Speed switch { 1 => 0, 10 => 1, 100 => 2, _ => 3 };
            int direction = key.Keycode is Key.Minus or Key.KpSubtract ? -1 : 1;
            client.Send("speed", speeds[Math.Clamp(index + direction, 0, 3)]);
            return;
        }
        string? op=key.Keycode switch { Key.Space=>active.Paused ? "resume":"pause", Key.S=>"checkpoint", Key.E=>"evaluation", Key.R when key.CtrlPressed && key.ShiftPressed=>"new-life", Key.R=>"reset", Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4=>"speed", _=>null };
        if (op is not null) client.Send(op,key.Keycode switch { Key.Key1=>"1x",Key.Key2=>"10x",Key.Key3=>"100x",Key.Key4=>"Max",_=>null });
    }
    private void VerifyUiSmoke()
    {
        Vector2 zoom = camera.Zoom, position = camera.Position;
        _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp });
        if (camera.Zoom.X <= zoom.X) throw new InvalidOperationException("Camera zoom did not respond.");
        camera.Zoom = zoom;
        _UnhandledInput(new InputEventMouseMotion { Relative = new(12, 8), ButtonMask = MouseButtonMask.Middle });
        if (camera.Position.IsEqualApprox(position)) throw new InvalidOperationException("Camera pan did not respond.");
        camera.Position = position;
        _UnhandledInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });
        var frame = current!.State!.Frame;
        var entity = frame.Entities.Append(frame.Agent).FirstOrDefault(e => e?.Id == selectedEntity);
        if ((frame.HasAgent || frame.Entities.Count > 0) && entity is null) throw new InvalidOperationException("Object selection failed.");
        if (entity is not null)
        {
            inspector.Text = JsonSerializer.Serialize(entity.Components, new JsonSerializerOptions { WriteIndented = true });
            if (entity.Components.Keys.Any(key => !inspector.Text.Contains(key, StringComparison.Ordinal))) throw new InvalidOperationException("Component inspector omitted data.");
        }
        GD.Print("VIEWER_UI_SMOKE_OK camera/selection/components");
    }
    public override void _ExitTree() { client.Dispose(); presentations?.Dispose(); }
}
