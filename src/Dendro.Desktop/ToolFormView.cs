using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

// Forms scroll independently while the operation remains available at the bottom.
internal class ToolFormView : DockPanel
{
    private UIElement? form;
    private Button[] actions = [];

    protected void SetForm(UIElement value) { form = value; RefreshLayout(); }

    protected void SetPrimaryAction(Button value)
    {
        SetPrimaryActions(value);
    }

    protected void SetPrimaryActions(params Button[] values)
    {
        actions = values;
        foreach (var action in actions) Ui.StyleAction(action, Ui.ActionEmphasis.Primary);
        RefreshLayout();
    }

    private void RefreshLayout()
    {
        foreach (var scroll in Children.OfType<ScrollViewer>()) scroll.Content = null;
        foreach (var border in Children.OfType<Border>())
            if (border.Child is Panel row) row.Children.Clear();
        Children.Clear();
        if (actions.Length > 0)
        {
            var footer = Ui.ActionFooter(actions);
            SetDock(footer, Dock.Bottom);
            Children.Add(footer);
        }
        if (form != null) Children.Add(Ui.Scroll(form));
    }
}
