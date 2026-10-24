using System.Diagnostics;
using System.Drawing;

namespace ArtificialLife.Launcher;

internal sealed class LauncherForm : Form
{
    private const string DefaultConfig = "configs/default.json";
    private const string VisualConfig = "configs/live-training.json";
    private readonly string repositoryRoot;
    private readonly ComboBox mode = new();
    private readonly ComboBox speed = new();
    private readonly TextBox config = new();
    private readonly TextBox checkpoint = new();
    private readonly NumericUpDown steps = new();
    private readonly Label configLabel = new();
    private readonly Label speedLabel = new();
    private readonly Label stepsLabel = new();
    private readonly Label description = new();

    public LauncherForm(string root)
    {
        repositoryRoot = root;
        Text = "ArtificialLife — лаунчер";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 460);
        ClientSize = new Size(720, 500);
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 3,
            RowCount = 9,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        for (int row = 0; row < 9; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 0 ? 56 : row == 7 ? 66 : 44));
        Controls.Add(layout);

        var title = new Label
        {
            Text = "ArtificialLife",
            Font = new Font("Segoe UI Semibold", 22),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 3);

        AddCaption(layout, "Режим запуска", 1);
        mode.DropDownStyle = ComboBoxStyle.DropDownList;
        mode.Items.AddRange(["Просмотр готовой модели", "Живое обучение в окне", "Обучение в терминале"]);
        mode.SelectedIndex = 0;
        mode.SelectedIndexChanged += (_, _) => UpdateMode();
        layout.Controls.Add(mode, 1, 1);
        layout.SetColumnSpan(mode, 2);

        configLabel.Text = "Конфигурация";
        configLabel.Dock = DockStyle.Fill;
        configLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(configLabel, 0, 2);
        config.Text = DefaultConfig;
        config.Dock = DockStyle.Fill;
        layout.Controls.Add(config, 1, 2);
        var chooseConfig = new Button { Text = "Обзор…", Dock = DockStyle.Fill };
        chooseConfig.Click += (_, _) => ChooseConfig();
        layout.Controls.Add(chooseConfig, 2, 2);

        speedLabel.Text = "Скорость";
        speedLabel.Dock = DockStyle.Fill;
        speedLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(speedLabel, 0, 3);
        speed.DropDownStyle = ComboBoxStyle.DropDownList;
        speed.Items.AddRange(["Обычная — 1×", "Быстрая — 10×", "Очень быстрая — 100×", "Максимальная"]);
        speed.SelectedIndex = 1;
        speed.Dock = DockStyle.Left;
        speed.Width = 250;
        layout.Controls.Add(speed, 1, 3);
        layout.SetColumnSpan(speed, 2);

        AddCaption(layout, "Чекпойнт", 4);
        checkpoint.Text = "artifacts/checkpoints/thermal";
        checkpoint.Dock = DockStyle.Fill;
        layout.Controls.Add(checkpoint, 1, 4);
        var chooseCheckpoint = new Button { Text = "Обзор…", Dock = DockStyle.Fill };
        chooseCheckpoint.Click += (_, _) => ChooseCheckpoint();
        layout.Controls.Add(chooseCheckpoint, 2, 4);

        stepsLabel.Text = "Шаги обучения";
        stepsLabel.Dock = DockStyle.Fill;
        stepsLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(stepsLabel, 0, 5);
        steps.Minimum = 1;
        steps.Maximum = 100_000_000;
        steps.Increment = 10_000;
        steps.Value = 100_000;
        steps.ThousandsSeparator = true;
        steps.Width = 180;
        steps.TextAlign = HorizontalAlignment.Right;
        layout.Controls.Add(steps, 1, 5);

        description.Text = "Запускает сохранённую модель. Для первого запуска сначала обучите её или выберите существующий чекпойнт.";
        description.Dock = DockStyle.Fill;
        description.ForeColor = Color.FromArgb(80, 90, 102);
        description.TextAlign = ContentAlignment.MiddleLeft;
        description.AutoSize = false;
        layout.Controls.Add(description, 0, 7);
        layout.SetColumnSpan(description, 3);

        var start = new Button
        {
            Text = "Запустить",
            Width = 150,
            Height = 42,
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.None
        };
        start.Click += (_, _) => Launch();
        layout.Controls.Add(start, 2, 8);
        layout.SetColumnSpan(start, 1);
        AcceptButton = start;

        UpdateMode();
    }

    private static void AddCaption(TableLayoutPanel layout, string text, int row)
    {
        layout.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
    }

    private void UpdateMode()
    {
        bool viewing = mode.SelectedIndex == 0;
        bool live = mode.SelectedIndex == 1;
        config.Enabled = configLabel.Enabled = !viewing;
        speed.Enabled = speedLabel.Enabled = live;
        steps.Enabled = stepsLabel.Enabled = !viewing && !live;
        if (viewing && checkpoint.Text.EndsWith("live-thermal", StringComparison.OrdinalIgnoreCase))
            checkpoint.Text = "artifacts/checkpoints/thermal";
        else if (live && checkpoint.Text.EndsWith("thermal", StringComparison.OrdinalIgnoreCase) &&
                 !checkpoint.Text.EndsWith("live-thermal", StringComparison.OrdinalIgnoreCase))
            checkpoint.Text = "artifacts/checkpoints/live-thermal";

        description.Text = mode.SelectedIndex switch
        {
            0 => "Запускает сохранённую модель. Для первого запуска сначала обучите её или выберите существующий чекпойнт.",
            1 => "Открывает окно Godot и продолжает обучение в реальном времени. Нажмите S в окне симуляции, чтобы сохранить модель.",
            _ => "Обучает модель без окна Godot и сохраняет чекпойнт после завершения заданного числа шагов."
        };
    }

    private void ChooseConfig()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите конфигурацию эксперимента",
            InitialDirectory = Path.Combine(repositoryRoot, "configs"),
            Filter = "Конфигурация JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = Path.GetFileName(ResolvePath(config.Text))
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            config.Text = MakeRelative(dialog.FileName);
    }

    private void ChooseCheckpoint()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите папку чекпойнта",
            InitialDirectory = Directory.Exists(ResolvePath(checkpoint.Text))
                ? ResolvePath(checkpoint.Text)
                : Path.Combine(repositoryRoot, "artifacts", "checkpoints")
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            checkpoint.Text = MakeRelative(dialog.SelectedPath);
    }

    private void Launch()
    {
        try
        {
            string selectedConfig = ResolvePath(config.Text);
            string selectedCheckpoint = ResolvePath(checkpoint.Text);
            if (mode.SelectedIndex != 0 && !File.Exists(selectedConfig))
                throw new FileNotFoundException("Файл конфигурации не найден.", selectedConfig);
            if (string.IsNullOrWhiteSpace(checkpoint.Text))
                throw new InvalidOperationException("Укажите папку чекпойнта.");

            var args = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(repositoryRoot, "tools", "run-godot.ps1")
            };
            switch (mode.SelectedIndex)
            {
                case 0:
                    args.AddRange(["-Checkpoint", selectedCheckpoint]);
                    break;
                case 1:
                    args.AddRange(["-Train", "-Config", selectedConfig, "-VisualConfig", Path.Combine(repositoryRoot, VisualConfig),
                        "-TrainingSpeed", GetSpeedArgument(), "-Checkpoint", selectedCheckpoint]);
                    break;
                case 2:
                    args[args.IndexOf("-File") + 1] = Path.Combine(repositoryRoot, "tools", "train.ps1");
                    args.AddRange(["-Config", selectedConfig, "-Steps", ((long)steps.Value).ToString(), "-Checkpoint", selectedCheckpoint]);
                    break;
            }

            string powershell = FindPowerShell();
            var startInfo = new ProcessStartInfo(powershell)
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            foreach (string argument in args)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось запустить", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string GetSpeedArgument() => speed.SelectedIndex switch
    {
        0 => "1x",
        1 => "10x",
        2 => "100x",
        _ => "Max"
    };

    private string ResolvePath(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(Path.Combine(repositoryRoot, path));

    private string MakeRelative(string path) => Path.GetRelativePath(repositoryRoot, path);

    private static string FindPowerShell()
    {
        string windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "pwsh.exe";
    }
}
