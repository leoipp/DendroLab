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
    internal enum MessageTone { Information, Warning, Error }

    private sealed class FieldGroup : StackPanel { }

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
        if (panel.Children[index] is FieldGroup)
        {
            var group = (FieldGroup)panel.Children[index];
            int labelIndex = -1;
            for (int i = 0; i < group.Children.Count; i++)
                if (group.Children[i] is TextBlock or HelpRow) { labelIndex = i; break; }
            if (labelIndex >= 0)
            {
                if (group.Children[labelIndex] is HelpRow labelExisting)
                {
                    labelExisting.Icon.Text += "\n\n" + icon.Text;
                    return;
                }
                var label = (TextBlock)group.Children[labelIndex];
                group.Children.RemoveAt(labelIndex);
                var labelRow = new HelpRow { Icon = icon, LastChildFill = true };
                DockPanel.SetDock(icon, Dock.Left);
                labelRow.Children.Add(icon);
                labelRow.Children.Add(label);
                group.Children.Insert(labelIndex, labelRow);
                return;
            }
        }
        else if (index > 0 && panel.Children[index] is TextBox or ComboBox or ListBox or DataGrid
            && panel.Children[index - 1] is TextBlock or HelpRow) index--;
        if (panel.Children[index] is HelpRow existing)
        {
            existing.Icon.Text += "\n\n" + icon.Text;
            return;
        }
        UIElement target = panel.Children[index];
        panel.Children.RemoveAt(index);
        var row = new HelpRow { Icon = icon, LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
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
        var b = CreateActionButton(label);
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
        var b = CreateActionButton(label);
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

    public static Button IconButton(string label, CommandIconKind icon, Action click)
    {
        var button = new Button
        {
            Content = new CommandIcon { Kind = icon, Width = 18, Height = 18 },
            Width = 32, Height = 30, MinWidth = 32, MinHeight = 30,
            Padding = new Thickness(5), Margin = new Thickness(2),
            ToolTip = label,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) =>
        {
            try { click(); }
            catch (Exception ex) { Error(ex); }
        };
        return button;
    }

    public static Button AsyncIconButton(string label, CommandIconKind icon, Func<Task> click)
    {
        var button = new Button
        {
            Content = new CommandIcon { Kind = icon, Width = 18, Height = 18 },
            Width = 32, Height = 30, MinWidth = 32, MinHeight = 30,
            Padding = new Thickness(5), Margin = new Thickness(2),
            ToolTip = label,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await click(); }
            catch (Exception ex) { Error(ex); }
            finally { button.IsEnabled = true; }
        };
        return button;
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

    public static StackPanel Form() => new() { Margin = new Thickness(16, 12, 16, 20) };

    public static FrameworkElement Field(Panel panel, string label, UIElement field)
    {
        var labelElement = Text(label, 11.5);
        labelElement.Foreground = new SolidColorBrush(Color.FromRgb(91, 113, 133));
        labelElement.FontWeight = FontWeights.SemiBold;
        labelElement.Margin = new Thickness(0, 2, 0, 3);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(field, labelElement);
        var group = new FieldGroup { Margin = new Thickness(0, 0, 0, 7) };
        group.Children.Add(labelElement);
        if (field is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            var margin = element.Margin;
            element.Margin = new Thickness(margin.Left, 2, margin.Right, 2);
        }
        group.Children.Add(field);
        panel.Children.Add(group);
        return group;
    }

    public static void Section(Panel panel, string title)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(48, 74, 96)),
            Margin = new Thickness(0, 12, 0, 4),
        };
        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 232, 239)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 3),
            Margin = new Thickness(0, 2, 0, 7),
            Child = heading,
        });
    }

    private static Button CreateActionButton(string label)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = label.Length == 0 ? 0 : 155,
            MinHeight = 36,
        };
        if (IsDangerAction(label))
        {
            button.Style = Application.Current.TryFindResource("DangerActionButton") as Style;
        }
        else if (IsPrimaryAction(label))
        {
            button.Style = Application.Current.TryFindResource("PrimaryActionButton") as Style;
            button.MinWidth = 195;
            System.Windows.Automation.AutomationProperties.SetName(button, label);
        }
        return button;
    }

    private static bool IsPrimaryAction(string label) =>
        label.StartsWith("Executar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Gerar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Comparar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Analisar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Calcular", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Auditar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Treinar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Aplicar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Iniciar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Criar nova versão", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Cadastrar modelo", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Salvar", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Aprovar versão", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Criar grupo", StringComparison.OrdinalIgnoreCase);

    private static bool IsDangerAction(string label) =>
        label.StartsWith("Remover revisão", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("Excluir revisão", StringComparison.OrdinalIgnoreCase);

    public static void Error(Exception ex)
    {
        if (App.IsSelfTest)
            throw new InvalidOperationException("Erro na interface durante o teste.", ex);
        Alert("DendroLab", ex.Message, MessageTone.Error);
    }

    public static void Alert(string title, string message, MessageTone tone = MessageTone.Information) =>
        ShowMessage(title, message, false, "Entendi", false, tone);

    public static bool Confirm(string title, string message, string confirmLabel = "Confirmar", bool destructive = false) =>
        App.IsSelfTest || ShowMessage(title, message, true, confirmLabel, destructive, destructive ? MessageTone.Warning : MessageTone.Information);

    private static bool ShowMessage(string title, string message, bool confirmation, string acceptLabel, bool destructive, MessageTone tone)
    {
        var (accent, tint, glyph) = tone switch
        {
            MessageTone.Error => (Color.FromRgb(178, 53, 65), Color.FromRgb(253, 238, 239), "×"),
            MessageTone.Warning => (Color.FromRgb(170, 101, 22), Color.FromRgb(252, 245, 232), "!"),
            _ => (Color.FromRgb(33, 102, 157), Color.FromRgb(235, 244, 251), "i"),
        };
        var icon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(tint),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = new SolidColorBrush(accent),
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 61, 82)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        System.Windows.Automation.AutomationProperties.SetName(heading, title);
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(new Border { Width = 12 });
        header.Children.Add(heading);

        var bodyText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(47, 69, 88)),
            LineHeight = 20,
        };
        var body = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 234, 242)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(14),
            Child = new ScrollViewer
            {
                Content = bodyText,
                MaxHeight = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };

        var accept = new Button
        {
            Content = acceptLabel,
            MinWidth = 96,
            IsDefault = true,
            Background = new SolidColorBrush(destructive ? Color.FromRgb(178, 53, 65) : Color.FromRgb(33, 102, 157)),
            BorderBrush = new SolidColorBrush(destructive ? Color.FromRgb(153, 42, 54) : Color.FromRgb(28, 88, 137)),
            Foreground = Brushes.White,
        };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button? cancel = null;
        if (confirmation)
        {
            cancel = new Button { Content = "Cancelar", MinWidth = 96, IsCancel = true };
            actions.Children.Add(cancel);
        }
        actions.Children.Add(accept);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(body);
        content.Children.Add(actions);
        var window = new Window
        {
            Title = title,
            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 233)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18),
                Child = content,
            },
            Width = 520,
            MinWidth = 440,
            MaxWidth = 620,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 500,
            Background = new SolidColorBrush(Color.FromRgb(244, 246, 249)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = ActiveWindow,
        };
        System.Windows.Automation.AutomationProperties.SetName(accept, acceptLabel);
        accept.Click += (_, _) => window.DialogResult = true;
        if (cancel != null) cancel.Click += (_, _) => window.DialogResult = false;
        window.Loaded += (_, _) => accept.Focus();
        bool? result = window.ShowDialog();
        return !confirmation || result == true;
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
