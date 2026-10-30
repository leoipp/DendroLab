using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dendro.Desktop;

/// <summary>Clips hosted content to the same radius as its border, including while docked.</summary>
public sealed class RoundedPanel : Border
{
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var result = base.ArrangeOverride(arrangeSize);
        if (Child == null)
            return result;
        // Clip only the content, inside the stroke. Clipping the Border itself cuts
        // its antialiased outline and lets rectangular child backgrounds cover it.
        var clip = new RectangleGeometry(
            new Rect(Child.RenderSize),
            Math.Max(0, CornerRadius.TopLeft - BorderThickness.Left),
            Math.Max(0, CornerRadius.TopLeft - BorderThickness.Top)
        );
        clip.Freeze();
        Child.Clip = clip;
        return result;
    }
}
