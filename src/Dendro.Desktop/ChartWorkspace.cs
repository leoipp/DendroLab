using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

/// <summary>Chart content whose auxiliary panes belong to the main docking manager.</summary>
internal sealed class ChartWorkspace : Grid, IDisposable
{
    private readonly MainWindow owner;
    internal Grid GraphBody { get; }
    internal FrameworkElement? ModelsContent { get; }
    internal FrameworkElement? DataContent { get; }

    internal ChartWorkspace(MainWindow owner, Grid graphBody, FrameworkElement? models, FrameworkElement? data)
    {
        this.owner = owner;
        GraphBody = graphBody; ModelsContent = models; DataContent = data;
        Children.Add(graphBody);
    }

    public void Dispose() => owner.DetachChartPanels(this);
}
