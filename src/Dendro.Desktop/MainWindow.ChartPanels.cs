using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;

namespace Dendro.Desktop;

public partial class MainWindow
{
    internal LayoutAnchorable? RegressionModelsPane { get; private set; }
    internal LayoutAnchorable? RegressionDataPane { get; private set; }
    private ChartWorkspace? activeChartWorkspace;
    private bool modelsWereVisible = true, dataWereVisible = true;
    private bool modelsWereAutoHidden, dataWereAutoHidden;

    private static LayoutAnchorable ChartPane(string id, string title) => new()
    {
        ContentId = id, Title = title, CanClose = false, CanHide = true,
        CanAutoHide = true, CanFloat = true, CanDockAsTabbedDocument = false,
        AutoHideWidth = 440, AutoHideHeight = 300,
    };

    private void EnsureChartPanels()
    {
        ILayoutPanelElement center = Documents;
        while (center.Parent is not LayoutPanel && center.Parent is ILayoutPanelElement ancestor) center = ancestor;
        if (center.Parent is not LayoutPanel parent) return;
        if (RegressionDataPane == null)
        {
            RegressionDataPane = ChartPane("regression-data", "Dados do ajuste");
            var bottom = new LayoutAnchorablePane { DockHeight = new GridLength(260), DockMinHeight = 100 };
            bottom.Children.Add(RegressionDataPane);
            int index = parent.Children.IndexOf(center);
            parent.Children.Remove(center);
            var column = new LayoutPanel { Orientation = Orientation.Vertical };
            column.Children.Add(center); column.Children.Add(bottom);
            parent.Children.Insert(index, column);
            center = column;
        }
        if (RegressionModelsPane == null)
        {
            RegressionModelsPane = ChartPane("regression-models", "Modelos");
            var left = new LayoutAnchorablePane { DockWidth = new GridLength(360), DockMinWidth = 240 };
            left.Children.Add(RegressionModelsPane);
            int index = parent.Children.IndexOf(center);
            if (parent.Orientation == Orientation.Horizontal) parent.Children.Insert(index, left);
            else
            {
                parent.Children.Remove(center);
                var row = new LayoutPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(left); row.Children.Add(center); parent.Children.Insert(index, row);
            }
        }
    }

    internal void AttachChartPanels(ChartWorkspace? workspace)
    {
        if (ReferenceEquals(activeChartWorkspace, workspace)) return;
        if (activeChartWorkspace != null) DetachChartPanels(activeChartWorkspace);
        if (workspace == null) return;
        EnsureChartPanels(); activeChartWorkspace = workspace;
        RegressionModelsPane!.Content = workspace.ModelsContent;
        RegressionDataPane!.Content = workspace.DataContent;
        SetPaneVisibility(RegressionModelsPane, workspace.ModelsContent != null && modelsWereVisible);
        SetPaneVisibility(RegressionDataPane, workspace.DataContent != null && dataWereVisible);
        if (workspace.ModelsContent != null && modelsWereVisible && modelsWereAutoHidden && !RegressionModelsPane.IsAutoHidden) RegressionModelsPane.ToggleAutoHide();
        if (workspace.DataContent != null && dataWereVisible && dataWereAutoHidden && !RegressionDataPane.IsAutoHidden) RegressionDataPane.ToggleAutoHide();
    }

    private static void SetPaneVisibility(LayoutAnchorable pane, bool visible)
    {
        if (visible) { if (pane.IsHidden) pane.Show(); }
        else if (!pane.IsHidden) pane.Hide();
    }

    internal void DetachChartPanels(ChartWorkspace workspace)
    {
        if (!ReferenceEquals(activeChartWorkspace, workspace)) return;
        if (workspace.ModelsContent != null)
        { modelsWereVisible = RegressionModelsPane is { IsHidden: false }; modelsWereAutoHidden = RegressionModelsPane?.IsAutoHidden == true; }
        if (workspace.DataContent != null)
        { dataWereVisible = RegressionDataPane is { IsHidden: false }; dataWereAutoHidden = RegressionDataPane?.IsAutoHidden == true; }
        activeChartWorkspace = null;
        foreach (var pane in new[] { RegressionModelsPane, RegressionDataPane })
            if (pane != null) { SetPaneVisibility(pane, false); pane.Content = null; }
    }

    private void RebindChartPanels(LayoutAnchorable[] panes)
    {
        activeChartWorkspace = null;
        RegressionModelsPane = panes.FirstOrDefault(p => p.ContentId == "regression-models");
        RegressionDataPane = panes.FirstOrDefault(p => p.ContentId == "regression-data");
        foreach (var pane in new[] { RegressionModelsPane, RegressionDataPane })
            if (pane != null) { SetPaneVisibility(pane, false); pane.Content = null; }
    }

    private void ShowRegressionPane(bool models)
    {
        if (activeChartWorkspace == null) return;
        var pane = models ? RegressionModelsPane : RegressionDataPane;
        if (pane?.Content == null) return;
        if (models) modelsWereVisible = true; else dataWereVisible = true;
        pane.Show(); pane.IsActive = true;
    }

    private void RegressionModelsClick(object sender, RoutedEventArgs e) => ShowRegressionPane(true);
    private void RegressionDataClick(object sender, RoutedEventArgs e) => ShowRegressionPane(false);
}
