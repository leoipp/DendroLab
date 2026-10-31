using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;

namespace Dendro.Desktop;

public partial class MainWindow
{
    internal LayoutAnchorable? RegressionModelsPane { get; private set; }
    internal LayoutAnchorable? RegressionDataPane { get; private set; }
    private ChartWorkspace? activeChartWorkspace;

    internal void AttachChartPanels(ChartWorkspace? workspace)
    {
        if (ReferenceEquals(activeChartWorkspace, workspace)) { UpdateRegressionNavigation(); return; }
        if (activeChartWorkspace != null) DetachChartPanels(activeChartWorkspace);
        if (workspace == null) { UpdateRegressionNavigation(); return; }
        activeChartWorkspace = workspace;
        // Clear panes restored from older layouts; regression content now belongs to its chart document.
        foreach (var pane in new[] { RegressionModelsPane, RegressionDataPane })
            if (pane != null) { SetPaneVisibility(pane, false); pane.Content = null; }
        UpdateRegressionNavigation();
    }

    private static void SetPaneVisibility(LayoutAnchorable pane, bool visible)
    {
        if (visible) { if (pane.IsHidden) pane.Show(); }
        else if (!pane.IsHidden) pane.Hide();
    }

    internal void DetachChartPanels(ChartWorkspace workspace)
    {
        if (!ReferenceEquals(activeChartWorkspace, workspace)) return;
        activeChartWorkspace = null;
        foreach (var pane in new[] { RegressionModelsPane, RegressionDataPane })
            if (pane != null) { SetPaneVisibility(pane, false); pane.Content = null; }
        UpdateRegressionNavigation();
    }

    private void RebindChartPanels(LayoutAnchorable[] panes)
    {
        activeChartWorkspace = null;
        RegressionModelsPane = panes.FirstOrDefault(p => p.ContentId == "regression-models");
        RegressionDataPane = panes.FirstOrDefault(p => p.ContentId == "regression-data");
        foreach (var pane in new[] { RegressionModelsPane, RegressionDataPane })
            if (pane != null) { SetPaneVisibility(pane, false); pane.Content = null; }
        UpdateRegressionNavigation();
    }

    internal void UpdateRegressionNavigation()
    {
        if (RegressionModelsButton == null || RegressionDataButton == null) return;
        RegressionModelsButton.IsEnabled = Session.IsOpen && activeChartWorkspace?.HasModels == true;
        RegressionDataButton.IsEnabled = Session.IsOpen && activeChartWorkspace?.HasData == true;
    }

    private void ShowRegressionPane(bool models)
    {
        if (activeChartWorkspace != null)
        {
            if (models) activeChartWorkspace.ShowModels(); else activeChartWorkspace.ShowData();
            return;
        }
        UpdateRegressionNavigation();
    }

    private void RegressionModelsClick(object sender, RoutedEventArgs e) => ShowRegressionPane(true);
    private void RegressionDataClick(object sender, RoutedEventArgs e) => ShowRegressionPane(false);
}
