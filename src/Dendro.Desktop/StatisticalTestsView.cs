using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Configuration;
using Dendro.Core.Regression;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class StatisticalTestsView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly string[] paths;
    private readonly ComboBox source,
        test,
        layout = Ui.Combo(["Valores separados por grupo", "Duas colunas da mesma tabela"]),
        value = Ui.Combo([]),
        second = Ui.Combo([]),
        group = Ui.Combo([]),
        x = Ui.Combo([]),
        model;
    private readonly TextBox alpha = Ui.Input("0,05");
    private readonly ListBox groupChoices = new() { MinHeight = 80, MaxHeight = 200 };
    private readonly StackPanel form;
    private readonly Dictionary<UIElement, FrameworkElement> fields = new();
    private readonly HelpIcon help = Ui.Help("");
    private readonly Button discover;
    private bool loading;
    private RegressionFamily[] families;
    private static readonly string[] GeneralTests =
    [
        "Regressões · identidade de modelos (F / Graybill)",
        "Médias · t de Welch",
        "Médias · t pareado",
        "Variâncias · F",
        "Médias · ANOVA de um fator",
        "Variâncias · Brown–Forsythe",
    ];

    public StatisticalTestsView(
        MainWindow owner,
        ProjectSession session,
        bool identity = false,
        string? path = null
    )
    {
        this.owner = owner;
        this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        paths = session.Files.Where(session.IsTabular).ToArray();
        source = Ui.Combo(paths.Select(p => session.ContentName(p)));
        if (path != null)
            source.SelectedIndex = Array.FindIndex(paths, p => p == path);
        test = Ui.Combo(GeneralTests);
        test.SelectedIndex = identity ? 0 : 1;
        families = RegressionCatalog.Families(session.Data!);
        model = Ui.Combo(families.Select(m => m.Name));
        model.DropDownOpened += (_, _) =>
        {
            string id = families.ElementAtOrDefault(model.SelectedIndex)?.Id ?? "";
            families = RegressionCatalog.Families(session.Data!);
            model.ItemsSource = families.Select(m => m.Name).ToArray();
            model.SelectedIndex =
                families.Length > 0 ? Math.Max(0, Array.FindIndex(families, m => m.Id == id)) : -1;
        };
        form = Ui.Form();
        Content = form;
        form.Children.Add(
            owner.ToolHeader("Testes estatísticos")
        );
        Ui.AddHelp(form, "Escolha o tipo de pergunta: identidade compara equações de regressão; os demais testes comparam médias ou variâncias entre amostras.");
        Ui.Section(form,"Origem e tipo de teste");
        foreach (
            var pair in new (FrameworkElement Element, string Id)[]
            {
                (source, "Source"),
                (test, "Test"),
                (layout, "Layout"),
                (value, "Value"),
                (second, "Second"),
                (group, "Group"),
                (x, "X"),
                (model, "Model"),
                (alpha, "Alpha"),
            }
        )
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                pair.Element,
                "Statistics." + pair.Id
            );
        Field("Tabela de origem", source);
        Field("Teste estatístico", test);
        Ui.AddHelp(form, help);
        Field("Organização das amostras", layout);
        Ui.Section(form,"Variáveis de análise");
        Field("Coluna de valores / Y", value);
        Field("Coluna B (pareada pela mesma linha no teste pareado)", second);
        Field("Coluna X", x);
        Field("Coluna dos grupos", group);
        discover = Ui.AsyncButton("Listar grupos", () => owner.Execute(LoadGroups));
        form.Children.Add(discover);
        Ui.Section(form,"Amostras e significância");
        Field("Grupos incluídos", groupChoices);
        Field("Modelo 1P do projeto", model);
        Field("Significância α (0 a 1)", alpha);
        form.Children.Add(Ui.AsyncButton("Executar teste", () => owner.Execute(Run)));
        source.SelectionChanged += async (_, _) =>
        {
            if (!loading)
                await owner.Execute(LoadColumns);
        };
        group.SelectionChanged += async (_, _) =>
        {
            if (!loading)
                await owner.Execute(LoadGroups);
        };
        test.SelectionChanged += (_, _) => UpdateFields();
        layout.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();
    }

    private void Field(string title, UIElement element)
    {
        fields[element] = Ui.Field(form, title, element);
    }

    private void Visible(UIElement element, bool visible)
    {
        fields[element].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFields()
    {
        bool identity = test.SelectedIndex == 0,
            paired = test.SelectedIndex == 2,
            many = test.SelectedIndex >= 4;
        if (paired)
            layout.SelectedIndex = 1;
        if (identity || many)
            layout.SelectedIndex = 0;
        bool grouped = layout.SelectedIndex == 0;
        discover.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        Visible(layout, !identity && !paired && !many);
        Visible(second, !grouped);
        Visible(group, grouped);
        Visible(groupChoices, grouped);
        Visible(x, identity);
        Visible(model, identity);
        help.Text =
            identity
                ? "Use para perguntar se dois ou mais grupos podem compartilhar a mesma equação de regressão. Selecione X, Y, a coluna que identifica os grupos e uma família de modelo 1P; clique em Listar grupos e mantenha pelo menos dois marcados. O teste compara uma equação comum com ajustes separados por grupo. Não é um teste de médias e não compara colunas estimadas. Para modelos não lineares, F é uma aproximação."
            : paired
                ? "Use quando as duas medições pertencem à mesma unidade: cada linha deve conter A e B da mesma parcela, árvore ou indivíduo. Selecione as duas colunas; o teste verifica se a diferença média A − B é zero. Pares incompletos são descartados juntos."
            : test.SelectedIndex switch
            {
                1 => "t de Welch compara a média de Y entre duas amostras independentes e não exige variâncias iguais. Use Valores separados por grupo, escolha a coluna de grupos, clique em Listar grupos e marque exatamente dois; ou use Duas colunas da mesma tabela se cada amostra estiver em uma coluna. Não use para medições repetidas das mesmas unidades.",
                3 => "F de variâncias compara a variância de duas amostras independentes. Selecione exatamente dois grupos ou duas colunas. É sensível a desvios de normalidade; para dados menos normais, prefira Brown–Forsythe.",
                4 => "ANOVA de um fator compara as médias de três ou mais grupos independentes em uma coluna Y. Selecione a coluna de grupos, clique em Listar grupos e marque os grupos. Um resultado significativo indica que pelo menos uma média difere, mas não identifica qual par.",
                5 => "Brown–Forsythe compara variâncias de dois ou mais grupos usando desvios em relação à mediana; costuma ser mais robusto que o teste F quando os dados não são normais. Selecione Y e a coluna de grupos, liste os grupos e marque pelo menos dois.",
                _ => "Escolha o teste conforme a pergunta e a estrutura da amostra. Os testes de médias e variâncias exigem grupos independentes; confira os pressupostos e observações do resultado.",
            };
    }

    private string CurrentPath =>
        source.SelectedIndex >= 0 && source.SelectedIndex < paths.Length
            ? paths[source.SelectedIndex]
            : throw new InvalidOperationException(
                "Adicione uma planilha ou banco e selecione a origem."
            );

    public Task Initialize() => paths.Length > 0 ? LoadColumns() : Task.CompletedTask;

    private async Task LoadColumns()
    {
        string path = CurrentPath;
        var option = session.ExcelOptions(path);
        var table = new TableSource(
            session.Resolve(path),
            option.S("sheet"),
            (int)option.N("header_row", 1)
        );
        var columns = await Task.Run(() =>
        {
            using var cursor = new TableCursor(table);
            return cursor.Columns;
        });
        loading = true;
        try
        {
            foreach (var combo in new[] { value, second, group, x })
            {
                combo.ItemsSource = columns;
                combo.SelectedIndex = columns.Length > 0 ? 0 : -1;
            }
            if (columns.Length > 1)
                value.SelectedIndex = second.SelectedIndex = 1;
            if (columns.Length > 2)
                group.SelectedIndex = 2;
        }
        finally
        {
            loading = false;
        }
        groupChoices.Items.Clear();
    }

    private async Task LoadGroups()
    {
        if (source.SelectedIndex < 0 || group.SelectedItem is not string column)
            return;
        string path = CurrentPath;
        var option = session.ExcelOptions(path);
        var groups = await Task.Run(() =>
            TableTools.Discover(
                new(session.Resolve(path), option.S("sheet"), (int)option.N("header_row", 1)),
                column,
                []
            )
        );
        groupChoices.Items.Clear();
        foreach (var stratum in groups)
            groupChoices.Items.Add(
                new CheckBox
                {
                    Content = stratum.Value,
                    Tag = stratum.Key.StartsWith("null:") ? "" : stratum.Value,
                    IsChecked = true,
                    Margin = new Thickness(6, 4, 6, 4),
                }
            );
    }

    private async Task Run()
    {
        string path = CurrentPath;
        var option = session.ExcelOptions(path);
        var groups = groupChoices
            .Items.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag!.ToString()!)
            .ToArray();
        bool grouped = layout.SelectedIndex == 0;
        if (grouped && groups.Length < 2)
            throw new InvalidOperationException("Marque pelo menos dois grupos.");
        string selectedModelId = families.ElementAtOrDefault(model.SelectedIndex)?.Id ?? "";
        var selectedModel = RegressionCatalog
            .Families(session.Data!)
            .FirstOrDefault(m => m.Id == selectedModelId);
        var request = new StatisticalRequest(
            (StatisticalTest)test.SelectedIndex,
            value.SelectedItem?.ToString() ?? "",
            second.SelectedItem?.ToString() ?? "",
            grouped ? group.SelectedItem?.ToString() ?? "" : "",
            grouped ? groups : null,
            x.SelectedItem?.ToString() ?? "",
            test.SelectedIndex == 0 && model.SelectedIndex >= 0 ? selectedModel : null,
            Ui.Number(alpha.Text)
        );
        var definition = new JsonObject
        {
            ["kind"] = "statistics",
            ["path"] = path,
            ["sheet"] = option.S("sheet"),
            ["header_row"] = option.N("header_row", 1),
            ["request"] = JsonSerializer.SerializeToNode(request),
        };
        owner.Progress.IsIndeterminate = true;
        var report = await AnalysisDocuments.Compute(session, definition);
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log(report.Summary.S("test") + ": " + report.Summary.S("decision"));
    }
}
