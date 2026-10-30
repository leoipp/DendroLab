using System.Windows;
using System.Windows.Media;

namespace Dendro.Desktop;

public sealed class Flags : FrameworkElement
{
    public Flags()
    {
        Width = 65;
        Height = 18;
        Margin = new Thickness(10, 0, 10, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(0, 156, 59)),
            null,
            new Rect(0, 0, 27, 18)
        );
        var diamond = new StreamGeometry();
        using (var c = diamond.Open())
        {
            c.BeginFigure(new Point(13.5, 2), true, true);
            c.PolyLineTo(
                new[] { new Point(25, 9), new Point(13.5, 16), new Point(2, 9) },
                true,
                false
            );
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 223, 0)), null, diamond);
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(0, 39, 118)),
            null,
            new Point(13.5, 9),
            5,
            5
        );
        dc.DrawLine(new Pen(Brushes.White, 1), new Point(9, 7.5), new Point(18, 10));
        dc.DrawRectangle(
            Brushes.White,
            new Pen(Brushes.LightSlateGray, .5),
            new Rect(35, 0, 27, 18)
        );
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(188, 0, 45)),
            null,
            new Point(48.5, 9),
            5,
            5
        );
    }
}
