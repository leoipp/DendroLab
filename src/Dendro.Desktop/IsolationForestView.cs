using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class IsolationForestView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private string[] paths = [];
    private JsonObject[] savedModels = [];
    private bool loading;
    private TableSource? loadedSource;
    private readonly ComboBox source = Ui.Combo([]), sheet = Ui.Combo([]), x = Ui.Combo([]), y = Ui.Combo([]), models = Ui.Combo([]);
    private readonly ListBox features = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 110, MaxHeight = 210 };
    private readonly TextBox name = Ui.Input("IF_anomalias"), trees = Ui.Input("100"), samples = Ui.Input("256"),
        depth = Ui.Input("0"), maxFeatures = Ui.Input("0"), contamination = Ui.Input("2"), threshold = Ui.Input(), seed = Ui.Input("42"), trainingLimit = Ui.Input("50000");

    public IsolationForestView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner; this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = Ui.Form(); Content = panel;
        panel.Children.Add(owner.ToolHeader("Isolation Forest"));
        Ui.AddHelp(panel, "Detecta observações que são isoladas rapidamente por árvores aleatórias. Selecione as variáveis numéricas e treine um modelo; depois aplique o mesmo modelo a outras tabelas.");
        Ui.Field(panel, "Fonte", source); Ui.Field(panel, "Tabela / worksheet", sheet);
        Ui.Field(panel, "Variáveis numéricas do treinamento (selecione várias)", features);
        Ui.AddHelp(panel, "Selecione medidas, como DAP, H, idade e volume. Códigos e identificadores numéricos podem distorcer a detecção. Linhas incompletas ou não numéricas ficam sem escore.");
        Ui.Field(panel, "Nome do novo modelo", name);
        Ui.Field(panel, "Árvores (1 a 500)", trees);
        Ui.Field(panel, "Amostras por árvore (2 a 4096)", samples);
        Ui.Field(panel, "Profundidade máxima (0 = automática)", depth);
        Ui.Field(panel, "Variáveis sorteadas por árvore (0 = todas)", maxFeatures);
        Ui.Field(panel, "Contaminação de referência (%) — acima de 0 até 50", contamination);
        Ui.Field(panel, "Limiar manual do escore (opcional, 0 a 1; substitui o quantil)", threshold);
        Ui.Field(panel, "Máximo de linhas na amostra de treinamento (2 a 500.000)", trainingLimit);
        Ui.Field(panel, "Semente aleatória", seed);
        Ui.AddHelp(panel, "O limite de treinamento usa amostragem uniforme de linhas completas. Até 250.000 amostras somadas entre árvores. O escore varia de 0 a 1 e não é probabilidade de erro. Empates no limiar não são classificados como anomalia.");
        Ui.Field(panel, "Eixo X do gráfico", x); Ui.Field(panel, "Eixo Y do gráfico", y);
        Ui.AddHelp(panel, "O modelo usa todas as variáveis selecionadas. O gráfico é uma projeção em dois eixos: azul = normal; vermelho = anomalia. O relatório mantém todas as linhas e acrescenta IF_Score e IF_Classificacao.");
        panel.Children.Add(Ui.AsyncButton("Treinar modelo e gerar gráfico", () => owner.Execute(() => Run(true))));
        Ui.Field(panel, "Modelos salvos no projeto", models);
        panel.Children.Add(Ui.Button("Atualizar modelos", RefreshModels));
        panel.Children.Add(Ui.AsyncButton("Aplicar modelo salvo e gerar gráfico", () => owner.Execute(() => Run(false))));
        Ui.AddHelp(panel, "Aplicar um modelo salvo mantém suas variáveis, árvores e limiar de treinamento. A tabela selecionada deve conter as mesmas variáveis.");
        var files = new WrapPanel();
        files.Children.Add(Ui.Button("Exportar modelo JSON…", ExportModel));
        files.Children.Add(Ui.AsyncButton("Importar modelo JSON…", () => owner.Execute(ImportModel)));
        panel.Children.Add(files);
        source.SelectionChanged += async (_, _) => { if (!loading && source.SelectedIndex >= 0) await owner.Execute(() => LoadSource()); };
        sheet.SelectionChanged += async (_, _) => { if (!loading && sheet.SelectedIndex >= 0) await owner.Execute(LoadColumns); };
        RefreshModels();
    }

    internal async Task Initialize(string? initialPath = null, string? initialSheet = null)
    {
        loading = true;
        try
        {
            paths = session.Files.Where(session.IsTabular).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            source.ItemsSource = paths.Select(path => session.ContentName(path)).ToArray();
            source.SelectedIndex = initialPath == null ? -1 : Array.FindIndex(paths, p => p == initialPath);
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
            var available = await Task.Run(() => TableStore.Tables(session.Resolve(path)));
            sheet.ItemsSource = available;
            sheet.SelectedIndex = Array.IndexOf(available, preferred ?? session.ExcelOptions(path).S("sheet"));
            if (sheet.SelectedIndex < 0 && available.Length > 0) sheet.SelectedIndex = 0;
        }
        finally { loading = false; }
        await LoadColumns();
    }

    private async Task LoadColumns()
    {
        loadedSource = null;
        if (source.SelectedIndex < 0 || sheet.SelectedItem is not string table) return;
        string path = paths[source.SelectedIndex];
        var selected = new TableSource(session.Resolve(path), table, session.IsSpreadsheet(path) ? (int)session.ExcelOptions(path).N("header_row", 1) : 1);
        string[] columns = await Task.Run(() => { using var cursor = new TableCursor(selected); return cursor.Columns; });
        features.ItemsSource = columns;
        foreach (string column in columns.Where(c => c is "DAP" or "H")) features.SelectedItems.Add(column);
        string[] axes = new[] { "(Linha de origem)", "(Escore IF)" }.Concat(columns).ToArray();
        x.ItemsSource = y.ItemsSource = axes;
        x.SelectedItem = columns.Contains("DAP") ? "DAP" : "(Linha de origem)";
        y.SelectedItem = columns.Contains("H") ? "H" : "(Escore IF)";
        loadedSource = selected;
    }

    private void RefreshModels()
    {
        savedModels = session.Meta.Arr("anomaly_models").OfType<JsonObject>().Reverse().ToArray();
        models.ItemsSource = savedModels.Select(m => m.S("name") + " · " + m.S("created")).ToArray();
        models.SelectedIndex = savedModels.Length > 0 ? 0 : -1;
    }
    private IsolationForestModel SelectedModel()
    {
        if (models.SelectedIndex < 0) throw new InvalidOperationException("Selecione um modelo salvo.");
        var model = JsonSerializer.Deserialize<IsolationForestModel>(savedModels[models.SelectedIndex]["model"]!.ToJsonString())!;
        IsolationForest.ValidateModel(model); return model;
    }
    private string StoreModel(IsolationForestModel model)
    {
        string id = Guid.NewGuid().ToString("N");
        session.Meta.Arr("anomaly_models").Add(new JsonObject { ["id"] = id, ["name"] = model.Name,
            ["created"] = DateTimeOffset.Now.ToString("O"), ["model"] = JsonSerializer.SerializeToNode(model) });
        session.Touch(); RefreshModels(); return id;
    }
    private static int Integer(TextBox box)
    {
        double value = Ui.Number(box.Text);
        if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue) throw new InvalidOperationException("Informe parâmetros inteiros válidos.");
        return (int)value;
    }
    private static string Axis(ComboBox combo) => combo.SelectedIndex == 0 ? "$row" : combo.SelectedIndex == 1 ? "$score"
        : combo.SelectedItem?.ToString() ?? throw new InvalidOperationException("Selecione os eixos do gráfico.");

    private async Task Run(bool train)
    {
        if (loadedSource == null) throw new InvalidOperationException("Selecione uma fonte e uma tabela válidas.");
        var table = loadedSource;
        string path = paths[source.SelectedIndex];
        string ax = Axis(x), ay = Axis(y);
        string[] selected = features.SelectedItems.Cast<string>().ToArray();
        var progress = owner.StartTableOperation("Lendo tabela para Isolation Forest…");
        var cancellation = owner.OperationCancellation;
        var data = await Task.Run(() => TableStore.Read(table));
        cancellation.ThrowIfCancellationRequested();
        IsolationForestModel model;
        string modelId = train ? "" : savedModels.ElementAtOrDefault(models.SelectedIndex)?.S("id") ?? "";
        if (train)
        {
            var options = new IsolationForestOptions(Integer(trees), Integer(samples), Integer(depth), Integer(maxFeatures), Ui.Number(contamination.Text) / 100,
                string.IsNullOrWhiteSpace(threshold.Text) ? null : Ui.Number(threshold.Text), Integer(seed), Integer(trainingLimit));
            string modelName = name.Text;
            model = await Task.Run(() => IsolationForest.Fit(data, selected, modelName, options, cancellation,
                (percent, message) => progress.Report(new(percent, message))));
        }
        else model = SelectedModel();
        var report = await Task.Run(() => IsolationForest.Report(data, model, ax, ay, cancellation,
            (percent, message) => progress.Report(new(percent, message))));
        cancellation.ThrowIfCancellationRequested();
        if (train) modelId = StoreModel(model);
        var definition = new JsonObject { ["kind"] = "isolation_forest", ["path"] = path, ["sheet"] = table.Table,
            ["header_row"] = table.HeaderRow, ["model_id"] = modelId, ["x"] = ax, ["y"] = ay };
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log(report.Summary.S("decision"));
    }

    private void ExportModel()
    {
        var model = SelectedModel();
        string? path = Ui.SaveFile("Modelo Isolation Forest|*.iforest.json|JSON|*.json", "Modelo.iforest.json");
        if (path != null) Dendro.Core.Configuration.ProjectStore.AtomicWrite(path,
            temp => File.WriteAllText(temp, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true })), true);
    }
    private async Task ImportModel()
    {
        string? path = Ui.PickFile("Modelo Isolation Forest|*.iforest.json;*.json");
        if (path == null) return;
        var model = await Task.Run(() => {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Arquivo de modelo acima de 64 MB.");
            var value = JsonSerializer.Deserialize<IsolationForestModel>(File.ReadAllText(path)) ?? throw new InvalidDataException("Modelo vazio.");
            IsolationForest.ValidateModel(value); return value;
        });
        StoreModel(model); owner.Log("Modelo Isolation Forest importado: " + model.Name);
    }
}
