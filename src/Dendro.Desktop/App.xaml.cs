using System.IO;
using System.Windows;

namespace Dendro.Desktop;

public partial class App : Application
{
    internal static bool IsSelfTest { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        Dendro.Core.Tabular.DlbEncryptionSession.Clear();
        base.OnExit(e);
    }

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
            Ui.Alert("DendroLab", args.Exception.Message, Ui.MessageTone.Error);
            Shutdown(1);
        };
        if (e.Args.Length > 0 && e.Args[0] is "--self-test" or "--self-test-ui-layout" or "--self-test-ui-results" or "--self-test-stacked-tabs" or "--self-test-chart-visuals" or "--self-test-workflow" or "--self-test-duplicates" or "--self-test-remove-discard" or "--self-test-context-menu-icons" or "--self-test-gis-contents")
        {
            IsSelfTest = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                string selfTestFailure = Path.Combine(AppContext.BaseDirectory, "self-test-error.txt");
                if (File.Exists(selfTestFailure)) File.Delete(selfTestFailure);
                if (e.Args[0] == "--self-test-ui-layout") await DesktopTests.RunUiLayout(e.Args[1]);
                else if (e.Args[0] == "--self-test-gis-contents") await DesktopTests.RunGisContents(e.Args[1]);
                else if (e.Args[0] == "--self-test-workflow") await DesktopTests.RunProcessingWorkflow(e.Args[1]);
                else if (e.Args[0] == "--self-test-duplicates") await DesktopTests.RunDuplicateSettings(e.Args[1]);
                else if (e.Args[0] == "--self-test-remove-discard") await DesktopTests.RunDeferredRemoval(e.Args[1]);
                else if (e.Args[0] == "--self-test-context-menu-icons") await DesktopTests.RunContextMenuIconAudit(e.Args[1]);
                else if (e.Args[0] == "--self-test-chart-visuals") await DesktopTests.RunChartVisuals(e.Args[1]);
                else if (e.Args[0] == "--self-test-stacked-tabs") await DesktopTests.RunStackedTabs(e.Args[1]);
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
