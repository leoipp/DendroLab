using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

// Forms scroll independently while the operation remains available at the bottom.
internal class ToolFormView : DockPanel
{
    private UIElement? form;
    private Button? action;

    protected void SetForm(UIElement value) { form = value; RefreshLayout(); }

    protected void SetPrimaryAction(Button value)
    {
        action = value;
        Ui.StyleAction(action, Ui.ActionEmphasis.Primary);
        RefreshLayout();
    }

    private void RefreshLayout()
    {
        foreach (var scroll in Children.OfType<ScrollViewer>()) scroll.Content = null;
        foreach (var border in Children.OfType<Border>())
            if (border.Child is Panel row) row.Children.Clear();
        Children.Clear();
        if (action != null)
        {
            var footer = Ui.ActionFooter(action);
            SetDock(footer, Dock.Bottom);
            Children.Add(footer);
        }
        if (form != null) Children.Add(Ui.Scroll(form));
    }
}
