using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Dendro.Desktop;

internal sealed class VectorSymbologyEditor : Window
{
    private readonly ProjectSession session;
    private readonly string path;
    private readonly VectorSymbology original;
    private readonly ComboBox renderer = Ui.Combo(["Símbolo único", "Valores únicos"]);
    private readonly ComboBox field = Ui.Combo([]);
    private readonly ComboBox palette = Ui.Combo(["Categórica", "Verdes", "Azuis"]);
    private readonly ComboBox pattern, dash;
    private readonly ColorSwatch fill, line;
    private readonly CheckBox fillEnabled, lineEnabled, rounds, others;
    private readonly TextBox width, point, spacing;
    private readonly StackPanel categoryRows = new();
    private readonly TextBlock status = Ui.Text("");
    private readonly TextBlock error = Ui.Text("");
    private readonly Canvas preview = new() { Height = 84, Background = ColorSwatch.Checker(), Margin = new Thickness(16, 0, 16, 8) };
    private readonly List<(VectorCategory Category, ColorSwatch Color, CheckBox Visible)> rows = new();
    private string classifiedField;
    private bool classifying;
    private bool batchUpdate;

    internal VectorSymbologyEditor(Window owner, ProjectSession session, string path)
    {
        this.session = session; this.path = path; original = VectorSymbology.Load(session, path);
        Title = "Simbologia"; Owner = owner; Width = 650; Height = 800; MinWidth = 480; MinHeight = 480;
        MaxHeight = SystemParameters.WorkArea.Height * .92;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Foreground = (Brush)Application.Current.FindResource("Ui.Text");
        fill = new ColorSwatch(WithOpacity(original.FillColor, original.FillOpacity));
        line = new ColorSwatch(WithOpacity(original.LineColor, original.LineOpacity));
        fillEnabled = new CheckBox { Content = "Habilitar preenchimento", IsChecked = original.FillEnabled && original.Pattern != "Sem preenchimento" };
        lineEnabled = new CheckBox { Content = "Habilitar contorno", IsChecked = original.LineEnabled };
        rounds = new CheckBox { Content = "Preservar cores das rodadas de amostragem no símbolo único", IsChecked = original.SamplingColors };
        others = new CheckBox { Content = "Exibir outros valores com o símbolo padrão", IsChecked = original.ShowOtherValues };
        width = Ui.Input(original.LineWidth.ToString(System.Globalization.CultureInfo.CurrentCulture));
        point = Ui.Input(original.PointSize.ToString(System.Globalization.CultureInfo.CurrentCulture));
        spacing = Ui.Input(original.HatchSpacing.ToString(System.Globalization.CultureInfo.CurrentCulture));
        pattern = Ui.Combo(["Sólido", "Diagonal", "Cruzada", "Horizontal", "Vertical"], original.Pattern == "Sem preenchimento" ? "Sólido" : original.Pattern);
        dash = Ui.Combo(["Contínuo", "Tracejado", "Pontilhado"], original.LineStyle);
        renderer.SelectedIndex = original.CategoryField.Length == 0 ? 0 : 1;
        System.Windows.Automation.AutomationProperties.SetAutomationId(renderer, "Symbology.Renderer");
        System.Windows.Automation.AutomationProperties.SetAutomationId(field, "Symbology.Field");
        System.Windows.Automation.AutomationProperties.SetAutomationId(fill, "Symbology.Fill");
        System.Windows.Automation.AutomationProperties.SetAutomationId(line, "Symbology.Line");
        classifiedField = original.CategoryField;

        var body = new DockPanel { Background = Brushes.White };
        var header = Ui.Form(); header.Margin = new Thickness(16, 12, 16, 8);
        header.Children.Add(Ui.Text(session.ContentName(path), 18));
        Ui.Field(header, "Representação", renderer);
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        DockPanel.SetDock(preview, Dock.Top); body.Children.Add(preview);

        var symbol = Ui.Form();
        symbol.Children.Add(fillEnabled);
        Ui.Field(symbol, "Cor e transparência do preenchimento / símbolo padrão", fill);
        Ui.Field(symbol, "Estilo de preenchimento", pattern);
        Ui.Field(symbol, "Espaçamento da hachura", spacing);
        symbol.Children.Add(lineEnabled);
        Ui.Field(symbol, "Cor e transparência do contorno", line);
        Ui.Field(symbol, "Espessura do contorno", width);
        Ui.Field(symbol, "Estilo do contorno", dash);
        Ui.Field(symbol, "Diâmetro dos pontos", point);
        symbol.Children.Add(rounds);
        symbol.Children.Add(Ui.Text("As medidas de espessura, hachura e pontos usam unidades de tela. Desligar um componente preserva suas configurações.", 12));

        var categories = Ui.Form();
        categories.Children.Add(Ui.Text("Uma cor para cada valor distinto do atributo salvo no DBF. Valores vazios formam uma categoria própria. Edite a cor para ajustar também sua transparência.", 12));
        Ui.Field(categories, "Campo do DBF", field);
        Ui.Field(categories, "Paleta para novas categorias", palette);
        var classify = Ui.AsyncButton("Classificar valores", Classify, Ui.ActionEmphasis.Secondary);
        var categoryActions = new WrapPanel();
        categoryActions.Children.Add(classify);
        categoryActions.Children.Add(Ui.Button("Recolorir categorias", () =>
        {
            batchUpdate = true;
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var color = PaletteColor(i, rows.Count); color.A = rows[i].Color.SelectedColor.A;
                    rows[i].Color.SetColor(color);
                }
            }
            finally { batchUpdate = false; }
            UpdatePreview();
        }, Ui.ActionEmphasis.Secondary));
        categories.Children.Add(categoryActions);
        categories.Children.Add(status); categories.Children.Add(others);
        categories.Children.Add(categoryRows);
        var categoryTab = new TabItem { Header = "Categorias", Content = Ui.Scroll(categories), IsEnabled = renderer.SelectedIndex == 1 };
        var tabs = new TabControl { Background = Brushes.White };
        tabs.Items.Add(new TabItem { Header = "Símbolo", Content = Ui.Scroll(symbol) }); tabs.Items.Add(categoryTab);
        if (renderer.SelectedIndex == 1) tabs.SelectedItem = categoryTab;
        var cancel = Ui.Button("Cancelar", () => DialogResult = false, Ui.ActionEmphasis.Secondary); cancel.IsCancel = true;
        var apply = Ui.Button("Aplicar", () =>
        {
            try
            {
                if (classifying) throw new InvalidOperationException("Aguarde a classificação terminar.");
                var style = Read(); style.Validate();
                if (renderer.SelectedIndex == 1 && (field.Text.Length == 0 || classifiedField != field.Text || rows.Count == 0))
                    throw new InvalidOperationException("Selecione um campo e clique em Classificar valores antes de aplicar.");
                session.Meta.Obj("gis_symbology")[path] = JsonSerializer.SerializeToNode(style);
                session.Touch(); DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { error.Text = ex.Message; }
        }, Ui.ActionEmphasis.Primary);
        var footer = Ui.ActionFooter(cancel, apply); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        error.Foreground = Brushes.Firebrick; error.Margin = new Thickness(16, 2, 16, 4);
        DockPanel.SetDock(error, Dock.Bottom); body.Children.Add(error); body.Children.Add(tabs); Content = body;
        foreach (var input in new[] { width, point, spacing }) input.TextChanged += (_, _) => UpdatePreview();
        foreach (var color in new[] { fill, line }) color.ColorChanged += UpdatePreview;
        foreach (var check in new[] { fillEnabled, lineEnabled, rounds, others }) check.Click += (_, _) => UpdatePreview();
        foreach (var combo in new[] { pattern, dash }) combo.SelectionChanged += (_, _) => UpdatePreview();
        renderer.SelectionChanged += (_, _) =>
        {
            categoryTab.IsEnabled = renderer.SelectedIndex == 1;
            if (categoryTab.IsEnabled) tabs.SelectedItem = categoryTab; else tabs.SelectedIndex = 0;
            UpdatePreview();
        };
        field.SelectionChanged += (_, _) => { status.Text = field.Text == classifiedField ? $"{rows.Count} categorias" : "Clique em Classificar valores para atualizar as categorias."; };
        Loaded += async (_, _) =>
        {
            try
            {
                var names = await Task.Run(() => DbfTable.Read(System.IO.Path.ChangeExtension(session.Resolve(path), ".dbf")).Fields.Select(f => f.Name).ToArray());
                field.ItemsSource = names;
                field.SelectedItem = names.FirstOrDefault(n => n.Equals(original.CategoryField, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
                ShowCategories(original.Categories ?? []);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { status.Text = "Não foi possível ler o DBF: " + ex.Message; }
            UpdatePreview();
        };
        UpdatePreview();
    }

    private static Color WithOpacity(string color, double opacity)
    { var result = (Color)ColorConverter.ConvertFromString(color); result.A = (byte)Math.Round(result.A * opacity); return result; }
    private static string Rgb(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private VectorSymbology Read() => new(Rgb(fill.SelectedColor), Rgb(line.SelectedColor), fill.SelectedColor.A / 255d,
        Ui.Number(width.Text), pattern.Text, dash.Text, Ui.Number(point.Text), Ui.Number(spacing.Text), rounds.IsChecked == true,
        fillEnabled.IsChecked == true, lineEnabled.IsChecked == true, line.SelectedColor.A / 255d,
        renderer.SelectedIndex == 1 ? classifiedField : "", rows.Select(r => new VectorCategory(r.Category.Value, r.Color.SelectedColor.ToString(), r.Visible.IsChecked == true)).ToList(), others.IsChecked == true);

    private async Task Classify()
    {
        if (classifying) return;
        classifying = true; field.IsEnabled = false;
        try
        {
            string name = field.Text;
            if (name.Length == 0) throw new InvalidOperationException("Selecione um campo do DBF.");
            status.Text = "Lendo valores do atributo…";
            var values = await Task.Run(() => VectorSymbology.ReadCategoryKeys(session.Resolve(path), name)
                .Where(value => value != null).Cast<string>().Distinct(StringComparer.Ordinal).Take(501).Order(StringComparer.Ordinal).ToArray());
            if (values.Length > 500) throw new InvalidOperationException("Este campo tem mais de 500 valores únicos. Escolha um atributo com menos categorias.");
            var previous = name == classifiedField ? rows.ToDictionary(r => r.Category.Value, r => new VectorCategory(r.Category.Value, r.Color.SelectedColor.ToString(), r.Visible.IsChecked == true), StringComparer.Ordinal) : [];
            classifiedField = name;
            ShowCategories(values.Select((value, i) => previous.GetValueOrDefault(value) ?? new VectorCategory(value, PaletteColor(i, values.Length).ToString())).ToList());
            error.Text = ""; UpdatePreview();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { status.Text = ex.Message; }
        finally { classifying = false; field.IsEnabled = true; }
    }

    private Color PaletteColor(int index, int count)
    {
        var color = palette.SelectedIndex switch
        {
            1 => HsvWheel.FromHsv(140, .3 + .65 * index / Math.Max(1d, count - 1), .85),
            2 => HsvWheel.FromHsv(210, .3 + .65 * index / Math.Max(1d, count - 1), .85),
            _ => HsvWheel.FromHsv(index * 137.508 % 360, .65, .82),
        };
        color.A = fill.SelectedColor.A;
        return color;
    }

    private void ShowCategories(List<VectorCategory> categories)
    {
        categoryRows.Children.Clear(); rows.Clear();
        foreach (var category in categories)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var visible = new CheckBox { IsChecked = category.Visible, ToolTip = "Exibir categoria", VerticalAlignment = VerticalAlignment.Center };
            var color = new ColorSwatch((Color)ColorConverter.ConvertFromString(category.Color), compact: true) { Padding = new Thickness(4), MinHeight = 30 };
            DockPanel.SetDock(visible, Dock.Left); DockPanel.SetDock(color, Dock.Left);
            row.Children.Add(visible); row.Children.Add(color);
            row.Children.Add(new TextBlock { Text = category.Value.Length == 0 ? "(Vazio / nulo)" : category.Value,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            rows.Add((category, color, visible)); categoryRows.Children.Add(row);
            color.ColorChanged += UpdatePreview; visible.Click += (_, _) => UpdatePreview();
        }
        status.Text = $"{rows.Count} categorias · campos e dados originais são preservados";
    }

    private void UpdatePreview()
    {
        if (batchUpdate) return;
        try
        {
            var style = Read(); style.Validate();
            fill.IsEnabled = pattern.IsEnabled = spacing.IsEnabled = style.FillEnabled;
            line.IsEnabled = width.IsEnabled = dash.IsEnabled = style.LineEnabled;
            preview.Children.Clear();
            var examples = renderer.SelectedIndex == 1 && rows.Count > 0
                ? rows.Where(r => r.Visible.IsChecked == true).Take(4).Select(r => style.ResolveCategory(r.Category.Value)!).ToArray() : [style];
            for (int i = 0; i < examples.Length; i++)
            {
                var item = examples[i]; var pen = item.Pen(1);
                var rectangle = new Rectangle { Width = 88, Height = 52, Fill = item.Fill(1), Stroke = pen?.Brush,
                    StrokeThickness = item.LineWidth, StrokeDashArray = pen?.DashStyle.Dashes };
                Canvas.SetLeft(rectangle, 12 + i * 100); Canvas.SetTop(rectangle, 16); preview.Children.Add(rectangle);
            }
            error.Text = "";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { error.Text = ex.Message; }
    }
}
