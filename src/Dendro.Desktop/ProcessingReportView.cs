using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dendro.Core.Processing;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;
using DataTable = Dendro.Core.Spreadsheets.DataTable;

namespace Dendro.Desktop;

internal sealed class ProcessingReportView : DockPanel
{
    internal static bool Handles(string sheet) => sheet is "H Ajuste" or "ESTATISTICAS INV";
    internal static async Task<ProcessingReportView> Create(TableSource source, CancellationToken cancellation)
    {
        if (source.Table == "H Ajuste")
        {
            var report = await Task.Run(() => ProcessingPresentation.Height(source, cancellation), cancellation);
            return new ProcessingReportView(report);
        }
        var inventory = await Task.Run(() => ProcessingPresentation.Inventory(source, cancellation), cancellation);
        return new ProcessingReportView(inventory);
    }

    private readonly TabControl tabs = new();
    private readonly StackPanel information = Ui.Form();
    private readonly ContentControl graph = new();
    private readonly ComboBox diagnostic = Ui.Combo([]);

    private StackPanel Header(string title)
    {
        Background = Brushes.White;
        var header = Ui.Form(); header.Children.Add(Ui.Text(title, 22));
        SetDock(header, Dock.Top); Children.Add(header);
        Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "Informações", Content = Ui.Scroll(information) });
        var plotPanel = new DockPanel();
        var controls = Ui.Form(); Ui.Field(controls, "Diagnóstico", diagnostic);
        SetDock(controls, Dock.Top); plotPanel.Children.Add(controls); plotPanel.Children.Add(graph);
        tabs.Items.Add(new TabItem { Header = "Gráficos", Content = plotPanel });
        return header;
    }

    private ProcessingReportView(HeightPresentation report)
    {
        var header = Header("Ajuste de altura");
        Ui.AddHelp(header, "Os indicadores utilizam todos os pares válidos de altura observada e estimada do relatório. Os gráficos exibem até 20.000 pares por amostragem uniforme. A altura dos eixos é apresentada em metros; os resíduos, em porcentagem.");
        var lots = Ui.Combo(new[] { "Todos os lotes" }.Concat(report.Lots.Rows.Skip(1).Select(r => r[0]?.ToString() ?? "")));
        Ui.Field(header, "Lote", lots);
        diagnostic.ItemsSource = new[] { "Altura observada × estimada", "Resíduos × altura observada" }; diagnostic.SelectedIndex = 0;
        void Render()
        {
            information.Children.Clear();
            var cards = new WrapPanel(); information.Children.Add(cards);
            int selected = lots.SelectedIndex;
            string[] names = ["R²", "RMSE", "MAE", "Bias", "Resíduo médio", "Desvio padrão resíduos", "N observações com predição", "N valores preenchidos"];
            string[] units = ["", "m", "m", "m", "%", "%", "", ""];
            int[] columns = [5, 6, 7, 8, 9, 10, 3, 4];
            for (int i = 0; i < names.Length; i++)
            {
                object? value = selected > 0 ? report.Lots.Value(selected, columns[i])
                    : report.Metrics.Rows.Skip(1).FirstOrDefault(r => r[0]?.ToString() == names[i])?.ElementAtOrDefault(1);
                double pairs = selected > 0 ? report.Lots.Number(selected, 3) : report.Observations;
                Card(cards, names[i], i < 6 && !(pairs > 0) ? null : value, units[i], i >= 6 ? 0 : 3);
            }
            string method = selected > 0 ? report.Lots.Text(selected, 1)
                : report.Metrics.Rows.Skip(1).FirstOrDefault(r => r[0]?.ToString() == "Modo")?.ElementAtOrDefault(1)?.ToString() ?? "";
            information.Children.Add(Ui.Text("Método: " + HeightMode(method)));
            var points = report.Sample.Where(p => selected <= 0 || p.Lot == lots.SelectedItem?.ToString()).ToArray();
            information.Children.Add(Ui.Text($"{report.Observations:N0} pares válidos no relatório · {points.Length:N0} pontos exibidos no filtro atual."));
            if (report.Observations == 0)
                information.Children.Add(Ui.Text("Não há pares de altura observada e estimada para avaliar o ajuste. No modo de alturas do arquivo, esse diagnóstico fica sem dados."));
            bool residual = diagnostic.SelectedIndex == 1;
            Draw(points.Select((p, i) => new ChartPoint(p.Observed, residual ? p.Residual : p.Estimated, i + 1, "Observações")).ToArray(),
                residual ? "Resíduos de altura" : "Altura observada e estimada", "Altura observada (m)", residual ? "Resíduo (%)" : "Altura estimada (m)", residual ? "zero" : "identity");
        }
        lots.SelectionChanged += (_, _) => Render(); diagnostic.SelectionChanged += (_, _) => Render(); Render();
    }

    private ProcessingReportView(InventoryPresentation report)
    {
        var header = Header("Estatísticas do inventário");
        Ui.AddHelp(header, "Os indicadores são os mesmos do relatório exportado, organizados por lote ou talhão. Selecione uma unidade para consultar produção e precisão. A ausência de um valor é indicada por —.");
        var level = Ui.Combo(["Por lote", "Por talhão"]); var unit = Ui.Combo([]);
        int contextColumn = report.Lots.Column("Contexto_ID");
        var scopes = report.Lots.Rows.Skip(1).GroupBy(r => contextColumn >= 0 ? r.ElementAtOrDefault(contextColumn)?.ToString() ?? "" : "")
            .Select(g => (Id: g.Key, Label: contextColumn < 0 ? "Base atual" : string.Join(" · ", new[] {
                g.First().ElementAtOrDefault(report.Lots.Column("Base_Origem"))?.ToString(), g.First().ElementAtOrDefault(report.Lots.Column("Tipo_Inventario"))?.ToString() }.Where(v => !string.IsNullOrWhiteSpace(v)))))
            .ToArray();
        var scope = Ui.Combo(scopes.Select(s => s.Label));
        if (contextColumn >= 0) Ui.Field(header, "Base e tipo de inventário", scope);
        Ui.Field(header, "Nível", level); Ui.Field(header, "Unidade", unit);
        diagnostic.ItemsSource = new[] { "Produção × erro relativo", "Produção esperada × observada" }; diagnostic.SelectedIndex = 0;
        DataTable current = report.Lots;
        DataTable Current() => current;
        void Render()
        {
            var current = Current(); int selected = unit.SelectedIndex;
            information.Children.Clear();
            var cards = new WrapPanel(); information.Children.Add(cards);
            object? Value(string name)
            {
                int col = current.Column(name);
                if (selected > 0) return current.Value(selected, col);
                string id = scopes.ElementAtOrDefault(scope.SelectedIndex).Id ?? "";
                int totalContext = report.Totals.Column("Contexto_ID");
                return report.Totals.Rows.Skip(1).FirstOrDefault(row => row[0]?.ToString() == level.SelectedItem?.ToString()
                    && (totalContext < 0 || row.ElementAtOrDefault(totalContext)?.ToString() == id))?.ElementAtOrDefault(col);
            }
            foreach (var (column, title, units) in new[] { ("n", "Parcelas", ""), ("Área (ha)", "Área efetiva", "ha"),
                ("Vol/ha Med", "Volume médio", "m³/ha"), ("Vol Total", "Volume total", "m³"), ("Erro Rel(%)", "Erro relativo", "%"), ("VMI", "VMI", "m³/fuste") })
                Card(cards, title, Value(column), units);
            var indicators = new DataTable(["Indicador", "Valor"]);
            foreach (object? column in current.Rows.FirstOrDefault()?.Skip(1) ?? [])
            {
                string name = column?.ToString() ?? "";
                if (name == "Contexto_ID") continue;
                indicators.Add(Indicator(name), Format(Value(name)));
            }
            var grid = Ui.Grid(indicators.ToJson()); grid.MinHeight = 260; information.Children.Add(grid);
            bool expected = diagnostic.SelectedIndex == 1;
            string x = expected ? "Vol/ha Esp" : "Vol/ha Med", y = expected ? "Vol/ha Med" : "Erro Rel(%)";
            int xc = current.Column(x), yc = current.Column(y);
            var points = Enumerable.Range(1, Math.Max(0, current.Count - 1)).Where(r => selected <= 0 || selected == r)
                .Select(r => new ChartPoint(current.Number(r, xc), current.Number(r, yc), r, "Unidades"))
                .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && (!expected || p.X > 0)).ToArray();
            Draw(points, expected ? "Produção esperada e observada" : "Produção e precisão", expected ? "Volume esperado (m³/ha)" : "Volume médio (m³/ha)",
                expected ? "Volume observado (m³/ha)" : "Erro relativo (%)", expected ? "identity" : "");
        }
        void ChangeLevel()
        {
            var source = level.SelectedIndex == 1 ? report.Stands : report.Lots;
            current = new DataTable();
            if (source.Count > 0) current.Rows.Add(source.Rows[0]);
            string id = scopes.ElementAtOrDefault(scope.SelectedIndex).Id ?? "";
            int index = source.Column("Contexto_ID");
            foreach (var row in source.Rows.Skip(1)) if (index < 0 || row.ElementAtOrDefault(index)?.ToString() == id) current.Rows.Add(row);
            unit.ItemsSource = new[] { "Total" }.Concat(Current().Rows.Skip(1).Select(r => r[0]?.ToString() ?? "")).ToArray();
            unit.SelectedIndex = 0; Render();
        }
        level.SelectionChanged += (_, _) => ChangeLevel(); unit.SelectionChanged += (_, _) => Render();
        scope.SelectionChanged += (_, _) => ChangeLevel();
        diagnostic.SelectionChanged += (_, _) => Render(); ChangeLevel();
    }

    private void Draw(ChartPoint[] points, string title, string x, string y, string reference)
    {
        if (points.Length == 0) { graph.Content = Ui.Text("Não há observações válidas para este gráfico."); return; }
        var config = new JsonObject { ["title"] = title, ["x_label"] = x, ["y_label"] = y, ["point_alpha"] = .45, ["point_size"] = 3, ["grid"] = true };
        double[][][] curves = [];
        if (reference.Length > 0)
        {
            double min = points.Min(p => p.X), max = points.Max(p => p.X);
            if (max == min) { min -= .5; max += .5; }
            curves = [[[min, reference == "zero" ? 0 : min], [max, reference == "zero" ? 0 : max]]];
            config["fits"] = new JsonArray(new JsonObject { ["group"] = "Referência", ["model"] = reference == "zero" ? "Resíduo zero" : "1:1", ["curve_color"] = "#687D8D", ["line_dashed"] = true, ["converged"] = true });
        }
        graph.Content = AnalysisDocuments.PlotBody(new(config, points, curves, 0));
    }

    private static void Card(Panel panel, string title, object? value, string unit, int decimals = 3)
    {
        var content = new StackPanel(); content.Children.Add(Ui.Text(title));
        content.Children.Add(Ui.Text(Format(value, title == "Parcelas" ? 0 : decimals) + (unit.Length == 0 ? "" : " " + unit), 22));
        panel.Children.Add(new Border { Child = content, Width = 230, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(244, 248, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(223, 233, 241)), BorderThickness = new Thickness(1) });
    }
    private static string Format(object? value, int decimals = 3) => value is double number ? double.IsFinite(number) ? (Math.Abs(number) < .5 * Math.Pow(10, -decimals) ? 0 : number).ToString("N" + decimals, CultureInfo.CurrentCulture) : "—" : value?.ToString() is { Length: > 0 } text ? text : "—";
    private static string HeightMode(string mode) => mode switch { "KEEP_FILE" => "Alturas do arquivo", "MODEL_GENERAL" => "RNA geral", "FINETUNE_MODEL" => "RNA ajustada ao inventário", _ => mode };
    private static string Indicator(string name) => name switch { "n" => "Número de parcelas", "Vol/ha Med" => "Volume médio (m³/ha)", "Vol/ha Esp" => "Volume esperado (m³/ha)", "Erro Rel(%)" => "Erro relativo (%)", "IC Inf" => "Limite inferior do intervalo de confiança", "IC Sup" => "Limite superior do intervalo de confiança", "Vol Total" => "Volume total (m³)", "DAP Med" => "DAP médio (mm)", "H Med" => "Altura média (dm)", "Idade" => "Idade (anos)", "VMI" => "Volume médio individual (m³/fuste)", "Erro Pad" => "Erro padrão", "Desv.Pad" => "Desvio padrão", "Erro Abs" => "Erro absoluto", _ => name };
}
