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
            button.Width = button.MinWidth = 28;
            button.Height = button.MinHeight = 25;
            button.Margin = new Thickness(0, 0, 2, 0);
            button.Padding = new Thickness(4);
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
        surface.Measure(new Size(68, 27));
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        const double rightInset = 26;
        double width = surface.Visibility == Visibility.Visible ? 68 : 0;
        surface.Arrange(new Rect(Math.Max(0, finalSize.Width - width - rightInset), 0, width, 27));
        return finalSize;
    }
}
