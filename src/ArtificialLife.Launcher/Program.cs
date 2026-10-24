namespace ArtificialLife.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherForm(FindRepositoryRoot()));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArtificialLife.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "tools", "run-godot.ps1")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Не удалось найти корень репозитория ArtificialLife.");
    }
}
