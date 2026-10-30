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
    private readonly Dictionary<UIElement, FrameworkElement> labels = new();
    private readonly HelpIcon help = Ui.Help("");
    private readonly Button discover;
    private bool loading;
    private RegressionFamily[] families;
    private static readonly string[] Tests =
    [
        "Identidade de modelos (F / Graybill)",
        "t de Welch — médias",
        "t pareado — médias",
        "F — variâncias",
        "ANOVA de um fator",
        "Brown–Forsythe — variâncias",
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
        test = Ui.Combo(Tests);
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
            owner.ToolHeader(identity ? "Identidade de modelos" : "Testes estatísticos")
        );
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
        Field("Teste", test);
        Ui.AddHelp(form, help);
        Field("Organização das amostras", layout);
        Field("Coluna de valores / Y", value);
        Field("Coluna B (pareada pela mesma linha no teste pareado)", second);
        Field("Coluna X", x);
        Field("Coluna dos grupos", group);
        discover = Ui.AsyncButton("Listar grupos", () => owner.Execute(LoadGroups));
        form.Children.Add(discover);
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
        var label = Ui.Text(title);
        form.Children.Add(label);
        form.Children.Add(element);
        labels[element] = label;
    }

    private void Visible(UIElement element, bool visible)
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        labels[element].Visibility = element.Visibility;
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
                ? "Compara uma equação comum com ajustes separados por grupo, usando a mesma família. Para modelos não lineares, o F é aproximado. Não use observações dependentes como se fossem réplicas independentes."
            : paired
                ? "Cada linha forma um par A/B. As duas colunas precisam representar as mesmas unidades nas duas condições. Linhas incompletas são omitidas em conjunto."
            : "Informe amostras independentes. Welch compara médias; F e Brown–Forsythe comparam variâncias; ANOVA compara médias de vários grupos. Confira os pressupostos apresentados no resultado.";
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
