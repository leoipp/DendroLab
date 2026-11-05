using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class ModelValidationView : ScrollViewer
{
    private sealed record SourceChoice(string Path, string Name, TableSource Table);
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source = Ui.Combo([]);
    private readonly ComboBox observed = Ui.Combo([]);
    private readonly StackPanel estimateChoices = new();
    private readonly TextBlock status = Ui.Text("");
    private SourceChoice[] sources = [];
    private bool loading;

    internal ModelValidationView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var form = Ui.Form();
        Content = form;
        form.Children.Add(owner.ToolHeader("Validação de modelos"));
        Ui.AddHelp(form, "Compare uma coluna observada com uma ou várias colunas estimadas na mesma tabela. Cada modelo é avaliado nas linhas em que Y observado e sua estimativa são numéricos.");
        Ui.Section(form, "Dados observados e estimados");
        Ui.Field(form, "Tabela de origem", source);
        Ui.Field(form, "Valores observados (Y)", observed);
        var choiceScroll = new ScrollViewer
        {
            Content = estimateChoices,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = new SolidColorBrush(Color.FromRgb(205, 216, 228)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
        };
        Ui.Field(form, "Colunas estimadas (selecione um ou mais modelos)", choiceScroll);
        Ui.AddHelp(form, "Erro = estimado − observado. R² = 1 − SQ(resíduos)/SQ(total), pode ser negativo. Erro médio (%) mantém o sinal; MAE e MAPE usam valores absolutos. Bias é o erro médio nas unidades de Y.");
        form.Children.Add(Ui.AsyncButton("Comparar modelos", () => owner.Execute(Compare)));
        form.Children.Add(status);
        source.SelectionChanged += async (_, _) => { if (!loading) await owner.Execute(LoadColumns); };
        observed.SelectionChanged += (_, _) => UpdateEstimateChoices();
    }

    internal async Task Initialize(string? initialPath = null)
    {
        loading = true;
        try
        {
            sources = await Task.Run(BuildSources);
            source.ItemsSource = sources.Select(item => item.Name).ToArray();
            int index = initialPath == null ? -1 : Array.FindIndex(sources, item =>
                string.Equals(item.Path, initialPath, StringComparison.OrdinalIgnoreCase));
            source.SelectedIndex = index >= 0 ? index : sources.Length > 0 ? 0 : -1;
        }
        finally { loading = false; }
        if (source.SelectedIndex >= 0) await LoadColumns();
        else status.Text = "Adicione uma tabela ao projeto para começar.";
    }

    private SourceChoice[] BuildSources()
    {
        var result = new List<SourceChoice>();
        foreach (string path in session.Files.Where(session.IsTabular))
        {
            string resolved = session.Resolve(path);
            string[] tables;
            try { tables = TableStore.Tables(resolved); }
            catch (IOException) { continue; }
            catch (InvalidDataException) { continue; }
            foreach (string table in tables)
            {
                var options = session.ExcelOptions(path);
                result.Add(new(path, session.ContentName(path) + (tables.Length > 1 ? " · " + table : ""),
                    new TableSource(resolved, table, session.IsSpreadsheet(path) ? (int)options.N("header_row", 1) : 1)));
            }
        }
        return result.ToArray();
    }

    private async Task LoadColumns()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length) return;
        var selected = sources[source.SelectedIndex];
        var columns = await Task.Run(() =>
        {
            using var cursor = new TableCursor(selected.Table);
            var sample = cursor.Rows().Take(100).ToArray();
            return (cursor.Columns, Numeric: Enumerable.Range(0, cursor.Columns.Length)
                .Where(column => sample.Any(row => TryNumber(row.ElementAtOrDefault(column), out _)))
                .Select(column => cursor.Columns[column]).ToArray());
        });
        loading = true;
        try
        {
            observed.ItemsSource = columns.Numeric;
            observed.SelectedIndex = columns.Numeric.Length > 0 ? 0 : -1;
            if (columns.Numeric.Contains("Y", StringComparer.OrdinalIgnoreCase))
                observed.SelectedIndex = Array.FindIndex(columns.Numeric, name => name.Equals("Y", StringComparison.OrdinalIgnoreCase));
            estimateChoices.Children.Clear();
            foreach (string name in columns.Numeric)
                estimateChoices.Children.Add(new CheckBox { Content = name, Tag = name, IsChecked = false,
                    Margin = new Thickness(4, 2, 4, 2), ToolTip = "Usar esta coluna como estimativa de um modelo." });
            UpdateEstimateChoices();
        }
        finally { loading = false; }
        status.Text = columns.Numeric.Length < 2 ? "A tabela precisa ter pelo menos duas colunas numéricas." : "Escolha Y observado e marque uma ou mais colunas estimadas.";
    }

    private void UpdateEstimateChoices()
    {
        string y = observed.SelectedItem?.ToString() ?? "";
        foreach (var box in estimateChoices.Children.OfType<CheckBox>())
        {
            bool isObserved = string.Equals(box.Tag?.ToString(), y, StringComparison.Ordinal);
            if (isObserved) box.IsChecked = false;
            box.IsEnabled = !isObserved;
        }
    }

    private async Task Compare()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length)
            throw new InvalidOperationException("Selecione uma tabela de origem.");
        string y = observed.SelectedItem?.ToString() ?? "";
        string[] estimates = estimateChoices.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true).Select(box => box.Tag?.ToString() ?? "").ToArray();
        if (string.IsNullOrWhiteSpace(y)) throw new InvalidOperationException("Selecione a coluna de Y observado.");
        if (estimates.Length == 0) throw new InvalidOperationException("Marque pelo menos uma coluna estimada.");
        var selected = sources[source.SelectedIndex];
        var cancellation = owner.OperationCancellation;
        var progress = owner.StartTableOperation("Comparando valores observados e estimados…");
        var report = await Task.Run(() => ModelValidation.Run(selected.Table, y, estimates, cancellation), cancellation);
        var result = BuildResult(report, selected.Name, selected.Table.Table);
        owner.Document("model-validation:" + selected.Path + ":" + selected.Table.Table,
            "Validação · " + selected.Name, Ui.WithToolbar(result,
                Ui.Button("Exportar resultados XLSX…", () => Export(report, selected.Name))));
        status.Text = $"Comparação concluída · {report.Series.Length} modelo(s).";
        owner.Log(status.Text);
    }

    private static UIElement BuildResult(ModelValidationReport report, string sourceName, string tableName)
    {
        var body = new DockPanel { LastChildFill = true };
        var header = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        header.Children.Add(Ui.Text($"Validação de modelos · {sourceName} · {tableName}", 17));
        Ui.AddHelp(header, "Cada linha de métricas usa somente os pares válidos daquele modelo. O gráfico de dispersão compara observado no eixo X e estimado no eixo Y; a diagonal representa estimativa perfeita. O histograma considera erros relativos de −1 a 1, normalizados pela soma das frequências.");
        var seriesChoice = Ui.Combo(report.Series.Select(series => series.Metrics.EstimateColumn));
        seriesChoice.SelectedIndex = 0;
        Ui.Field(header, "Modelo / coluna estimada", seriesChoice);
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        var tabs = new TabControl();
        var metrics = MetricsTable(report);
        tabs.Items.Add(new TabItem { Header = "Métricas", Content = Ui.Grid(metrics.ToJson()) });
        var scatter = new ModelValidationChart { Height = 480, Margin = new Thickness(8), Mode = ModelValidationChart.ChartMode.Scatter };
        var histogram = new ModelValidationChart
        {
            Height = 480,
            Margin = new Thickness(8),
            Mode = ModelValidationChart.ChartMode.Histogram,
            ToolTip = "Erro relativo = (estimado − observado) / observado. O histograma usa classes de −1 a 1; a frequência relativa de cada classe é frequência da classe / soma das frequências incluídas nesse intervalo.",
        };
        void UpdateCharts()
        {
            int index = Math.Clamp(seriesChoice.SelectedIndex, 0, report.Series.Length - 1);
            scatter.Series = histogram.Series = report.Series[index];
        }
        seriesChoice.SelectionChanged += (_, _) => UpdateCharts();
        UpdateCharts();
        tabs.Items.Add(new TabItem { Header = "Dispersão observado × estimado", Content = scatter });
        tabs.Items.Add(new TabItem { Header = "Histograma do erro relativo", Content = histogram });
        body.Children.Add(tabs);
        return body;
    }

    private static DataTable MetricsTable(ModelValidationReport report)
    {
        var table = new DataTable(["Modelo / estimativa", "N", "Pares omitidos", "R²", "Erro médio (%)", "Erro absoluto médio (MAE)", "Bias", "RMSE", "Erro percentual absoluto médio (MAPE, %)", "N para erro percentual"]);
        foreach (var series in report.Series)
        {
            var m = series.Metrics;
            table.Add(m.EstimateColumn, (double)m.N, (double)m.Omitted, m.R2, m.MeanErrorPercent,
                m.MeanAbsoluteError, m.Bias, m.Rmse, m.MeanAbsolutePercentError, (double)m.RelativeErrorN);
        }
        return table;
    }

    private static void Export(ModelValidationReport report, string sourceName)
    {
        string? path = Ui.SaveFile("Pasta de trabalho Excel|*.xlsx", "Validacao_modelos.xlsx");
        if (path == null) return;
        var metrics = MetricsTable(report);
        var histogram = new DataTable(["Modelo", "Centro da classe (erro relativo)", "Limite inferior", "Limite superior", "Frequência", "Frequência relativa"]);
        foreach (var series in report.Series)
            foreach (var bin in series.Histogram)
                histogram.Add(series.Metrics.EstimateColumn, bin.Center, bin.Lower, bin.Upper, (double)bin.Count, bin.RelativeFrequency);
        ProjectStore.AtomicWrite(path, temporary => WorkbookWriter.Write(temporary,
            [new("Métricas", metrics), new("Histograma", histogram)]), true);
    }

    private static bool TryNumber(object? value, out double number)
    {
        string text = TableCursor.Text(value).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number) && double.IsFinite(number);
    }
}

internal sealed class ModelValidationChart : FrameworkElement
{
    internal enum ChartMode { Scatter, Histogram }
    internal ChartMode Mode { get; init; }
    private ModelValidationSeries? series;
    internal ModelValidationSeries? Series { get => series; set { series = value; InvalidateVisual(); } }

    internal ModelValidationChart()
    {
        MinHeight = 340;
        ClipToBounds = true;
        Background = Brushes.White;
    }

    public Brush Background { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (RenderSize.Width < 220 || RenderSize.Height < 180 || Series == null) return;
        var plot = new Rect(110, 46, Math.Max(1, RenderSize.Width - 140), Math.Max(1, RenderSize.Height - 112));
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(229, 235, 243)), 1) { DashStyle = DashStyles.Dash };
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(95, 112, 128)), 1);
        var title = Mode == ChartMode.Scatter ? "Valores observados × estimados" : "Frequência relativa do erro";
        Text(dc, title, plot.Left, 14, 17, Brushes.DarkSlateGray);
        if (Mode == ChartMode.Scatter) DrawScatter(dc, plot, gridPen, axisPen);
        else DrawHistogram(dc, plot, gridPen, axisPen);
    }

    private void DrawScatter(DrawingContext dc, Rect plot, Pen gridPen, Pen axisPen)
    {
        var points = Series!.ScatterSample;
        if (points.Length == 0) { Text(dc, "Sem pares numéricos válidos.", plot.Left, plot.Top + 20, 14); return; }
        double min = points.Min(p => Math.Min(p.Observed, p.Estimated));
        double max = points.Max(p => Math.Max(p.Observed, p.Estimated));
        if (!(max > min)) { double padding = Math.Max(Math.Abs(min) * .05, .5); min -= padding; max += padding; }
        double paddingRange = (max - min) * .04; min -= paddingRange; max += paddingRange;
        double range = max - min;
        double scale = Math.Min(plot.Width / range, plot.Height / range);
        double side = range * scale;
        var square = new Rect(plot.Left + (plot.Width - side) / 2, plot.Top + (plot.Height - side) / 2, side, side);
        DrawGrid(dc, square, min, max, gridPen, axisPen);
        var identity = new Pen(new SolidColorBrush(Color.FromRgb(213, 109, 44)), 2) { DashStyle = DashStyles.Dash };
        dc.DrawLine(identity, new Point(Map(min, square.Left, square.Width, min, max), Map(min, square.Bottom, -square.Height, min, max)),
            new Point(Map(max, square.Left, square.Width, min, max), Map(max, square.Bottom, -square.Height, min, max)));
        var brush = new SolidColorBrush(Color.FromArgb(150, 33, 102, 157));
        foreach (var item in points)
        {
            double x = Map(item.Observed, square.Left, square.Width, min, max);
            double y = Map(item.Estimated, square.Bottom, -square.Height, min, max);
            if (double.IsFinite(x) && double.IsFinite(y)) dc.DrawEllipse(brush, null, new Point(x, y), 3, 3);
        }
        Text(dc, "Observado", square.Left + square.Width / 2 - 28, plot.Bottom + 34, 12);
        VerticalText(dc, "Estimado", 38, square.Top + square.Height / 2, 12);
        Text(dc, $"{points.Length:N0} pontos na amostra visual", plot.Left, RenderSize.Height - 22, 10, Brushes.SlateGray);
    }

    private void DrawHistogram(DrawingContext dc, Rect plot, Pen gridPen, Pen axisPen)
    {
        var bins = Series!.Histogram;
        if (bins.Length == 0) { Text(dc, "Sem erros relativos calculáveis.", plot.Left, plot.Top + 20, 14); return; }
        const double min = -1, max = 1;
        double maxFrequency = bins.Max(bin => bin.RelativeFrequency);
        if (!(maxFrequency > 0)) maxFrequency = 1;
        double top = maxFrequency * 1.08;
        for (int tick = 0; tick <= 4; tick++)
        {
            double yValue = top * tick / 4;
            double y = plot.Bottom - yValue / top * plot.Height;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            TextRight(dc, yValue.ToString("G4", CultureInfo.CurrentCulture), plot.Left - 7, y - 8, 10);
        }
        dc.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight); dc.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);
        double binWidth = plot.Width / bins.Length;
        var brush = new SolidColorBrush(Color.FromRgb(33, 102, 157));
        foreach (var (bin, index) in bins.Select((item, index) => (item, index)))
        {
            double height = bin.RelativeFrequency / top * plot.Height;
            dc.DrawRectangle(brush, new Pen(Brushes.White, 1), new Rect(plot.Left + index * binWidth + 1, plot.Bottom - height, Math.Max(1, binWidth - 2), height));
        }
        for (int tick = 0; tick <= 4; tick++)
        {
            double xValue = min + (max - min) * tick / 4;
            double x = plot.Left + (xValue - min) / (max - min) * plot.Width;
            Text(dc, xValue.ToString("G4", CultureInfo.CurrentCulture), x - 16, plot.Bottom + 8, 10);
        }
        Text(dc, "Erro relativo (−1 a 1)", plot.Left + plot.Width / 2 - 58, RenderSize.Height - 23, 11);
        VerticalText(dc, "Frequência relativa", 38, plot.Top + plot.Height / 2, 11);
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double min, double max, Pen gridPen, Pen axisPen)
    {
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = min + (max - min) * tick / 4;
            double x = plot.Left + plot.Width * tick / 4;
            double y = plot.Bottom - plot.Height * tick / 4;
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(dc, value.ToString("G4", CultureInfo.CurrentCulture), x - 16, plot.Bottom + 8, 10);
            TextRight(dc, value.ToString("G4", CultureInfo.CurrentCulture), plot.Left - 7, y - 8, 10);
        }
        dc.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight); dc.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);
    }

    private void VerticalText(DrawingContext dc, string value, double centerX, double centerY, double size)
    {
        var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.PushTransform(new RotateTransform(-90, centerX, centerY));
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, centerY - formatted.Height / 2));
        dc.Pop();
    }

    private static double Map(double value, double origin, double length, double min, double max) => origin + (value - min) / (max - min) * length;

    private void Text(DrawingContext dc, string value, double x, double y, double size, Brush? brush = null)
    {
        var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    private void TextRight(DrawingContext dc, string value, double right, double y, double size)
    {
        var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(right - formatted.Width, y));
    }
}
