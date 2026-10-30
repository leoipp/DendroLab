using System.IO;
using System.Windows;

namespace Dendro.Desktop;

public partial class App : Application
{
    internal static bool IsSelfTest { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (IsSelfTest)
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"),
                    args.Exception.ToString()
                );
                Shutdown(1);
                return;
            }
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DENDRO"
                );
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, "errors.log"),
                    $"[{DateTimeOffset.Now:O}] {args.Exception}\n\n"
                );
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show(
                args.Exception.Message,
                "DendroLab",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        };
        if (e.Args.Length > 0 && e.Args[0] is "--self-test" or "--self-test-ui-results" or "--self-test-workflow")
        {
            IsSelfTest = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args[0] == "--self-test-workflow") await DesktopTests.RunProcessingWorkflow(e.Args[1]);
                else if (e.Args[0] == "--self-test-ui-results") await DesktopTests.RunResultPresentation(e.Args[1]);
                else await DesktopTests.Run(e.Args.Skip(1).ToArray());
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"),
                    ex.ToString()
                );
                Shutdown(1);
            }
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            await window.OpenProject(e.Args[0]);
    }
}
