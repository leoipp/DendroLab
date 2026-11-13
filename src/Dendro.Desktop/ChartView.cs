using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed partial class ChartView : DockPanel
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly bool fitting;
    internal bool IsFitting => fitting;
    private JsonObject config = new(),
        metadata = new();
    private string[] paths = Array.Empty<string>();
    private JsonObject[] graphSources = [];
    private bool loading;
    private int revision;
    private readonly ComboBox source = new(),
        x = new(),
        y = new(),
        x2 = new(),
        x3 = new(),
        stratum = new(),
        group = new(),
        models = new(),
        color = new();
    private readonly TextBox title = Ui.Input(),
        xlabel = Ui.Input(),
        ylabel = Ui.Input(),
        size = Ui.Input("3"),
        pointAlpha = Ui.Input("1"),
        width = Ui.Input("1600"),
        height = Ui.Input("1000"),
        xmin = Ui.Input(),
        xmax = Ui.Input(),
        ymin = Ui.Input(),
        ymax = Ui.Input();
    private readonly TextBlock formula = new() { TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox xTickStep=Ui.Input(), yTickStep=Ui.Input();
    private readonly TextBox violinBandwidth = Ui.Input("1");
    private readonly TextBox violinBins = Ui.Input("12");
    private readonly ComboBox violinResolution = Ui.Combo(["48 pontos", "64 pontos", "96 pontos", "128 pontos", "192 pontos"]);
    private readonly ComboBox violinOutlierRule = Ui.Combo(["1,5 × IQR (padrão)", "2 × IQR", "3 × IQR"]);
    private readonly CheckBox grid = new() { Content = "Grade", IsChecked = true },
        logx = new() { Content = "X logarítmico" },
        logy = new() { Content = "Y logarítmico" };
    private readonly Stack<JsonArray> undo = new();
    private readonly CheckBox compareModels = new() { Content = "Ajustar e comparar modelos", Margin = new Thickness(2, 5, 2, 7) };
    private readonly CheckBox showCurrentCurve = new() { Content = "Mostrar curva vigente do catálogo", Margin = new Thickness(2, 5, 2, 5) };
    private readonly ComboBox currentCurveSource = Ui.Combo(["Regressão salva", "Índice de sítio", "Crescimento e produção"]);
    private readonly ComboBox siteIndexModel = new();
    private readonly TextBox siteIndexReferenceAge = Ui.Input("7");
    private readonly TextBox siteIndexDeviation = Ui.Input("20");
    private readonly CheckBox showSiteIndexCurves = new() { Content = "Mostrar nove curvas anamórficas", Margin = new Thickness(2, 5, 2, 5) };
    private JsonObject[] siteIndexModels = [];
    private string currentCurveMessage = "";
    private readonly ComboBox comparePredictors = Ui.Combo([
        "Todos (1P, 2P e 3P)",
        "Somente 1P",
        "Somente 2P",
        "Somente 3P",
        "1P + 2P",
        "1P + 3P",
        "2P + 3P",
    ]);
    private readonly Dictionary<string, ChartWorkspace> chartWorkspaces = new();
    private readonly TextBlock status = Ui.Text("");
    private Button? fitCurrentGraph;
    private readonly ComboBox predictorCount = Ui.Combo(["1 variável (1P)", "2 variáveis (2P)", "3 variáveis (3P)"]);
    private FrameworkElement? predictorCountField, x2Field, x3Field;
    private FrameworkElement? comparePredictorsField;
    private ScatterPlot? currentPlot;
    private ViolinPlot? currentViolinPlot;

    public ChartView(MainWindow owner, ProjectSession session, bool fitting)
    {
        this.owner = owner;
        this.session = session;
        this.fitting = fitting;
        var header = owner.ToolHeader(fitting ? "Ajuste de regressão" : "Gráficos");
        header.Margin = new Thickness(10);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        var tabs = new TabControl();
        Children.Add(tabs);
        var data = Ui.Form();
        Ui.Section(data, fitting ? "Gráfico de origem" : "Dados do gráfico");
        Ui.Field(data, fitting ? "Gráfico de origem" : "Planilha de origem", source);
        System.Windows.Automation.AutomationProperties.SetAutomationId(source, "Chart.Source");
        if (!fitting)
        {
            System.Windows.Automation.AutomationProperties.SetAutomationId(x, "Chart.X");
            System.Windows.Automation.AutomationProperties.SetAutomationId(y, "Chart.Y");
            Ui.Field(data, "X", x);
            Ui.Field(data, "Y", y);
            Ui.Field(data, "Título", title);
        }
        Ui.Field(data, "Coluna de estrato", stratum);
        Ui.Field(data, "Grupo", group);
        var generateGraph = Ui.AsyncButton(
            "Gerar / atualizar gráfico",
            () => owner.Execute(() => Generate())
        );
        if (!fitting)
        {
            var primaryActions = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            primaryActions.ColumnDefinitions.Add(new ColumnDefinition());
            primaryActions.ColumnDefinitions.Add(new ColumnDefinition());
            primaryActions.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            primaryActions.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            StyleChartAction(generateGraph, "Gerar / atualizar gráfico");
            Grid.SetColumn(generateGraph, 0);
            primaryActions.Children.Add(generateGraph);

            fitCurrentGraph = Ui.AsyncButton("Ajustar este gráfico", () => owner.Execute(() => owner.OpenChart(config.S("path"), true, config)));
            StyleChartAction(fitCurrentGraph, "Ajustar este gráfico");
            fitCurrentGraph.IsEnabled = false;
            Grid.SetColumn(fitCurrentGraph, 1);
            primaryActions.Children.Add(fitCurrentGraph);
            data.Children.Add(primaryActions);

            var batchButton = Ui.AsyncButton("Gerar gráficos em lote…", () => owner.Execute(GenerateBatch));
            batchButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            batchButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            batchButton.MinWidth = 0;
            batchButton.Margin = new Thickness(2, 2, 2, 5);
            data.Children.Add(batchButton);
            Ui.AddHelp(data, "Aplica X, Y, estrato/grupo e aparência atuais a várias tabelas. Cada gráfico fica nos Conteúdos da sua tabela.");
        }
        else
        {
            data.Children.Add(generateGraph);
        }

        status.Margin = new Thickness(0);
        status.TextWrapping = TextWrapping.Wrap;
        var statusFooter = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 232, 239)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 7, 16, 8),
            Child = status,
        };
        DockPanel.SetDock(statusFooter, Dock.Bottom);
        var dataPanel = new DockPanel();
        dataPanel.Children.Add(statusFooter);
        dataPanel.Children.Add(Ui.Scroll(data));
        tabs.Items.Add(new TabItem { Header = "Dados", Content = dataPanel });
        var appearance = Ui.Form();
        Ui.Section(appearance, "Rótulos e aparência");
        Ui.Field(appearance, "Rótulo X", xlabel);
        Ui.Field(appearance, "Rótulo Y", ylabel);
        Ui.Field(appearance, "Tamanho dos pontos (1 a 12)", size);
        Ui.Field(appearance, "Alpha dos pontos (0 = transparente; 1 = opaco)", pointAlpha);
        color.ItemsSource = new[] { "Azul", "Verde", "Laranja", "Roxo", "Vinho" };
        color.SelectedIndex = 0;
        Ui.Field(appearance, "Cor", color);
        foreach (var check in new[] { grid, logx, logy })
            appearance.Children.Add(check);
        Ui.Field(appearance, "X mínimo (vazio = automático)", xmin);
        Ui.Field(appearance, "X máximo", xmax);
        Ui.Field(appearance, "Y mínimo", ymin);
        Ui.Field(appearance, "Y máximo", ymax);
        Ui.Field(appearance,"Intervalo das marcações X (vazio = automático)",xTickStep);
        Ui.Field(appearance,"Intervalo das marcações Y (vazio = automático)",yTickStep);
        Ui.AddHelp(appearance,"As marcações automáticas priorizam inteiros quando a escala permite. Use intervalos como 1, 5, 10 ou 0,1. Em escala logarítmica, o intervalo é medido em expoentes de 10. Máximo de 200 divisões por eixo.");
        appearance.Children.Add(
            Ui.AsyncButton("Aplicar aparência", () => owner.Execute(() => Generate()))
        );
        if (!fitting)
            tabs.Items.Add(new TabItem { Header = "Aparência", Content = Ui.Scroll(appearance) });
        if (!fitting)
        {
            var violin = Ui.Form();
            Ui.Section(violin, "Distribuição e identificação de outliers");
            Ui.Field(violin, "Bins no eixo X (2–100)", violinBins);
            Ui.Field(violin, "Resolução da densidade", violinResolution);
            Ui.Field(violin, "Suavização da distribuição", violinBandwidth);
            Ui.Field(violin, "Limite de outlier", violinOutlierRule);
            Ui.AddHelp(violin, "O eixo X é dividido em intervalos de mesma largura. Cada intervalo forma um violino com a distribuição dos valores de Y naquele trecho de X. A resolução controla a suavidade do contorno; a largura de banda também ajusta a suavização. Pontos além dos limites de Tukey são marcados como possíveis outliers.");
            violinResolution.SelectedIndex = 2;
            violinOutlierRule.SelectedIndex = 0;
            tabs.Items.Add(new TabItem { Header = "Violino", Content = Ui.Scroll(violin) });

            var siteIndex = Ui.Form();
            Ui.Section(siteIndex, "Famílias de curvas de índice de sítio");
            siteIndexModels = session.Meta.Arr("site_index_models").OfType<JsonObject>()
                .Where(model => model.B("vigente", true) && model.S("source_y") is "Hd" or "DAPd")
                .OrderBy(model => model.S("codigo"), StringComparer.OrdinalIgnoreCase).ToArray();
            siteIndexModel.ItemsSource = siteIndexModels.Select(model => $"{model.S("codigo")} · {model.S("name")} · Y={model.S("source_y")} · base {model.N("reference_age"):G5} {model.S("age_unit")}").ToArray();
            if (siteIndexModels.Length > 0) siteIndexModel.SelectedIndex = 0;
            Ui.Field(siteIndex, "Equação-guia vigente", siteIndexModel);
            Ui.Field(siteIndex, "Idade-base no eixo X (anos)", siteIndexReferenceAge);
            Ui.Field(siteIndex, "Desvio máximo na idade-base (%)", siteIndexDeviation);
            siteIndex.Children.Add(showSiteIndexCurves);
            Ui.AddHelp(siteIndex, "A equação-guia gera nove curvas anamórficas: valores igualmente espaçados entre −desvio e +desvio na idade-base. Cada três curvas formam as classes 1 (inferior), 2 (intermediária) e 3 (superior); a curva central coincide com a guia. Para base de 84 meses, informe 7 anos. As curvas só são aplicadas quando você atualiza o gráfico.");
            siteIndex.Children.Add(Ui.AsyncButton("Aplicar curvas ao gráfico", () => owner.Execute(ApplySiteIndexCurves)));
            tabs.Items.Add(new TabItem { Header = "Índice de sítio", Content = Ui.Scroll(siteIndex) });
        }
        var fit = Ui.Form();
        Ui.Section(fit, "Modelo de regressão");
        predictorCountField = Ui.Field(fit, "Número de variáveis", predictorCount);
        x2Field = Ui.Field(fit, "Variável X2", x2);
        x3Field = Ui.Field(fit, "Variável X3", x3);
        System.Windows.Automation.AutomationProperties.SetAutomationId(x2, "Chart.X2");
        System.Windows.Automation.AutomationProperties.SetAutomationId(x3, "Chart.X3");
        Ui.Field(fit, "Modelo", models);
        models.ToolTip = "Modelos cadastrados para o tipo de ajuste selecionado.";
        Ui.Field(fit, "Fórmula cadastrada", formula);
        formula.ToolTip = "Fórmula registrada no cadastro do modelo; exibida somente para consulta.";
        Ui.AddHelp(fit, "Em modelos 2P e 3P, a curva é desenhada em função de X1; X2 e X3 ficam fixos na média das observações usadas no ajuste.");
        compareModels.ToolTip = "Marcado: ajusta todos os modelos 1P, 2P e 3P cadastrados. X2 e X3 devem ser informados conforme o modelo.";
        System.Windows.Automation.AutomationProperties.SetAutomationId(compareModels, "Chart.CompareModels");
        fit.Children.Add(compareModels);
        comparePredictorsField = Ui.Field(fit, "Grupos de modelos", comparePredictors);
        System.Windows.Automation.AutomationProperties.SetAutomationId(comparePredictors, "Chart.ComparePredictors");
        comparePredictors.ToolTip = "Escolha quais modelos entram na comparação. Para 2P e 3P, informe as variáveis X2 e X3 correspondentes.";
        if (fitting)
        {
            Ui.Section(fit, "Curva de referência vigente");
            fit.Children.Add(showCurrentCurve);
            Ui.Field(fit, "Catálogo", currentCurveSource);
            Ui.AddHelp(fit, "A curva é sobreposta após o ajuste quando o nome do gráfico em Conteúdos corresponde ao ID do modelo vigente. Em Regressão salva, é usada a versão salva mais recente cujo ID ou nome corresponda ao gráfico.");
            showCurrentCurve.Checked += (_, _) => currentCurveSource.IsEnabled = true;
            showCurrentCurve.Unchecked += (_, _) => currentCurveSource.IsEnabled = false;
            currentCurveSource.IsEnabled = false;
        }
        fit.Children.Add(Ui.AsyncButton("Ajustar", () => owner.Execute(() => Generate("fit", compareModels.IsChecked == true))));
        if (fitting)
            fit.Children.Add(Ui.AsyncButton("Ajuste em lote…", () => owner.Execute(GenerateFitBatch)));
        if (fitting)
            tabs.Items.Add(new TabItem { Header = "Ajuste de regressão", Content = Ui.Scroll(fit) });
        var export = Ui.Form();
        Ui.Section(export, "Tamanho da exportação");
        Ui.Field(export, "Largura (pixels)", width);
        Ui.Field(export, "Altura (pixels)", height);
        export.Children.Add(
            Ui.AsyncButton("Exportar PNG, SVG ou Excel…", () => owner.Execute(Export))
        );
        tabs.Items.Add(new TabItem { Header = "Exportar", Content = Ui.Scroll(export) });
        source.SelectionChanged += async (_, _) =>
        {
            if (loading)
                return;
            await owner.Execute(async () =>
            {
                undo.Clear();
                config = new();
                await LoadSource();
                if (fitting)
                    await Generate();
            });
        };
        predictorCount.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            UpdatePredictorFields();
            RefreshModelOptions();
            MarkChartModified();
        };
        compareModels.Checked += (_, _) => CompareModeChanged();
        compareModels.Unchecked += (_, _) => CompareModeChanged();
        comparePredictors.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            UpdatePredictorFields();
            RefreshModelOptions();
        };
        foreach (var predictor in new[] { x2, x3 })
            predictor.SelectionChanged += (_, _) =>
            {
                if (!loading) MarkChartModified();
            };
        source.DropDownOpened += (_, _) =>
        {
            if (!fitting || loading)
                return;
            loading = true;
            try
            {
                graphSources = session
                    .Meta.Arr("charts")
                    .OfType<JsonObject>()
                    .Select(c => c.DeepClone().AsObject())
                    .ToArray();
                paths = graphSources.Select(c => c.S("path")).ToArray();
                source.ItemsSource = graphSources.Select(GraphLabel).ToArray();
                source.SelectedIndex = Array.FindIndex(
                    graphSources,
                    c => Identity(c) == Identity(config)
                );
            }
            finally
            {
                loading = false;
            }
        };
        stratum.SelectionChanged += async (_, _) =>
        {
            if (!loading)
                await owner.Execute(FillGroups);
        };
        models.SelectionChanged += (_, _) =>
        {
            if (loading)
                return;
            UpdateSelectedModelDisplay();
        };
        if (!fitting)
        {
            source.SelectionChanged += (_, _) => MarkChartModified();
            foreach (var combo in new[] { x, y, stratum, group, color })
                combo.SelectionChanged += (_, _) => MarkChartModified();
            foreach (var input in new[] { title, xlabel, ylabel, size, pointAlpha, xmin, xmax, ymin, ymax, xTickStep, yTickStep, violinBandwidth, violinBins })
                input.TextChanged += (_, _) => MarkChartModified();
            violinResolution.SelectionChanged += (_, _) => MarkChartModified();
            violinOutlierRule.SelectionChanged += (_, _) => MarkChartModified();
            foreach (var check in new[] { grid, logx, logy })
            {
                check.Checked += (_, _) => MarkChartModified();
                check.Unchecked += (_, _) => MarkChartModified();
            }
        }
    }

    private void MarkChartModified()
    {
        if (loading || fitting || fitCurrentGraph == null || string.IsNullOrEmpty(config.S("path"))) return;
        fitCurrentGraph.IsEnabled = false;
        fitCurrentGraph.ToolTip = "Atualize o gráfico antes de ajustar as alterações.";
    }

    private void UpdatePredictorFields()
    {
        int[] compareCounts = AvailableComparePredictors();
        int count = compareModels.IsChecked == true
            ? compareCounts.DefaultIfEmpty(1).Max()
            : predictorCount.SelectedIndex + 1;
        if (predictorCountField != null)
            predictorCountField.Visibility = compareModels.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        if (comparePredictorsField != null)
            comparePredictorsField.Visibility = compareModels.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (x2Field != null) x2Field.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (x3Field != null) x3Field.Visibility = count >= 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int[] SelectedComparePredictors() => comparePredictors.SelectedIndex switch
    {
        1 => [1],
        2 => [2],
        3 => [3],
        4 => [1, 2],
        5 => [1, 3],
        6 => [2, 3],
        _ => [1, 2, 3],
    };

    private int[] AvailableComparePredictors()
    {
        var definitions = metadata?["models"] as JsonArray ?? [];
        var available = definitions
            .OfType<JsonObject>()
            .Select(model => (int)model.N("predictors", 1))
            .ToHashSet();
        return SelectedComparePredictors().Where(available.Contains).ToArray();
    }

    private void CompareModeChanged()
    {
        if (loading) return;
        UpdatePredictorFields();
        RefreshModelOptions();
    }

    public async Task Load(string? path, JsonObject? saved, bool regenerate = true)
    {
        loading = true;
        try
        {
            metadata = (
                await WorkspaceOperations.Run(
                    new() { ["op"] = "metadata", ["project"] = session.Data!.DeepClone() }
                )
            )["result"]!.AsObject();
            graphSources = session
                .Meta.Arr("charts")
                .OfType<JsonObject>()
                .Select(c => c.DeepClone().AsObject())
                .ToArray();
            if (fitting && graphSources.Length == 0)
                throw new InvalidOperationException(
                    "Crie um gráfico antes de abrir Ajuste de regressão."
                );
            paths = fitting
                ? graphSources.Select(c => c.S("path")).ToArray()
                : session.Files.Where(session.IsTabular).ToArray();
            config = saved?.DeepClone().AsObject() ?? new();
            showCurrentCurve.IsChecked = config.B("show_current_curve");
            currentCurveSource.SelectedIndex = Math.Clamp((int)config.N("current_curve_source"), 0, 2);
            currentCurveSource.IsEnabled = fitting && showCurrentCurve.IsChecked == true;
            predictorCount.SelectedIndex = Math.Clamp((int)config.N("predictor_count", 1) - 1, 0, 2);
            comparePredictors.SelectedIndex = (int)config.N("fit_predictor_scope", 0);
            UpdatePredictorFields();
            source.ItemsSource = fitting
                ? graphSources.Select(GraphLabel).ToArray()
                : paths.Select(SourceLabel).ToArray();
            RefreshModelOptions(config.S("model_id"));
            if (fitting && saved != null && !graphSources.Any(c => Identity(c) == Identity(saved)))
                throw new InvalidOperationException("Selecione um gráfico já criado no projeto.");
            source.SelectedIndex = Math.Max(
                0,
                fitting && saved != null
                    ? Array.FindIndex(graphSources, c => Identity(c) == Identity(saved))
                    : Array.FindIndex(
                        paths,
                        p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
                    )
            );
        }
        finally
        {
            loading = false;
        }
        if (paths.Length > 0)
        {
            await LoadSource();
            if ((fitting || saved?.S("x").Length > 0 && saved?.S("y").Length > 0) && regenerate)
                await Generate();
        }
    }

    private void RefreshModelOptions(string? preferredId = null, int? preferredIndex = null)
    {
        var definitions = metadata["models"] as JsonArray ?? new JsonArray();
        int count = predictorCount.SelectedIndex + 1;
        int[] compareCounts = AvailableComparePredictors();
        var available = definitions.OfType<JsonObject>()
            .Where(m => compareModels.IsChecked == true
                ? compareCounts.Contains((int)m.N("predictors", 1))
                : (int)m.N("predictors", 1) == count)
            .ToArray();
        models.ItemsSource = available.Select(model => model.S("name")).ToArray();
        int selected = preferredId is { Length: > 0 }
            ? Array.FindIndex(available, model => model.S("id") == preferredId)
            : -1;
        if (selected < 0 && preferredIndex is int requested)
            selected = requested;
        models.SelectedIndex = selected >= 0 && selected < available.Length ? selected : available.Length > 0 ? 0 : -1;
        UpdateSelectedModelDisplay();
    }

    private JsonObject[] AvailableModels() => (metadata["models"] as JsonArray ?? new JsonArray())
        .OfType<JsonObject>()
        .Where(m => compareModels.IsChecked == true
            ? AvailableComparePredictors().Contains((int)m.N("predictors", 1))
            : (int)m.N("predictors", 1) == predictorCount.SelectedIndex + 1)
        .ToArray();

    private JsonObject? SelectedModel() => models.SelectedIndex >= 0
        ? AvailableModels().ElementAtOrDefault(models.SelectedIndex)
        : null;

    private void UpdateSelectedModelDisplay() => formula.Text = SelectedModel()?.S("formula") ?? "";

    private async Task LoadSource()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= paths.Length)
            return;
        int current = ++revision;
        string path = paths[source.SelectedIndex];
        if (fitting)
        {
            var origin = graphSources[source.SelectedIndex];
            var registered =
                session
                    .Meta.Arr("charts")
                    .OfType<JsonObject>()
                    .FirstOrDefault(c => Identity(c) == Identity(origin))
                ?? throw new InvalidOperationException(
                    "O gráfico de origem foi removido. Selecione outro gráfico."
                );
            if (Identity(config) != Identity(registered))
                config = registered.DeepClone().AsObject();
            InheritGraph(config, registered);
        }
        var option = session.ExcelOptions(path);
        config["path"] = path;
        config["sheet"] = config.S("sheet", option.S("sheet"));
        config["header_row"] = config.N("header_row", option.N("header_row", 1));
        var r = (
            await WorkspaceOperations.Run(
                new()
                {
                    ["op"] = "columns",
                    ["path"] = session.Resolve(path),
                    ["sheet"] = config.S("sheet"),
                    ["header_row"] = config.N("header_row", 1),
                }
            )
        )["result"]!;
        if (current != revision)
            return;
        loading = true;
        try
        {
            source.ItemsSource = fitting
                ? graphSources.Select(GraphLabel).ToArray()
                : paths.Select(SourceLabel).ToArray();
            source.SelectedIndex = Array.FindIndex(
                paths,
                p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
            );
            if (fitting)
                source.SelectedIndex = Array.FindIndex(
                    graphSources,
                    chart => Identity(chart) == Identity(config)
                );
            string[] columns = r["columns"]!.AsArray().Select(v => v.Display()).ToArray();
            x.ItemsSource = columns;
            y.ItemsSource = columns;
            x2.ItemsSource = columns;
            x3.ItemsSource = columns;
            stratum.ItemsSource = new[] { "Global (sem estratificação)" }.Concat(columns).ToArray();
            x.SelectedItem = config.S("x");
            if (x.SelectedIndex < 0)
                x.SelectedIndex = 0;
            var configuredVariables = config["x_variables"] as JsonArray;
            string secondX = configuredVariables is { Count: > 1 }
                ? configuredVariables[1]?.GetValue<string>() ?? ""
                : "";
            x2.SelectedItem = secondX;
            string thirdX = configuredVariables is { Count: > 2 }
                ? configuredVariables[2]?.GetValue<string>() ?? ""
                : "";
            x3.SelectedItem = thirdX;
            y.SelectedItem = config.S("y");
            if (y.SelectedIndex < 0)
                y.SelectedIndex = Math.Min(1, columns.Length - 1);
            stratum.SelectedItem = config.S("stratum");
            if (stratum.SelectedIndex < 0)
                stratum.SelectedIndex = 0;
            title.Text = config.S("title");
            xlabel.Text = config.S("x_label");
            ylabel.Text = config.S("y_label");
            size.Text = config.N("point_size", 3).ToString();
            pointAlpha.Text = config.N("point_alpha", 1).ToString();
            color.SelectedIndex = (int)config.N("color");
            grid.IsChecked = config.B("grid", true);
            logx.IsChecked = config.B("log_x");
            logy.IsChecked = config.B("log_y");
            xmin.Text = config["xmin"].Display();
            xmax.Text = config["xmax"].Display();
            ymin.Text = config["ymin"].Display();
            xTickStep.Text=config["x_tick_step"].Display();yTickStep.Text=config["y_tick_step"].Display();
            ymax.Text = config["ymax"].Display();
            violinResolution.SelectedIndex = config.N("violin_resolution", 96) switch
            {
                <= 48 => 0,
                <= 64 => 1,
                <= 96 => 2,
                <= 128 => 3,
                _ => 4,
            };
            violinBandwidth.Text = config.N("violin_bandwidth", 1).ToString(CultureInfo.CurrentCulture);
            violinBins.Text = config.N("violin_bins", 12).ToString(CultureInfo.CurrentCulture);
            violinOutlierRule.SelectedIndex = config.N("violin_outlier_iqr", 1.5) switch
            {
                <= 1.5 => 0,
                <= 2 => 1,
                _ => 2,
            };
            var definitions = metadata["models"]!.AsArray();
            int selectedModel = config.S("model_id").Length > 0
                ? definitions.ToList().FindIndex(d => d.S("id") == config.S("model_id"))
                : (int)config.N("model", 0);
            var selectedDefinition = definitions.ElementAtOrDefault(selectedModel);
            RefreshModelOptions(selectedDefinition?.S("id"), selectedModel);
            width.Text = config.N("width", 1600).ToString();
            height.Text = config.N("height", 1000).ToString();
        }
        finally
        {
            loading = false;
        }
        await FillGroups();
    }

    private async Task FillGroups()
    {
        string selected = config.B("group_selected") ? config.S("group") : "Todos os estratos";
        var values = new List<string> { "Todos os estratos" };
        if (stratum.SelectedIndex > 0)
        {
            var groups = (
                await WorkspaceOperations.Run(
                    new()
                    {
                        ["op"] = "groups",
                        ["path"] = session.Resolve(config.S("path")),
                        ["sheet"] = config.S("sheet"),
                        ["header_row"] = config.N("header_row", 1),
                        ["column"] = stratum.SelectedItem?.ToString(),
                    }
                )
            )["result"]!.AsArray();
            values.AddRange(groups.Select(n => n.Display()));
        }
        group.ItemsSource = values;
        group.SelectedIndex = config.B("group_selected")
            ? values.Skip(1).ToList().IndexOf(selected) + 1
            : 0;
    }

    private JsonObject Collect()
    {
        if (source.SelectedIndex < 0)
            throw new InvalidOperationException(
                fitting ? "Selecione um gráfico de origem." : "Selecione uma planilha."
            );
        if (
            fitting
            && !session
                .Meta.Arr("charts")
                .OfType<JsonObject>()
                .Any(c => Identity(c) == Identity(config))
        )
            throw new InvalidOperationException("O gráfico de origem não existe mais no projeto.");
        var c = config.DeepClone().AsObject();
        c["x"] = x.SelectedItem?.ToString();
        int variableCount = compareModels.IsChecked == true
            ? AvailableComparePredictors().DefaultIfEmpty(1).Max()
            : predictorCount.SelectedIndex + 1;
        c["predictor_count"] = variableCount;
        c["fit_predictor_scope"] = comparePredictors.SelectedIndex < 0 ? 0 : comparePredictors.SelectedIndex;
        c["fit_predictors"] = compareModels.IsChecked == true
            ? new JsonArray(AvailableComparePredictors().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())
            : null;
        if (fitting)
        {
            c["show_current_curve"] = showCurrentCurve.IsChecked == true;
            c["current_curve_source"] = Math.Max(0, currentCurveSource.SelectedIndex);
        }
        var xVariables = new JsonArray(x.SelectedItem?.ToString());
        if (variableCount >= 2)
        {
            if (x2.SelectedItem is not string selectedX2)
                throw new InvalidOperationException("Selecione a variável X2.");
            xVariables.Add(selectedX2);
        }
        if (variableCount >= 3)
        {
            if (x3.SelectedItem is not string selectedX3)
                throw new InvalidOperationException("Selecione a variável X3.");
            xVariables.Add(selectedX3);
        }
        if (xVariables.Select(n => n!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != variableCount)
            throw new InvalidOperationException("Selecione variáveis explicativas diferentes.");
        c["x_variables"] = xVariables;
        c["y"] = y.SelectedItem?.ToString();
        c["title"] = string.IsNullOrWhiteSpace(title.Text)
            ? $"{c.S("y")} × {c.S("x")}"
            : title.Text;
        title.Text = c.S("title");
        c["stratum"] = stratum.SelectedIndex > 0 ? stratum.SelectedItem?.ToString() : "";
        c["group"] = group.SelectedIndex > 0 ? group.SelectedItem?.ToString() : "";
        c["group_selected"] = group.SelectedIndex > 0;
        c["x_label"] = string.IsNullOrWhiteSpace(xlabel.Text) ? c.S("x") : xlabel.Text;
        c["y_label"] = string.IsNullOrWhiteSpace(ylabel.Text) ? c.S("y") : ylabel.Text;
        c["point_size"] = Ui.Number(size.Text);
        c["point_alpha"] = Ui.Number(pointAlpha.Text);
        double bandwidth = Ui.Number(violinBandwidth.Text);
        if (!double.IsFinite(bandwidth) || bandwidth < 0.25 || bandwidth > 4)
            throw new InvalidDataException("A suavização do violino deve ficar entre 0,25 e 4.");
        c["violin_bandwidth"] = bandwidth;
        double binCount = Ui.Number(violinBins.Text);
        if (!double.IsFinite(binCount) || binCount < 2 || binCount > 100 || binCount != Math.Truncate(binCount))
            throw new InvalidDataException("O número de bins do violino deve ser um inteiro entre 2 e 100.");
        c["violin_bins"] = (int)binCount;
        c["violin_resolution"] = violinResolution.SelectedIndex switch
        {
            0 => 48,
            1 => 64,
            3 => 128,
            4 => 192,
            _ => 96,
        };
        c["violin_outlier_iqr"] = violinOutlierRule.SelectedIndex switch
        {
            1 => 2,
            2 => 3,
            _ => 1.5,
        };
        foreach(var tick in new[]{("x_tick_step",xTickStep),("y_tick_step",yTickStep)})
        {
            if(string.IsNullOrWhiteSpace(tick.Item2.Text))c.Remove(tick.Item1);
            else {double step=Ui.Number(tick.Item2.Text);if(!double.IsFinite(step)||step<=0)throw new InvalidDataException("O intervalo das marcações deve ser positivo.");c[tick.Item1]=step;}
        }
        c["color"] = color.SelectedIndex;
        c["grid"] = grid.IsChecked == true;
        c["log_x"] = logx.IsChecked == true;
        c["log_y"] = logy.IsChecked == true;
        int selectedModelIndex = models.SelectedIndex;
        c["model"] = selectedModelIndex;
        c["model_id"] = SelectedModel()?.S("id") ?? "";
        c["formula"] = formula.Text;
        c["parameters"] = SelectedModel()?.N("parameters", c.N("parameters", 2)) ?? c.N("parameters", 2);
        c["frontier"] = 0;
        c["width"] = Ui.Number(width.Text);
        c["height"] = Ui.Number(height.Text);
        c["fitting"] = fitting;
        foreach (
            var item in new[] { ("xmin", xmin), ("xmax", xmax), ("ymin", ymin), ("ymax", ymax) }
        )
            if (string.IsNullOrWhiteSpace(item.Item2.Text))
                c.Remove(item.Item1);
            else
                c[item.Item1] = Ui.Number(item.Item2.Text);
        if (fitting)
        {
            var origin = session
                .Meta.Arr("charts")
                .OfType<JsonObject>()
                .Single(chart => Identity(chart) == Identity(config));
            InheritGraph(c, origin);
            title.Text = c.S("title");
        }
        if (
            c.N("point_size") < 1
            || c.N("point_size") > 12
            || c.N("parameters") < 1
            || c.N("parameters") > 4
        )
            throw new InvalidOperationException(
                "Pontos: 1–12 pixels; coeficientes: 1–4."
            );
        if (c.N("point_alpha") < 0 || c.N("point_alpha") > 1)
            throw new InvalidOperationException("O alpha dos pontos deve estar entre 0 e 1.");
        for (int axis = 0; axis < 2; axis++)
        {
            string lo = axis == 0 ? "xmin" : "ymin",
                hi = axis == 0 ? "xmax" : "ymax";
            if (c[lo] != null && c[hi] != null && c.N(lo) >= c.N(hi))
                throw new InvalidOperationException("O mínimo deve ser menor que o máximo.");
            if (
                c.B(axis == 0 ? "log_x" : "log_y")
                && ((c[lo] != null && c.N(lo) <= 0) || (c[hi] != null && c.N(hi) <= 0))
            )
                throw new InvalidOperationException("Limites logarítmicos devem ser positivos.");
        }
        if (!fitting)
        {
            foreach (
                var key in new[]
                {
                    "fits",
                    "fit_signature",
                    "frontier_points",
                    "selected_fit",
                    "selected_fits",
                }
            )
                c.Remove(key);
            c["frontier"] = 0;
        }
        else if (
            new[] { "frontier", "bins", "model", "formula", "parameters" }.Any(key =>
                !JsonNode.DeepEquals(c[key], config[key])
            )
        )
            foreach (
                var key in new[]
                {
                    "fits",
                    "fit_signature",
                    "frontier_points",
                    "selected_fit",
                    "selected_fits",
                }
            )
                c.Remove(key);
        return c;
    }

    private string GraphLabel(JsonObject chart) =>
        $"{session.ContentName(chart.S("path"), chart)} · {session.ContentName(chart.S("path"))} / {chart.S("sheet")}";

    private string SourceLabel(string path)
    {
        string sheet = string.Equals(config.S("path"), path, StringComparison.OrdinalIgnoreCase)
            ? config.S("sheet")
            : session.ExcelOptions(path).S("sheet");
        string file = session.ContentName(path);
        return sheet.Length == 0 ? file : $"{sheet} · {file}";
    }

    private static void InheritGraph(JsonObject target, JsonObject origin)
    {
        foreach (
            var key in new[]
            {
                "path",
                "sheet",
                "header_row",
                "x",
                "y",
                "title",
                "x_label",
                "y_label",
                "point_size",
                "point_alpha",
                "x_tick_step", "y_tick_step", "group_colors",
                "color",
                "grid",
                "log_x",
                "log_y",
                "xmin",
                "xmax",
                "ymin",
                "ymax",
            }
        )
        {
            if (origin[key] != null)
                target[key] = origin[key]!.DeepClone();
            else
                target.Remove(key);
        }
        target["point_size"] = origin.N("point_size", 3);
        target["point_alpha"] = origin.N("point_alpha", 1);
    }

    private static DataGridRow? FindFitRow(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is DataGridRow row) return row;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    internal static string Identity(JsonObject c) =>
        "chart:"
        + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    new JsonArray(
                        c.S("path").ToUpperInvariant(),
                        c.S("sheet"),
                        c.S("title")
                    ).ToJsonString()
                )
            )
        );

    internal async Task Generate(string operation = "chart", bool compare = false)
    {
        var c = Collect();
        if (!fitting && operation == "fit")
            throw new InvalidOperationException(
                "Ajustes de modelo ficam na ferramenta Ajuste de regressão."
            );
        if (operation == "fit")
        {
            if (compare && models.Items.Count == 0)
                throw new InvalidOperationException("Não há modelos cadastrados para comparar.");
            if (!compare && models.SelectedIndex < 0)
                throw new InvalidOperationException(
                    "Selecione um modelo cadastrado."
                );
            c.Remove("selected_fit");
            c.Remove("selected_fits");
        }
        var request = c.DeepClone().AsObject();
        request["path"] = session.Resolve(c.S("path"));
        var data = (
            await WorkspaceOperations.Run(
                new()
                {
                    ["op"] = operation,
                    ["project"] = session.Data!.DeepClone(),
                    ["chart"] = request,
                    ["model"] =
                        compare ? -2
                        : models.SelectedIndex < 0 ? -1
                        : models.SelectedIndex,
                }
            )
        )["result"]!.AsObject();
        config = data["chart"]!.DeepClone().AsObject();
        config["path"] = c.S("path");
        if (!fitting)
        {
            if (showSiteIndexCurves.IsChecked == true)
            {
                if (siteIndexModel.SelectedIndex < 0 || siteIndexModel.SelectedIndex >= siteIndexModels.Length)
                    throw new InvalidOperationException("Cadastre ou selecione uma equação vigente de índice de sítio.");
                double referenceAge = Ui.Number(siteIndexReferenceAge.Text);
                double deviation = Ui.Number(siteIndexDeviation.Text);
                SiteIndexCurveOverlay.Add(config, data, siteIndexModels[siteIndexModel.SelectedIndex], referenceAge, deviation);
            }
            else if (config.B("site_index_overlay"))
            {
                data.Remove("reference_curves");
                config["site_index_overlay"] = false;
                config.Remove("site_index_model");
                config.Remove("site_index_reference_age");
                config.Remove("site_index_deviation_percent");
            }
        }
        session.StoreChart(config);
        Render(data);
        if (!fitting && fitCurrentGraph != null)
        {
            fitCurrentGraph.IsEnabled = true;
            fitCurrentGraph.ToolTip = "Abrir regressão usando este gráfico atualizado.";
        }
        int keptPoints, removedPoints, distinctXCount;
        if (ChartPointView.IsPacked(data))
        {
            keptPoints = (int)data.N("point_kept_count");
            removedPoints = (int)data.N("point_count") - keptPoints;
            distinctXCount = (int)data.N("point_distinct_x");
        }
        else
        {
            keptPoints = removedPoints = 0;
            var distinctX = new HashSet<double>();
            foreach (var point in ChartPointView.Open(data, config).Points())
            {
                if (point.Excluded) { removedPoints++; continue; }
                keptPoints++;
                if (distinctX.Count < 2) distinctX.Add(Math.Round(point.X, 10));
            }
            distinctXCount = distinctX.Count;
        }
        status.Text =
            $"{keptPoints:N0} mantidos · {removedPoints:N0} removidos · {data.N("invalid"):N0} linhas inválidas."
            + (
                config["frontier_points"] is JsonArray envelope && envelope.Count > 0
                    ? $"\nFronteira calculada: {envelope.Count} pontos."
                    : ""
            )
            + (!fitting && keptPoints > 1 && distinctXCount < 2
                ? "\nAviso: X tem apenas um valor distinto; confira os eixos antes de ajustar uma regressão."
                : "")
            + (currentCurveMessage.Length > 0 ? "\n" + currentCurveMessage : "");
    }

    private async Task ApplySiteIndexCurves()
    {
        showSiteIndexCurves.IsChecked = true;
        await Generate();
    }

    private void Render(JsonObject data)
    {
        var saved = config.DeepClone().AsObject();
        currentCurveMessage = fitting && saved.B("show_current_curve")
            ? CurrentCurveOverlay.Add(session.Data!, saved, data, session.ContentName(saved.S("path"), saved))
            : "";
        var plot = new ScatterPlot
        {
            Config = saved,
            Data = data,
            RemoveEnabled = fitting,
        };
        currentPlot = plot;
        ToggleRemoval();
        plot.ToolTip = fitting
            ? "Selecione por clique ou contorno. Shift restringe o arraste a uma linha horizontal ou vertical; Ctrl acrescenta; Delete remove; Esc limpa. A seleção sincroniza com o violino."
            : null;
        var regressionMenu = new MenuItem { Header = "Ajuste de regressão…" };
        regressionMenu.Click += async (_, e) =>
        {
            e.Handled = true;
            await owner.Execute(() => owner.OpenChart(saved.S("path"), true, saved));
        };
        plot.ContextMenu = new ContextMenu();
        plot.ContextMenu.Items.Add(regressionMenu);
        var body = new System.Windows.Controls.Grid();
        var graphTabs = new TabControl();
        var scatterBody = new System.Windows.Controls.Grid();
        scatterBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var topPanel = new StackPanel();
        topPanel.Children.Add(PlotLegend.Create(saved, data));
        scatterBody.Children.Add(topPanel);
        scatterBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        System.Windows.Controls.Grid.SetRow(plot, 1);
        scatterBody.Children.Add(plot);
        graphTabs.Items.Add(new TabItem { Header = "Dispersão", Content = scatterBody });
        var violinPlot = new ViolinPlot { Config = saved, Data = data, RemoveEnabled = fitting };
        violinPlot.ToolTip = fitting
            ? "Clique ou arraste para selecionar observações. Segure Shift para traçar uma linha horizontal ou vertical; Ctrl acrescenta à seleção."
            : null;
        currentViolinPlot = violinPlot;
        plot.SelectionChanged += rows => violinPlot.SynchronizeSelection(rows);
        // Route violin-originated selections through the plot's public selection
        // path so toolbar actions, including Remove selection, update their state.
        violinPlot.SelectionChanged += rows => plot.SelectRows(rows);
        var violinSummary = new TextBlock
        {
            Text = "Distribuição de Y dentro de cada intervalo de X. Pontos destacados são possíveis outliers.",
            Margin = new Thickness(12, 7, 8, 7),
            Foreground = new SolidColorBrush(Color.FromRgb(73, 93, 112)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var selectOutliers = Ui.Button("Selecionar possíveis outliers na dispersão", () =>
        {
            if (violinPlot.SelectOutliers() > 0)
                graphTabs.SelectedIndex = 0;
        });
        selectOutliers.Margin = new Thickness(6, 4, 10, 4);
        selectOutliers.ToolTip = "Seleciona no gráfico de dispersão todas as linhas marcadas em vermelho como possíveis outliers.";
        selectOutliers.IsEnabled = false;
        ToolTipService.SetShowOnDisabled(selectOutliers, true);
        var violinBody = new DockPanel();
        var violinHeader = new Grid();
        violinHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        violinHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        violinHeader.Children.Add(violinSummary);
        Grid.SetColumn(selectOutliers, 1);
        violinHeader.Children.Add(selectOutliers);
        DockPanel.SetDock(violinHeader, Dock.Top);
        violinPlot.SummaryChanged += summary =>
        {
            violinSummary.Text = summary;
            selectOutliers.IsEnabled = violinPlot.OutlierCount > 0;
        };
        violinBody.Children.Add(violinHeader);
        violinBody.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = violinPlot,
        });
        graphTabs.Items.Add(new TabItem { Header = "Violino e outliers", Content = violinBody });
        ChartWorkspace? workspace = null;
        var graphSurface = new DockPanel();
        var graphToolbar = new DockPanel { LastChildFill = true, Background = Brushes.White };
        Button fullScreenButton = null!;
        void UpdateFullScreenButton(bool isFullScreen)
        {
            if (workspace != null && owner.IsActiveChartWorkspace(workspace))
                workspace.SetGraphFullScreen(isFullScreen);
            fullScreenButton.Content = new CommandIcon
            {
                Kind = isFullScreen ? CommandIconKind.ExitFullScreen : CommandIconKind.FullScreen,
                Width = 18, Height = 18,
            };
            fullScreenButton.ToolTip = isFullScreen
                ? "Sair da tela cheia (Esc)"
                : "Tela cheia · Ctrl+Alt ou Ctrl+Shift + → próximo gráfico / ← anterior";
            System.Windows.Automation.AutomationProperties.SetName(fullScreenButton,
                isFullScreen ? "Sair da tela cheia" : "Exibir gráfico em tela cheia");
        }
        void ToggleFullScreen() => owner.ToggleChartFullScreen();
        owner.ChartFullScreenChanged += UpdateFullScreenButton;
        fullScreenButton = Ui.IconButton("Tela cheia · Ctrl+Alt ou Ctrl+Shift + → próximo gráfico / ← anterior", CommandIconKind.FullScreen, ToggleFullScreen);
        UpdateFullScreenButton(owner.IsChartFullScreen);
        fullScreenButton.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(fullScreenButton, Dock.Right);
        graphToolbar.Children.Add(fullScreenButton);
        DockPanel.SetDock(graphToolbar, Dock.Top);
        graphSurface.Children.Add(graphToolbar);
        graphSurface.Children.Add(graphTabs);
        body.Children.Add(graphSurface);
        FrameworkElement? modelsPanel = null;
        CheckBox? showRemovedToggle = null;
        if (fitting)
        {
            showRemovedToggle = new CheckBox
            {
                Content = "Mostrar pontos removidos no gráfico",
                IsChecked = saved.B("show_removed"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 8, 0),
            };
            void VisibilityChanged()
            {
                saved["show_removed"] = showRemovedToggle.IsChecked == true;
                if (Identity(config) == Identity(saved))
                    config["show_removed"] = saved["show_removed"]!.DeepClone();
                plot.Config = saved;
                plot.ClearSelection();
                plot.InvalidateVisual();
                session.StoreChart(saved);
            }
            showRemovedToggle.Checked += (_, _) => VisibilityChanged();
            showRemovedToggle.Unchecked += (_, _) => VisibilityChanged();
        }
        if (config["fits"] is JsonArray fits && fits.Count > 0)
        {
            var headers = new[]
            {
                "group",
                "model",
                "status",
                "r2",
                "rmse",
                "aic",
                "bic",
                "n",
                "b0",
                "b1",
                "b2",
                "b3",
            };
            var rows = new JsonArray();
            foreach (var fit in fits)
                rows.Add(
                    new JsonArray(
                        headers
                            .Select(h => fit![h]?.DeepClone())
                            .ToArray()
                    )
                );
            var captions = new[]
            {
                "Estrato",
                "Modelo",
                "Status",
                "R²",
                "RMSE",
                "AIC",
                "BIC",
                "N",
                "b0",
                "b1",
                "b2",
                "b3",
            };
            var table = Ui.Grid(
                new JsonObject
                {
                    ["columns"] = new JsonArray(
                        captions.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()
                    ),
                    ["rows"] = rows,
                }
            );
            table.SelectionMode = DataGridSelectionMode.Extended;
            table.CanUserAddRows = false;
            table.IsReadOnly = true;
            table.SelectionUnit = DataGridSelectionUnit.FullRow;
            var view = (System.Data.DataView)table.ItemsSource;
            view.Table!.Columns.Add("fit_index", typeof(int));
            view.Table.Columns.Add("curve_color", typeof(string));
            for (int i = 0; i < view.Table.Rows.Count; i++)
            {
                view.Table.Rows[i]["fit_index"] = i;
                view.Table.Rows[i]["curve_color"] = Dendro.Core.Regression.FitDisplay.Color(
                    fits[i]!
                );
            }
            table.AutoGeneratingColumn += (_, e) =>
            {
                if (e.PropertyName is "fit_index" or "curve_color")
                    e.Cancel = true;
            };
            table.Columns.Add(
                new DataGridTemplateColumn
                {
                    Header = "Linha",
                    Width = 55,
                    IsReadOnly = true,
                    CellTemplate = (DataTemplate)owner.FindResource("FitLineSwatch"),
                }
            );
            var registerGrowth = new MenuItem
            {
                Header = "Cadastrar em Crescimento e produção…",
                Icon = new CommandIcon { Kind = CommandIconKind.Growth, Width = 16, Height = 16 },
            };
            var saveRegression = new MenuItem
            {
                Header = "Salvar regressão para aplicação…",
                Icon = new CommandIcon { Kind = CommandIconKind.Regression, Width = 16, Height = 16 },
            };
            var saveSiteIndexModel = new MenuItem
            {
                Header = "Salvar como curva-guia de índice de sítio…",
                Icon = new CommandIcon { Kind = CommandIconKind.SiteIndex, Width = 16, Height = 16 },
            };
            var fitMenu = new ContextMenu();
            fitMenu.Items.Add(registerGrowth);
            fitMenu.Items.Add(saveRegression);
            fitMenu.Items.Add(saveSiteIndexModel);
            table.ContextMenu = fitMenu;
            table.PreviewMouseRightButtonDown += (_, e) =>
            {
                var row = FindFitRow(e.OriginalSource as DependencyObject);
                if (row == null)
                {
                    e.Handled = true;
                    return;
                }
                table.SelectedItem = row.Item;
            };
            fitMenu.Opened += (_, _) =>
            {
                registerGrowth.IsEnabled = table.SelectedItem is System.Data.DataRowView selectedRow
                    && GrowthFitRegistration.CanRegister(fits[(int)selectedRow["fit_index"]]!.AsObject());
                saveRegression.IsEnabled = table.SelectedItem is System.Data.DataRowView selectedSaveRow
                    && RegressionFitRegistration.CanRegister(fits[(int)selectedSaveRow["fit_index"]]!.AsObject());
                saveSiteIndexModel.IsEnabled = table.SelectedItem is System.Data.DataRowView selectedSiteRow
                    && SiteIndexFitRegistration.CanRegister(saved, fits[(int)selectedSiteRow["fit_index"]]!.AsObject());
            };
            registerGrowth.Click += async (_, e) =>
            {
                e.Handled = true;
                if (table.SelectedItem is not System.Data.DataRowView selectedRow) return;
                int index = (int)selectedRow["fit_index"];
                await owner.Execute(() => GrowthFitRegistration.Open(owner, session, saved, fits[index]!.AsObject()));
            };
            saveRegression.Click += async (_, e) =>
            {
                e.Handled = true;
                if (table.SelectedItem is not System.Data.DataRowView selectedRow) return;
                int index = (int)selectedRow["fit_index"];
                await owner.Execute(() => RegressionFitRegistration.Open(owner, session, saved, fits[index]!.AsObject()));
            };
            saveSiteIndexModel.Click += async (_, e) =>
            {
                e.Handled = true;
                if (table.SelectedItem is not System.Data.DataRowView selectedSiteRow) return;
                int index = (int)selectedSiteRow["fit_index"];
                await owner.Execute(() => SiteIndexFitRegistration.Open(owner, session, saved, fits[index]!.AsObject()));
            };
            var restore =
                saved["selected_fits"] is JsonArray selectedFits
                    ? selectedFits.Select(n => (int)JsonExtensions.Number(n)).ToHashSet()
                : saved.N("selected_fit", -1) >= 0
                    ? new HashSet<int> { (int)saved.N("selected_fit") }
                : new HashSet<int>();
            foreach (var row in table.Items.OfType<System.Data.DataRowView>())
                if (restore.Contains((int)row["fit_index"]))
                    table.SelectedItems.Add(row);
            table.SelectionChanged += (_, _) =>
            {
                var selected = table
                    .SelectedItems.Cast<System.Data.DataRowView>()
                    .Select(row => (int)row["fit_index"])
                    .Order()
                    .ToArray();
                saved["selected_fits"] = new JsonArray(
                    selected.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()
                );
                saved["selected_fit"] = selected.Length > 0 ? selected[0] : -1;
                if (Identity(config) == Identity(saved))
                {
                    config["selected_fits"] = saved["selected_fits"]!.DeepClone();
                    config["selected_fit"] = saved["selected_fit"]!.DeepClone();
                }
                plot.Config = saved;
                plot.InvalidateVisual();
                session.StoreChart(saved);
            };
            var tablePanel = new DockPanel();
            var hint = Ui.Help("Modelos exibidos · Ctrl + clique para selecionar vários; Shift para selecionar um intervalo.");
            hint.Margin = new Thickness(10, 5, 10, 5);
            DockPanel.SetDock(hint, Dock.Top);
            tablePanel.Children.Add(hint);
            tablePanel.Children.Add(table);
            modelsPanel = tablePanel;
        }
        void RemoveSelectedRows(int[] rows)
        {
            _ = owner.Execute(async () =>
            {
                owner.SetTool(this, reveal: !owner.IsChartFullScreen);
                if (Identity(config) != Identity(saved))
                {
                    await Load(saved.S("path"), saved);
                }
                currentPlot = plot;
                await RemoveRows(rows);
                saved["excluded"] = config["excluded"]?.DeepClone();
            });
        }
        plot.RemoveRequested += RemoveSelectedRows;
        violinPlot.RemoveRequested += RemoveSelectedRows;
        var dataPanel = fitting ? AddRegressionTable(saved,plot,showRemovedToggle!) : null;
        FrameworkElement content = modelsPanel == null && dataPanel == null ? body : workspace = new ChartWorkspace(owner, body, modelsPanel, dataPanel);
        if (workspace != null)
            workspace.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.Control || plot.SelectedRows.Length == 0) return;
                e.Handled = true;
                plot.ConfirmRemoval();
            };
        var document=owner.Document(
            Identity(saved),
            session.ContentName(saved.S("path"), saved),
            content,
            () => ActivatePlot(saved, plot)
        );
        document.Closing += (_, _) =>
        {
            if (owner.IsChartFullScreen) owner.ExitChartFullScreen();
            owner.ChartFullScreenChanged -= UpdateFullScreenButton;
        };
        if (workspace != null) chartWorkspaces[Identity(saved)] = workspace;
        if(fitting)document.Closing+=(_,_)=>
        {
            string key = Identity(saved);
            if(dataCleanups.Remove(key,out var cleanup))cleanup();
            chartWorkspaces.Remove(key);
        };
    }

    private void ActivatePlot(JsonObject saved, ScatterPlot plot)
    {
        if (!ReferenceEquals(owner.ToolContent.Content, this)) return;
        if (Identity(config) == Identity(saved))
        {
            currentPlot = plot;
            ToggleRemoval();
            return;
        }
        _ = owner.Execute(async () =>
        {
            undo.Clear();
            await Load(saved.S("path"), saved, false);
            currentPlot = plot;
            ToggleRemoval();
        });
    }

    private async Task ActivateSavedGraph(JsonObject saved, ScatterPlot plot)
    {
        owner.SetTool(this, reveal: !owner.IsChartFullScreen);
        if (Identity(config) != Identity(saved))
        {
            undo.Clear();
            await Load(saved.S("path"), saved, false);
        }
        currentPlot = plot;
        ToggleRemoval();
    }

    private void ToggleRemoval()
    {
        if (currentViolinPlot != null) currentViolinPlot.RemoveEnabled = fitting;
        if (currentPlot != null)
        {
            currentPlot.RemoveEnabled = fitting;
            if (!currentPlot.RemoveEnabled)
            {
                currentPlot.ClearSelection();
                currentViolinPlot?.ClearSelection();
            }
            currentPlot.Cursor = currentPlot.RemoveEnabled
                ? System.Windows.Input.Cursors.Cross
                : System.Windows.Input.Cursors.Arrow;
        }
    }

    private void PushUndo() =>
        undo.Push(config["excluded"]?.DeepClone().AsArray() ?? new JsonArray());

    internal JsonObject CurrentConfig => config.DeepClone().AsObject();

    internal void RefreshContentNames()
    {
        int selected = source.SelectedIndex;
        bool wasLoading = loading;
        loading = true;
        try
        {
            source.ItemsSource = fitting
                ? graphSources.Select(GraphLabel).ToArray()
                : paths.Select(SourceLabel).ToArray();
            source.SelectedIndex = selected;
        }
        finally
        {
            loading = wasLoading;
        }
    }

    internal async Task RemoveRows(IEnumerable<int> rows)
    {
        PushUndo();
        var excluded = config["excluded"] as JsonArray ?? new JsonArray();
        var ids = excluded.Select(n => n!.GetValue<int>()).Concat(rows).Distinct();
        config["excluded"] = new JsonArray(
            ids.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()
        );
        ClearFits();
        if (currentPlot != null) await RefreshExclusionState(currentPlot, chartWorkspaces.GetValueOrDefault(Identity(config)));
        else await Generate();
    }

    internal async Task UndoRemoval()
    {
        if (undo.TryPop(out var rows))
        {
            config["excluded"] = rows;
            ClearFits();
            if (currentPlot != null) await RefreshExclusionState(currentPlot, chartWorkspaces.GetValueOrDefault(Identity(config)));
            else await Generate();
        }
    }

    private async Task RefreshExclusionState(ScatterPlot plot, ChartWorkspace? workspace)
    {
        var excluded = config.Arr("excluded").Select(n => (int)JsonExtensions.Number(n)).ToHashSet();
        var points = ChartPointView.Open(plot.Data, config);
        if (!ChartPointView.IsPacked(plot.Data))
        foreach (var point in (plot.Data["points"] as JsonArray ?? []).OfType<JsonObject>())
        {
            int row = (int)point.N("row", -1);
            point["excluded"] = excluded.Contains(row);
        }
        plot.Config = config.DeepClone().AsObject();
        plot.Data = plot.Data.DeepClone().AsObject();
        if (currentViolinPlot != null)
        {
            currentViolinPlot.Config = config.DeepClone().AsObject();
            currentViolinPlot.Data = plot.Data;
        }
        plot.ClearSelection();
        session.StoreChart(config);
        workspace?.RemoveModels();
        var counts = await Task.Run(() =>
        {
            int removed = 0, kept = 0;
            foreach (var point in points.Points()) if (point.Excluded) removed++; else kept++;
            return (kept, removed);
        });
        int kept = counts.kept, removed = counts.removed;
        status.Text = $"{kept:N0} mantidos · {removed:N0} removidos · {plot.Data.N("invalid"):N0} linhas inválidas.";
        await RegressionDataReady;
        if (dataRowFilters.TryGetValue(Identity(config), out var updateRows))
            await updateRows(points.Points().Where(point => !point.Excluded).Select(point => point.Row));
    }

    private void ClearFits()
    {
        config.Remove("fits");
        config.Remove("fit_signature");
        config.Remove("frontier_points");
        config.Remove("selected_fit");
        config.Remove("selected_fits");
    }

    private static void StyleChartAction(Button button, string label)
    {
        button.Content = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.MinWidth = 0;
        button.MinHeight = 42;
        button.Margin = new Thickness(2);
    }

    private async Task Export()
    {
        var c = Collect();
        var path = Ui.SaveFile(
            "Imagem PNG|*.png|Gráfico SVG|*.svg|Excel|*.xlsx",
            c.S("title") + ".png"
        );
        if (path == null)
            return;
        int format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => 0,
            ".svg" => 1,
            ".xlsx" => 2,
            _ => throw new InvalidOperationException("Use PNG, SVG ou XLSX."),
        };
        c["path"] = session.Resolve(c.S("path"));
        await WorkspaceOperations.Run(
            new()
            {
                ["op"] = "export",
                ["project"] = session.Data!.DeepClone(),
                ["chart"] = c,
                ["output"] = path,
                ["format"] = format,
            }
        );
        owner.Log("Gráfico exportado: " + path);
    }
}
