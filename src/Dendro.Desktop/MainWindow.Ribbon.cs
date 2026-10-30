using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dendro.Desktop;

public partial class MainWindow
{
    internal bool RibbonPinned { get; private set; } = true;
    internal bool RibbonExpanded => Ribbon.Height > 40;

    internal void SetRibbonPinned(bool pinned)
    {
        RibbonPinned = pinned;
        RibbonPinGlyph.LayoutTransform = new RotateTransform(pinned ? 0 : 90);
        System.Windows.Automation.AutomationProperties.SetName(RibbonPin, pinned ? "Desafixar menu" : "Fixar menu");
        RibbonPin.ToolTip = pinned
            ? "Desafixar o menu para liberar espaço (Ctrl+F1)."
            : "Fixar o menu aberto (Ctrl+F1). Clique nas abas para abrir temporariamente.";
        SetRibbonExpanded(pinned);
    }

    internal void SetRibbonExpanded(bool expanded) => Ribbon.Height = RibbonPinned || expanded ? 152 : 40;

    private void RibbonPinClick(object sender, RoutedEventArgs e) => SetRibbonPinned(!RibbonPinned);
    private void RibbonMouseLeave(object sender, MouseEventArgs e)
    {
        if (!RibbonPinned) SetRibbonExpanded(false);
    }
    private void RibbonTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Ribbon) && !RibbonPinned) SetRibbonExpanded(true);
    }
    private void RibbonTabClick(object sender, MouseButtonEventArgs e)
    {
        if (RibbonPinned) return;
        // A selected tab must also reopen when clicked again after auto-hiding.
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node is not TabItem && !ReferenceEquals(node, Ribbon))
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is TabItem) SetRibbonExpanded(true);
    }
}
