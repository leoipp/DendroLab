using System.IO;
using System.Windows;
using System.Windows.Threading;
using AvalonDock.Layout;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static async Task RunStackedTabs(string root)
    {
        Directory.CreateDirectory(root);
        var window = new MainWindow { WorkspacePath = Path.Combine(root, "layout.xml"), CloseConfirmation = () => Task.FromResult(true) };
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var pane = (LayoutAnchorablePane)window.ContentsPane.Parent;
        ((LayoutAnchorablePane)window.ToolsPane.Parent).Children.Remove(window.ToolsPane);
        pane.Children.Add(window.ToolsPane);
        window.ToolsPane.IsSelected = true;
        window.ToolsPane.IsActive = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Check(window.ToolsPane.IsSelected, "aba Ferramentas mantém a seleção do modelo");
        window.ToolsPane.ToggleAutoHide();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.ToolsPane.ToggleAutoHide();
        window.ToolsPane.IsActive = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Check(window.ToolsPane.Content is FrameworkElement content && content.IsVisible,
            "fixar novamente mantém o conteúdo visível");
        Capture(window, Path.Combine(root, "stacked.png"));
        window.Close();
    }
}
