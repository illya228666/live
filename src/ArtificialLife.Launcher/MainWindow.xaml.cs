using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ArtificialLife.Launcher;

public partial class MainWindow : Window
{
    private const string DefaultConfig = "configs/default.json";
    private const string VisualConfig = "configs/live-training.json";
    private readonly string repositoryRoot = FindRepositoryRoot();

    public MainWindow()
    {
        InitializeComponent();
        UpdateMode();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMode();

    private void UpdateMode()
    {
        if (ModeBox is null || DescriptionText is null)
            return;

        bool viewing = ModeBox.SelectedIndex == 0;
        bool live = ModeBox.SelectedIndex == 1;
        ConfigBox.IsEnabled = ChooseConfigButton.IsEnabled = ConfigLabel.IsEnabled = !viewing;
        SpeedBox.IsEnabled = SpeedLabel.IsEnabled = live;
        StepsBox.IsEnabled = StepsLabel.IsEnabled = !viewing && !live;

        if (viewing && CheckpointBox.Text.EndsWith("live-thermal", StringComparison.OrdinalIgnoreCase))
            CheckpointBox.Text = "artifacts/checkpoints/thermal";
        else if (live && CheckpointBox.Text.EndsWith("thermal", StringComparison.OrdinalIgnoreCase) &&
                 !CheckpointBox.Text.EndsWith("live-thermal", StringComparison.OrdinalIgnoreCase))
            CheckpointBox.Text = "artifacts/checkpoints/live-thermal";

        DescriptionText.Text = ModeBox.SelectedIndex switch
        {
            0 => "Запускает Godot с сохранённой моделью. Для первого запуска сначала обучите её или выберите существующий чекпойнт.",
            1 => "Открывает Godot и продолжает обучение в реальном времени. Нажмите S в окне симуляции, чтобы сохранить веса.",
            _ => "Обучает модель без окна Godot и сохраняет чекпойнт после заданного числа шагов."
        };
    }

    private void ChooseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите конфигурацию эксперимента",
            InitialDirectory = Path.Combine(repositoryRoot, "configs"),
            Filter = "Конфигурация JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = Path.GetFileName(ResolvePath(ConfigBox.Text))
        };
        if (dialog.ShowDialog(this) == true)
            ConfigBox.Text = MakeRelative(dialog.FileName);
    }

    private void ChooseCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку чекпойнта",
            InitialDirectory = Directory.Exists(ResolvePath(CheckpointBox.Text))
                ? ResolvePath(CheckpointBox.Text)
                : Path.Combine(repositoryRoot, "artifacts", "checkpoints")
        };
        if (dialog.ShowDialog(this) == true)
            CheckpointBox.Text = MakeRelative(dialog.FolderName);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string selectedConfig = ResolvePath(ConfigBox.Text);
            string selectedCheckpoint = ResolvePath(CheckpointBox.Text);
            if (ModeBox.SelectedIndex != 0 && !File.Exists(selectedConfig))
                throw new FileNotFoundException("Файл конфигурации не найден.", selectedConfig);
            if (string.IsNullOrWhiteSpace(CheckpointBox.Text))
                throw new InvalidOperationException("Укажите папку чекпойнта.");

            long stepCount = 0;
            if (ModeBox.SelectedIndex == 2 &&
                (!long.TryParse(StepsBox.Text, out stepCount) || stepCount < 1))
                throw new InvalidOperationException("Число шагов должно быть положительным целым числом.");

            string script = ModeBox.SelectedIndex == 2 ? "train.ps1" : "run-godot.ps1";
            var arguments = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(repositoryRoot, "tools", script)
            };
            switch (ModeBox.SelectedIndex)
            {
                case 0:
                    arguments.AddRange(["-Checkpoint", selectedCheckpoint]);
                    break;
                case 1:
                    arguments.AddRange(["-Train", "-Config", selectedConfig,
                        "-VisualConfig", Path.Combine(repositoryRoot, VisualConfig),
                        "-TrainingSpeed", GetSpeedArgument(), "-Checkpoint", selectedCheckpoint]);
                    break;
                case 2:
                    arguments.AddRange(["-Config", selectedConfig, "-Steps", stepCount.ToString(),
                        "-Checkpoint", selectedCheckpoint]);
                    break;
            }

            var startInfo = new ProcessStartInfo(FindPowerShell())
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось запустить",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetSpeedArgument() => SpeedBox.SelectedIndex switch
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

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArtificialLife.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "tools", "run-godot.ps1")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Не удалось найти корень репозитория ArtificialLife.");
    }
}
