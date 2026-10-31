using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dendro.Desktop;

/// <summary>Chart, regression controls, and adjustment data in one document workspace.</summary>
internal sealed class ChartWorkspace : Grid, IDisposable
{
    private readonly MainWindow owner;
    private readonly TabControl auxiliary = new() { MinHeight = 160 };
    internal Grid GraphBody { get; }
    internal FrameworkElement? ModelsContent { get; }
    internal FrameworkElement? DataContent { get; }
    internal bool HasModels => FindTab(ModelsContent) != null;
    internal bool HasData => FindTab(DataContent) != null;

    internal ChartWorkspace(MainWindow owner, Grid graphBody, FrameworkElement? models, FrameworkElement? data)
    {
        this.owner = owner;
        GraphBody = graphBody; ModelsContent = models; DataContent = data;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 250 });
        Children.Add(graphBody);
        if (models != null || data != null)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star), MinHeight = 160 });
            var splitter = new GridSplitter
            {
                Height = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(220, 228, 236)),
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            };
            SetRow(splitter, 1); Children.Add(splitter);
            if (data != null) auxiliary.Items.Add(new TabItem { Header = "Dados do ajuste", Content = data });
            if (models != null) auxiliary.Items.Add(new TabItem { Header = "Modelos", Content = models });
            auxiliary.SelectedIndex = 0;
            SetRow(auxiliary, 2); Children.Add(auxiliary);
        }
    }

    private TabItem? FindTab(FrameworkElement? content) => content == null
        ? null
        : auxiliary.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Content, content));

    internal void ShowData() { var tab = FindTab(DataContent); if (tab != null) auxiliary.SelectedItem = tab; }
    internal void ShowModels() { var tab = FindTab(ModelsContent); if (tab != null) auxiliary.SelectedItem = tab; }
    internal void RemoveModels()
    {
        var tab = auxiliary.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Content, ModelsContent));
        if (tab != null)
        {
            auxiliary.Items.Remove(tab);
            owner.UpdateRegressionNavigation();
        }
    }

    public void Dispose() => owner.DetachChartPanels(this);
}
