using ArtificialLife.Application;
using ArtificialLife.Core;
using Godot;

namespace ArtificialLife.Godot;

public partial class Main
{
    private Window packageDialog = null!;
    private FileDialog packageFiles = null!, packageFolder = null!;
    private ItemList activePackages = null!;
    private TextEdit packageStatus = null!;
    private Button importPackageFile = null!, importPackageFolder = null!;
    private bool packageImportBusy, packageUiClosing;
    private RuntimeSession? PackageRuntime => trainingSession?.Runtime ?? session?.Runtime;

    private void InitializePackageUi(string root)
    {
        var layer = new CanvasLayer { Name = "PackageUi" }; AddChild(layer);
        var button = new Button { Name = "PackagesButton", Text = "Пакеты…", Position = new(1040, 55), Size = new(140, 28) };
        layer.AddChild(button);
        packageDialog = new Window { Name = "PackageManager", Title = "Пакеты симуляции", Size = new(800, 520), MinSize = new(720, 460), Visible = false, WrapControls = false, Transient = true };
        AddChild(packageDialog);
        packageDialog.CloseRequested += () => packageDialog.Hide();
        var margin = new MarginContainer(); packageDialog.AddChild(margin);
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.OffsetLeft = 16; margin.OffsetTop = 16; margin.OffsetRight = -16; margin.OffsetBottom = -16;
        var content = new VBoxContainer(); margin.AddChild(content);
        content.AddChild(new global::Godot.Label
        {
            Text = "Перетащите ZIP, module.json или папку пакета в окно симуляции.\nИли выберите пакет кнопками ниже. Мир и обучение продолжаются.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        var buttons = new HBoxContainer(); content.AddChild(buttons);
        importPackageFile = new Button { Name = "ImportPackageFile", Text = "Добавить файл…" };
        importPackageFolder = new Button { Name = "ImportPackageFolder", Text = "Добавить папку…" };
        buttons.AddChild(importPackageFile); buttons.AddChild(importPackageFolder);
        content.AddChild(new global::Godot.Label { Text = "Подключённые пакеты" });
        activePackages = new ItemList { Name = "ActivePackages", CustomMinimumSize = new(0, 140), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        content.AddChild(activePackages);
        packageStatus = new TextEdit { Name = "PackageImportStatus", Editable = false, CustomMinimumSize = new(0, 112), Text = "Выберите пакет для добавления." };
        packageStatus.AddThemeColorOverride("font_readonly_color", TextColor);
        content.AddChild(packageStatus);
        var close = new Button { Text = "Закрыть", SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        close.Pressed += () => packageDialog.Hide(); content.AddChild(close);
        packageFiles = new FileDialog
        {
            Name = "PackageFilePicker", ModeOverridesTitle = false, Title = "Добавить пакеты", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFiles, Filters = ["module.json;Манифест пакета", "*.zip;ZIP-пакеты"], CurrentDir = root
        };
        packageFolder = new FileDialog
        {
            Name = "PackageFolderPicker", ModeOverridesTitle = false, Title = "Выбрать папку с module.json", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenDir, CurrentDir = root
        };
        packageDialog.AddChild(packageFiles); packageDialog.AddChild(packageFolder);
        button.Pressed += () => { RefreshPackageList(); UpdatePackageControls(); packageDialog.PopupCentered(); };
        importPackageFile.Pressed += () => packageFiles.PopupCentered(new Vector2I(900, 600));
        importPackageFolder.Pressed += () => packageFolder.PopupCentered(new Vector2I(900, 600));
        packageFiles.FilesSelected += ImportPackages;
        packageFolder.DirSelected += path => ImportPackages([path]);
        GetWindow().FilesDropped += ImportPackages;
        packageDialog.FilesDropped += ImportPackages;
        UpdatePackageControls();
    }

    private void RefreshPackageList()
    {
        activePackages.Clear();
        foreach (var package in (PackageRuntime?.Packages ?? Array.Empty<ModuleManifest>()).OrderBy(p => p.Id))
            activePackages.AddItem($"{package.Id}   {package.Version}");
    }

    private void UpdatePackageControls()
    {
        bool unavailable = PackageRuntime is null || PackageRuntime.Fault is not null || trainingSession?.Evaluating == true;
        importPackageFile.Disabled = importPackageFolder.Disabled = packageImportBusy || unavailable;
        if (!packageImportBusy && trainingSession?.Evaluating == true)
            packageStatus.Text = "Добавление недоступно в Frozen evaluation. Закройте окно и нажмите E, чтобы вернуться к обучению.";
        else if (!packageImportBusy && PackageRuntime is null)
            packageStatus.Text = "Сначала запустите симуляцию.";
        else if (!packageImportBusy && PackageRuntime?.Fault is { } fault)
            packageStatus.Text = "Симуляция остановлена из-за ошибки:\n" + fault;
        else if (!packageImportBusy && !unavailable && (packageStatus.Text.StartsWith("Сначала", StringComparison.Ordinal) ||
            packageStatus.Text.StartsWith("Добавление недоступно", StringComparison.Ordinal) || packageStatus.Text.StartsWith("Симуляция остановлена", StringComparison.Ordinal)))
            packageStatus.Text = "Выберите пакет для добавления.";
    }

    private async void ImportPackages(string[] paths)
    {
        if (packageUiClosing || packageImportBusy) return;
        packageDialog.PopupCentered();
        var runtime = PackageRuntime;
        if (runtime is null || trainingSession?.Evaluating == true || runtime.Fault is not null) { UpdatePackageControls(); return; }
        packageImportBusy = true;
        packageStatus.Text = "Проверка и импорт пакетов…";
        UpdatePackageControls();
        try
        {
            // File work does not touch runtime objects. Activation resumes on Godot's main thread.
            string packageRoot = runtime.Options.PackageRoot;
            var imported = await Task.Run(() => new RuntimePackageImporter(packageRoot).Import(paths));
            if (packageUiClosing) return;
            if (!ReferenceEquals(runtime, PackageRuntime) || trainingSession?.Evaluating == true)
            {
                packageStatus.Text = "Пакеты сохранены. Симуляция сменилась во время импорта; выберите пакеты ещё раз для подключения.";
                return;
            }
            var result = runtime.Activate(new("gui-" + Guid.NewGuid().ToString("N"), imported.Select(p => p.Reference).ToArray()));
            string names = string.Join(", ", imported.Select(p => $"{p.Reference.Id}@{p.Reference.Version}"));
            packageStatus.Text = result.Accepted
                ? $"Подключено: {names}\nМир и обучение сохранены."
                : $"Пакеты сохранены, подключение отклонено:\n{result.Message}";
            ShowNotice(result.Accepted ? $"Packages connected: {names}" : $"Package rejected: {result.Message}", 8);
            if (trainingSession is not null) RefreshTrainingSnapshot();
            else if (session is not null) snapshot = session.Snapshot();
            RefreshPackageList(); QueueRedraw();
        }
        catch (Exception exception)
        {
            if (!packageUiClosing) packageStatus.Text = "Не удалось добавить пакет:\n" + exception.Message;
        }
        finally
        {
            packageImportBusy = false;
            if (!packageUiClosing) UpdatePackageControls();
        }
    }

    private void DisposePackageUi()
    {
        packageUiClosing = true;
        GetWindow().FilesDropped -= ImportPackages;
    }
}
