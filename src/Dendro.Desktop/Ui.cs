using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dendro.Desktop;

internal static class Ui
{
    public static HelpIcon Help(string text) => new(text);

    private sealed class HelpRow : DockPanel
    {
        public required HelpIcon Icon { get; init; }
    }

    public static void AddHelp(Panel panel, string text) => AddHelp(panel, Help(text));

    public static void AddHelp(Panel panel, HelpIcon icon)
    {
        if (panel.Children.Count == 0) { panel.Children.Add(icon); return; }
        int index = panel.Children.Count - 1;
        // Put help beside the label rather than between a field and its label.
        if (index > 0 && panel.Children[index] is TextBox or ComboBox or ListBox or DataGrid
            && panel.Children[index - 1] is TextBlock or HelpRow) index--;
        if (panel.Children[index] is HelpRow existing)
        {
            existing.Icon.Text += "\n\n" + icon.Text;
            return;
        }
        UIElement target = panel.Children[index];
        panel.Children.RemoveAt(index);
        var row = new HelpRow { Icon = icon, LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(target);
        panel.Children.Insert(index, row);
    }

    public static TextBlock Text(string text, double size = 13) =>
        new()
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 6),
        };

    public static TextBox Input(string text = "") => new() { Text = text };

    public static Button Button(string label, Action click)
    {
        var b = new Button { Content = label };
        b.Click += (_, _) =>
        {
            try
            {
                click();
            }
            catch (Exception ex)
            {
                Error(ex);
            }
        };
        return b;
    }

    public static Button AsyncButton(string label, Func<Task> click)
    {
        var b = new Button { Content = label };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try
            {
                await click();
            }
            catch (Exception ex)
            {
                Error(ex);
            }
            finally
            {
                b.IsEnabled = true;
            }
        };
        return b;
    }

    public static ComboBox Combo(IEnumerable<string> values, string? selected = null)
    {
        var c = new ComboBox { ItemsSource = values.ToArray() };
        if (selected != null)
            c.SelectedItem = selected;
        if (c.SelectedIndex < 0 && c.Items.Count > 0)
            c.SelectedIndex = 0;
        return c;
    }

    public static ScrollViewer Scroll(UIElement content) =>
        new()
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

    public static StackPanel Form() => new() { Margin = new Thickness(16) };

    public static void Field(Panel panel, string label, UIElement field)
    {
        panel.Children.Add(Text(label));
        panel.Children.Add(field);
    }

    public static void Error(Exception ex)
    {
        if (App.IsSelfTest)
            throw new InvalidOperationException("Erro na interface durante o teste.", ex);
        MessageBox.Show(ex.Message, "DendroLab", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static double Number(string text)
    {
        if (
            double.TryParse(
                text.Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var n
            ) && double.IsFinite(n)
        )
            return n;
        throw new InvalidOperationException("Informe um número válido.");
    }

    public static double? Factor(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var n = Number(text);
        if (n < 0 || n > 1)
            throw new InvalidOperationException("O fator deve estar entre 0 e 1.");
        return n;
    }

    public static DataTable Table(JsonNode data)
    {
        var t = new DataTable();
        var columns = data["columns"]!.AsArray();
        for (int i = 0; i < columns.Count; i++)
            t.Columns.Add($"c{i}", typeof(object));
        foreach (var n in data["rows"]!.AsArray())
        {
            var values = n!
                .AsArray()
                .Select(v =>
                    v is JsonValue j && j.TryGetValue<double>(out var number)
                        ? (object)number
                        : v.Display()
                )
                .ToArray();
            t.Rows.Add(values);
        }
        return t;
    }

    public static DataGrid Grid(JsonNode data)
    {
        var grid = new DataGrid { ItemsSource = Table(data).DefaultView };
        grid.AutoGeneratingColumn += (_, e) =>
        {
            if (
                e.PropertyName.StartsWith('c')
                && int.TryParse(e.PropertyName[1..], out int i)
                && i < data["columns"]!.AsArray().Count
            )
            {
                e.Column.Header = data["columns"]![i].Display();
                if (e.Column is DataGridTextColumn numeric)
                {
                    numeric.Binding = new System.Windows.Data.Binding(e.PropertyName)
                    {
                        StringFormat = "{0:G6}",
                        Converter = new TableCellFormatting(TableCellFormatting.Guess(e.Column.Header?.ToString() ?? "")),
                    };
                }
            }
        };
        return grid;
    }

    public static string? PickFile(
        string filter = "Planilhas Excel|*.xlsx;*.xls|Todos os arquivos|*.*"
    )
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        return d.ShowDialog(ActiveWindow) == true ? d.FileName : null;
    }

    public static string? PickFolder()
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        return d.ShowDialog(ActiveWindow) == true ? d.FolderName : null;
    }

    public static string? SaveFile(string filter, string name)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = name };
        return d.ShowDialog(ActiveWindow) == true ? d.FileName : null;
    }

    private static Window ActiveWindow =>
        Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current.MainWindow;

    public static Window Dialog(
        string title,
        UIElement content,
        double width = 780,
        double height = 650
    ) =>
        new()
        {
            Title = title,
            Content = content,
            Width = width,
            Height = height,
            MinWidth = 480,
            MinHeight = 320,
            Owner = ActiveWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

    public static DockPanel WithToolbar(UIElement body, params Button[] buttons)
    {
        var p = new DockPanel { Background = Brushes.White };
        var bar = new WrapPanel { Margin = new Thickness(10, 6, 10, 6) };
        foreach (var b in buttons)
            bar.Children.Add(b);
        DockPanel.SetDock(bar, Dock.Top);
        p.Children.Add(bar);
        p.Children.Add(body);
        return p;
    }
}
