using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ArtificialLife.Observation;
using Microsoft.Win32;

namespace ArtificialLife.Launcher;

public partial class MainWindow : Window
{
    private readonly string root = FindRoot();
    public MainWindow()
    {
        InitializeComponent(); UpdateMode();
        Loaded += (_, _) => RefreshSessions(this, new RoutedEventArgs());
    }
    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ArtificialLife.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("Live repository not found.");
    }
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMode();
    private void UpdateMode()
    {
        if (DescriptionText is null) return;
        ConfigBox.IsEnabled = ChooseConfigButton.IsEnabled = ModeBox.SelectedIndex is 1 or 3;
        SpeedBox.IsEnabled = ModeBox.SelectedIndex is 1 or 2 or 3;
        StepsBox.IsEnabled = ModeBox.SelectedIndex == 4;
        DescriptionText.Text = ModeBox.SelectedIndex switch
        {
            0 => "Viewer подключается к выбранной сессии. Выберите работающий мир из списка или запустите новый.",
            1 => "Мир без агента. Системы и модули работают в отдельном процессе.",
            2 => "Готовая политика действует в отдельном мире без обучения.",
            3 => "Непрерывное обучение в отдельном World Host. Новая жизнь начинается с новых весов и памяти.",
            _ => "Оценка фиксированной политики с заданным бюджетом. Внешнее вмешательство запрещено."
        };
        if (ModeBox.SelectedIndex == 1) ConfigBox.Text = "configs/world-only.json";
        if (ModeBox.SelectedIndex == 3 && ConfigBox.Text.EndsWith("world-only.json", StringComparison.OrdinalIgnoreCase)) ConfigBox.Text = "configs/runtime.json";
    }
    private void ChooseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) == true) ConfigBox.Text = dialog.FileName;
    }
    private void ChooseCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) CheckpointBox.Text = dialog.FolderName;
    }
    private Process Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Cannot start " + executable);
    }
    private string Cli => Path.Combine(root, "src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe");
    private void Viewer(string? session)
    {
        string godot = Directory.GetFiles(Path.Combine(root, ".tools", "godot"), "*console.exe", SearchOption.AllDirectories).Single();
        var args = new List<string> { "--audio-driver", "Dummy", "--path", Path.Combine(root, "src/ArtificialLife.Godot"), "--" };
        if (session is not null) args.AddRange(["--session", session]);
        using var process = Start(godot, args.ToArray());
    }
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ModeBox.SelectedIndex == 0) { Viewer((SessionsBox.SelectedItem as SessionDescriptor)?.SessionId); return; }
            string[] modes = ["", "world-only", "inference", "learning", "evaluation"];
            string directory = Path.Combine(root, "artifacts", "worlds", "launcher-" + Guid.NewGuid().ToString("N"));
            var args = new List<string> { "world", "run", "--mode", modes[ModeBox.SelectedIndex], "--directory", directory };
            if (ModeBox.SelectedIndex is 2 or 4) args.AddRange(["--policy", Path.GetFullPath(CheckpointBox.Text, root)]);
            else args.AddRange(["--config", Path.GetFullPath(ConfigBox.Text, root)]);
            args.AddRange(["--speed", SpeedBox.SelectedIndex switch { 0 => "1x", 1 => "10x", 2 => "100x", _ => "Max" }]);
            if (ModeBox.SelectedIndex == 4)
            {
                if (!long.TryParse(StepsBox.Text, out long steps) || steps < 1) throw new ArgumentException("Укажите положительный бюджет шагов.");
                args.AddRange(["--steps", steps.ToString()]);
            }
            using var process = Start(Cli, args.ToArray());
            DescriptionText.Text = "World Host: " + directory;
            if (OpenViewerBox.IsChecked == true)
            {
                for (int i = 0; i < 200; i++)
                {
                    string identity = Path.Combine(directory, "session-id");
                    if (File.Exists(identity)) { Viewer(File.ReadAllText(identity).Trim()); break; }
                    if (process.HasExited) throw new IOException("World Host завершился при запуске. Проверьте конфигурацию и checkpoint.");
                    await Task.Delay(100);
                }
            }
            RefreshSessions(this, new RoutedEventArgs());
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch failed"); }
    }
    private async void RefreshSessions(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new ObservationClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.Refresh(null, timeout.Token);
            SessionsBox.ItemsSource = client.Sessions;
            if (client.Sessions.Length > 0) SessionsBox.SelectedIndex = 0;
        }
        catch (Exception ex) { DescriptionText.Text = ex.Message; }
    }
    private void OpenSelectedViewer(object sender, RoutedEventArgs e)
    {
        try { Viewer((SessionsBox.SelectedItem as SessionDescriptor)?.SessionId); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void StopSelectedWorld(object sender, RoutedEventArgs e)
    {
        if (SessionsBox.SelectedItem is not SessionDescriptor session) return;
        using var process = Start(Cli, "world", "stop", "--session", session.SessionId);
    }
    private void SessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StopWorldButton is not null) StopWorldButton.IsEnabled = SessionsBox.SelectedItem is SessionDescriptor session && session.Mode is not ("Evaluation" or "Research");
    }
}
