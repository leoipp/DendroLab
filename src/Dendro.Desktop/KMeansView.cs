using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class KMeansView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private string[] paths = [];
    private bool loading;
    private TableSource? loadedSource;
    private readonly ComboBox source = Ui.Combo([]), sheet = Ui.Combo([]), x = Ui.Combo([]), y = Ui.Combo([]);
    private readonly ListBox features = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 120, MaxHeight = 240 };
    private readonly TextBox minK = Ui.Input("2"), maxK = Ui.Input("8"), selectedK = Ui.Input(), seed = Ui.Input("42"),
        trainingLimit = Ui.Input("20000"), silhouetteSample = Ui.Input("1000");

    internal KMeansView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var form = Ui.Form(); Content = form;
        form.Children.Add(owner.ToolHeader("K-means · agrupamento"));
        Ui.AddHelp(form, "Agrupa linhas numéricas semelhantes. Compare a curva elbow (inertia) e a silhouette média para escolher K; por padrão, o app seleciona o K com maior silhouette.");
        Ui.Section(form, "Dados");
        Ui.Field(form, "Fonte", source);
        Ui.Field(form, "Tabela / worksheet", sheet);
        Ui.Field(form, "Variáveis numéricas para o agrupamento (selecione 2 ou mais)", features);
        Ui.AddHelp(form, "As variáveis selecionadas são padronizadas por z-score antes de calcular as distâncias. Identificadores, códigos e medidas em escalas incompatíveis podem produzir grupos sem significado.");
        Ui.Field(form, "Eixo X do gráfico de grupos", x);
        Ui.Field(form, "Eixo Y do gráfico de grupos", y);

        Ui.Section(form, "Escolha de K e limites");
        Ui.Field(form, "K mínimo (2 a 12)", minK);
        Ui.Field(form, "K máximo (até 12)", maxK);
        Ui.Field(form, "K final (vazio = maior silhouette)", selectedK);
        Ui.AddHelp(form, "Execute primeiro com o K automático para inspecionar os gráficos. Para seguir o cotovelo observado no elbow, informe aqui o K escolhido e execute novamente.");
        Ui.Field(form, "Semente aleatória", seed);
        Ui.Field(form, "Máximo de linhas para ajustar e avaliar", trainingLimit);
        Ui.Field(form, "Tamanho da amostra silhouette", silhouetteSample);
        Ui.AddHelp(form, "K-means++ é repetido com várias inicializações para reduzir a dependência do centro inicial. Quando a tabela excede o limite, os centros e métricas são ajustados numa amostra uniforme determinística; depois todas as linhas completas recebem um grupo. A silhouette é estimada numa amostra estratificada por grupo.");
        form.Children.Add(Ui.AsyncButton("Calcular elbow e silhouette e gerar grupos", () => owner.Execute(Run)));

        source.SelectionChanged += async (_, _) => { if (!loading && source.SelectedIndex >= 0) await owner.Execute(() => LoadSource()); };
        sheet.SelectionChanged += async (_, _) => { if (!loading && sheet.SelectedIndex >= 0) await owner.Execute(LoadColumns); };
    }

    internal async Task Initialize(string? initialPath = null, string? initialSheet = null)
    {
        loading = true;
        try
        {
            paths = session.Files.Where(session.IsTabular).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            source.ItemsSource = paths.Select(path => session.ContentName(path)).ToArray();
            source.SelectedIndex = initialPath == null ? (paths.Length > 0 ? 0 : -1) : Array.FindIndex(paths, path => path == initialPath);
        }
        finally { loading = false; }
        if (source.SelectedIndex >= 0) await LoadSource(initialSheet);
    }

    private async Task LoadSource(string? preferred = null)
    {
        loadedSource = null;
        if (source.SelectedIndex < 0) return;
        string path = paths[source.SelectedIndex];
        loading = true;
        try
        {
            sheet.ItemsSource = Array.Empty<string>(); features.ItemsSource = Array.Empty<string>();
            string[] tables = await Task.Run(() => TableStore.Tables(session.Resolve(path)));
            sheet.ItemsSource = tables;
            string wanted = preferred ?? session.ExcelOptions(path).S("sheet");
            sheet.SelectedIndex = Array.IndexOf(tables, wanted);
            if (sheet.SelectedIndex < 0 && tables.Length > 0) sheet.SelectedIndex = 0;
        }
        finally { loading = false; }
        if (sheet.SelectedIndex >= 0) await LoadColumns();
    }

    private async Task LoadColumns()
    {
        loadedSource = null;
        if (source.SelectedIndex < 0 || sheet.SelectedItem is not string table) return;
        string path = paths[source.SelectedIndex];
        var selected = new TableSource(session.Resolve(path), table,
            session.IsSpreadsheet(path) ? (int)session.ExcelOptions(path).N("header_row", 1) : 1);
        string[] numeric = await Task.Run(() =>
        {
            using var cursor = new TableCursor(selected);
            var sample = cursor.Rows().Take(250).ToArray();
            return Enumerable.Range(0, cursor.Columns.Length)
                .Where(index => sample.Count(row => double.IsFinite(Numeric(row.ElementAtOrDefault(index)))) >= Math.Min(3, sample.Length))
                .Select(index => cursor.Columns[index]).ToArray();
        });
        features.ItemsSource = numeric;
        foreach (string preferred in new[] { "DAP", "H" })
            if (numeric.Contains(preferred)) features.SelectedItems.Add(preferred);
        x.ItemsSource = numeric; y.ItemsSource = numeric;
        x.SelectedItem = numeric.Contains("DAP") ? "DAP" : numeric.ElementAtOrDefault(0);
        y.SelectedItem = numeric.Contains("H") ? "H" : numeric.ElementAtOrDefault(1);
        loadedSource = selected;
    }

    private static double Numeric(object? value)
    {
        if (value is double number) return number;
        string text = TableCursor.Text(value).Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : double.NaN;
    }

    private static int Integer(TextBox box, string label)
    {
        double number = Ui.Number(box.Text);
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new InvalidOperationException($"Informe um valor inteiro válido para {label}.");
        return (int)number;
    }

    private async Task Run()
    {
        if (loadedSource == null || source.SelectedIndex < 0)
            throw new InvalidOperationException("Selecione uma fonte e uma tabela válidas.");
        string[] selectedFeatures = features.SelectedItems.Cast<string>().ToArray();
        if (selectedFeatures.Length < 2) throw new InvalidOperationException("Selecione pelo menos duas variáveis numéricas.");
        string axisX = x.SelectedItem?.ToString() ?? throw new InvalidOperationException("Selecione o eixo X.");
        string axisY = y.SelectedItem?.ToString() ?? throw new InvalidOperationException("Selecione o eixo Y.");
        int minimum = Integer(minK, "K mínimo"), maximum = Integer(maxK, "K máximo");
        int? finalK = string.IsNullOrWhiteSpace(selectedK.Text) ? null : Integer(selectedK, "K final");
        var options = new KMeansOptions(minimum, maximum, finalK, Integer(seed, "semente"),
            Integer(trainingLimit, "máximo de linhas"), Integer(silhouetteSample, "amostra silhouette"));
        var table = loadedSource;
        string path = paths[source.SelectedIndex];
        var definition = new JsonObject
        {
            ["kind"] = "kmeans", ["path"] = path, ["sheet"] = table.Table, ["header_row"] = table.HeaderRow,
            ["features"] = JsonSerializer.SerializeToNode(selectedFeatures), ["x"] = axisX, ["y"] = axisY,
            ["options"] = JsonSerializer.SerializeToNode(options),
        };
        var progress = owner.StartTableOperation("Lendo tabela para K-means…");
        var cancellation = owner.OperationCancellation;
        var data = await Task.Run(() => TableStore.Read(table));
        cancellation.ThrowIfCancellationRequested();
        var report = await Task.Run(() => KMeansClustering.Run(data, selectedFeatures, axisX, axisY, options, cancellation,
            (percent, message) => progress.Report(new(percent, message))));
        cancellation.ThrowIfCancellationRequested();
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log(report.Summary.S("decision"));
    }
}
