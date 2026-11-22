using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dendro.Desktop;

internal sealed partial class ChartView
{
    private static Border BatchList(Panel items, double height) => new()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 233)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Background = Brushes.White,
        Padding = new Thickness(10),
        Margin = new Thickness(0, 4, 0, 8),
        Height = height,
        Child = Ui.Scroll(items),
    };

    private static WrapPanel BatchSelection(IEnumerable<CheckBox> checks)
    {
        var items = checks.ToArray();
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("Selecionar todos", () =>
        {
            foreach (var item in items.Where(c => c.IsEnabled)) item.IsChecked = true;
        }));
        buttons.Children.Add(Ui.Button("Limpar seleção", () =>
        {
            foreach (var item in items) item.IsChecked = false;
        }));
        return buttons;
    }

    private static DockPanel BatchDialogBody(UIElement form, TextBlock count, TextBlock message, params Button[] buttons)
    {
        var body = new DockPanel();
        var footer = new DockPanel
        {
            Margin = new Thickness(16, 8, 16, 12),
            LastChildFill = true,
        };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) actions.Children.Add(button);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        footer.Children.Add(count);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        message.TextWrapping = TextWrapping.Wrap;
        message.Foreground = Brushes.Firebrick;
        message.Margin = new Thickness(16, 0, 16, 4);
        DockPanel.SetDock(message, Dock.Bottom);
        body.Children.Add(message);
        body.Children.Add(Ui.Scroll(form));
        return body;
    }
}
