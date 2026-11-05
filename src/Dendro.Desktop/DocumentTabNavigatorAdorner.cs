using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dendro.Desktop;

internal sealed class DocumentTabNavigatorAdorner : Adorner
{
    private readonly VisualCollection visuals;
    private readonly Border surface;
    private readonly Button previous;
    private readonly Button next;

    internal DocumentTabNavigatorAdorner(UIElement adornedElement, Action goPrevious, Action goNext)
        : base(adornedElement)
    {
        visuals = new VisualCollection(this);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        previous = Ui.IconButton("Janela anterior", CommandIconKind.ArrowLeft, goPrevious);
        next = Ui.IconButton("Próxima janela", CommandIconKind.ArrowRight, goNext);
        foreach (var button in new[] { previous, next })
        {
            button.Width = button.MinWidth = 24;
            button.Height = button.MinHeight = 22;
            button.Margin = new Thickness(0, 0, 2, 0);
            button.Padding = new Thickness(3);
        }
        buttons.Children.Add(previous);
        buttons.Children.Add(next);
        surface = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(219, 227, 236)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(3, 0, 2, 0),
            Child = buttons,
        };
        visuals.Add(surface);
        UpdateCount(0);
    }

    internal void UpdateCount(int count)
    {
        bool enabled = count > 1;
        previous.IsEnabled = enabled;
        next.IsEnabled = enabled;
        surface.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        surface.Measure(new Size(60, 24));
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        const double rightInset = 26;
        var activeTab = FindActiveTab(AdornedElement);
        double top = 0;
        double height = 24;
        if (activeTab is { ActualHeight: > 0 })
        {
            var tabTop = activeTab.TransformToAncestor(AdornedElement).Transform(new Point(0, 0)).Y;
            top = tabTop;
            height = activeTab.ActualHeight;
        }
        double width = surface.Visibility == Visibility.Visible ? 60 : 0;
        surface.Arrange(new Rect(Math.Max(0, finalSize.Width - width - rightInset), top, width, height));
        return finalSize;
    }

    private static TabItem? FindActiveTab(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TabItem { IsSelected: true } tab)
                return tab;
            if (FindActiveTab(child) is { } nested)
                return nested;
        }
        return null;
    }
}
