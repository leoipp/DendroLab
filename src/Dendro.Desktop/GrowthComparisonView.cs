using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class GrowthComparisonView : ScrollViewer
{
    private sealed record SourceChoice(string Path, string Sheet, int Header, string Label);
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private GrowthValidationView.CurveChoice[] curves = [];
    private readonly List<SourceChoice> sources = [];
    private bool loadingTables;
    private SourceChoice? loadedSource;
    private readonly ComboBox baseline = Ui.Combo([]), candidate = Ui.Combo([]), oldUnit = Ui.Combo(["anos", "meses"]),
        newUnit = Ui.Combo(["anos", "meses"]), source = Ui.Combo(["Somente comparar curvas"]),
        table = Ui.Combo([]), age = Ui.Combo([]), observed = Ui.Combo([]), dataUnit = Ui.Combo(["anos", "meses"]), year = Ui.Combo(["(sem separação por ano)"]);
    private readonly ListBox unitColumns = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 65, MaxHeight = 120 };
    private readonly TextBox minimum = Ui.Input("0,5"), maximum = Ui.Input("15"), reference = Ui.Input("7"),
        productionUnit = Ui.Input("m³/ha"),
        relative = Ui.Input("5"), absolute = Ui.Input("0"), cutoff = Ui.Input((DateTime.Today.Year - 1).ToString());
    private readonly CheckBox comparable = new() { Content = new TextBlock { Text = "As curvas representam a mesma produção, na mesma unidade, regime e população. Nos gráficos, X é idade e Y é produção acumulada.", TextWrapping = TextWrapping.Wrap } },
        independent = new() { Content = new TextBlock { Text = "Esta base não foi usada para ajustar nem selecionar nenhum dos dois modelos; as unidades selecionadas são independentes entre si.", TextWrapping = TextWrapping.Wrap } };
    private readonly StackPanel validation = new();

    public GrowthComparisonView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner; this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = Ui.Form(); Content = panel;
        panel.Children.Add(owner.ToolHeader("Comparar curvas de produção"));
        Ui.AddHelp(panel, "Compare o ajuste anual de MNP com a curva vigente. O relatório mostra revisão da produção nas mesmas idades e, com dados observados, qual curva prevê melhor.");
        Ui.Field(panel, "Curva vigente / anterior", baseline);
        Ui.Field(panel, "Unidade de X se a curva anterior veio de um gráfico", oldUnit);
        Ui.Field(panel, "Curva nova / ajuste recente", candidate);
        Ui.Field(panel, "Unidade de X se a curva nova veio de um gráfico", newUnit);
        panel.Children.Add(Ui.Button("Atualizar modelos disponíveis", RefreshCurves));
        panel.Children.Add(comparable);
        Ui.Field(panel, "Unidade comum da produção (curvas e observações)", productionUnit);
        Ui.Field(panel, "Idade mínima comum aos ajustes (anos)", minimum);
        Ui.Field(panel, "Idade máxima comum aos ajustes (anos)", maximum);
        Ui.Field(panel, "Idade de referência / rotação (anos)", reference);
        Ui.Field(panel, "Tolerância prática relativa (%)", relative);
        Ui.Field(panel, "Tolerância prática absoluta (unidade da produção)", absolute);
        Ui.AddHelp(panel, "Compatibilidade prática usa 201 idades e a soma das tolerâncias absoluta e relativa. Não é um teste de identidade estatística. Defina uma faixa coberta por ambos os ajustes.");
        Ui.Field(panel, "Base comum de observações (opcional)", source);
        Ui.AddHelp(panel, "Use uma tabela do mesmo estrato/código/regime das curvas. Prepare esse subconjunto em Estratificar quando necessário; o nome da curva não filtra os dados automaticamente.");
        Ui.Field(validation, "Tabela / worksheet da fonte selecionada", table);
        Ui.Field(validation, "Coluna de idade observada", age);
        Ui.Field(validation, "Unidade da idade observada", dataUnit);
        Ui.Field(validation, "Coluna da produção observada (mesma unidade das curvas)", observed);
        Ui.Field(validation, "Coluna do ano de medição / entrada dos dados (opcional)", year);
        Ui.Field(validation, "Último ano da base histórica", cutoff);
        Ui.AddHelp(validation, "Ano de medição e ano de entrada no ajuste podem ser diferentes. Escolha a coluna que identifica de fato os dados adicionados ao ajuste anual.");
        Ui.Field(validation, "Colunas que identificam a unidade independente (selecione uma ou mais)", unitColumns);
        Ui.AddHelp(validation, "Ex.: Talhão + Parcela se parcelas são independentes; use Talhão se a dependência é entre medições do talhão. Todas as medições de cada unidade ficam juntas. Sem identificação de unidade, o relatório é descritivo.");
        validation.Children.Add(independent);
        Ui.AddHelp(validation, "Se os dados novos entraram no ajuste recente, não marque a declaração de independência. Melhor ajuste nesses dados pode ser apenas adaptação à própria base de calibração.");
        validation.Visibility = Visibility.Collapsed; panel.Children.Add(validation);
        Ui.AddHelp(panel, "Curva mais alta não comprova ganho real de produtividade. Atribuir mudança aos dados novos exige controlar idade, sítio, regime, manejo e composição amostral. O teste de identidade existente deve usar grupos adequados; bases anuais cumulativas compartilham observações.");
        panel.Children.Add(Ui.AsyncButton("Comparar curvas e avaliar dados", () => owner.Execute(Run)));
        source.SelectionChanged += async (_, _) => { if (source.SelectedIndex >= 0) await owner.Execute(LoadSource); };
        table.SelectionChanged += async (_, _) => { if (!loadingTables && table.SelectedIndex >= 0) await owner.Execute(LoadColumns); };
        RefreshCurves();
    }

    private void RefreshCurves()
    {
        curves = GrowthValidationView.Available(session).Where(c => c.Issue.Length == 0).ToArray();
        baseline.ItemsSource = curves.Select(c => c.Name).ToArray(); candidate.ItemsSource = curves.Select(c => c.Name).ToArray();
        baseline.SelectedIndex = Array.FindIndex(curves, c => !c.FromChart && c.Name.EndsWith(" · vigente", StringComparison.Ordinal));
        if (baseline.SelectedIndex < 0 && curves.Length > 0) baseline.SelectedIndex = 0;
        candidate.SelectedIndex = Array.FindIndex(curves, c => c.FromChart);
        if (candidate.SelectedIndex < 0 && curves.Length > 1) candidate.SelectedIndex = baseline.SelectedIndex == 0 ? 1 : 0;
    }

    internal Task Initialize()
    {
        sources.Clear();
        // Opening the tool uses project metadata only. External files are opened on selection.
        foreach (string path in session.Files.Where(session.IsTabular).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var option = session.ExcelOptions(path);
            string[] sheets = session.Meta.Arr("processing_results").OfType<JsonObject>()
                .Where(r => string.Equals(r.S("path"), path, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Arr("sheets")).Select(s => s.Display()).Distinct(StringComparer.Ordinal).ToArray();
            if (sheets.Length == 0) sheets = [option.S("sheet")];
            foreach (string sheet in sheets)
                sources.Add(new(path, sheet, session.IsSpreadsheet(path) ? (int)option.N("header_row", 1) : 1,
                    session.ContentName(path) + (sheet.Length > 0 ? " · " + sheet : "")));
        }
        source.ItemsSource = new[] { "Somente comparar curvas" }.Concat(sources.Select(s => s.Label)).ToArray(); source.SelectedIndex = 0;
        return Task.CompletedTask;
    }

    private async Task LoadSource()
    {
        loadedSource = null;
        validation.Visibility = source.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (source.SelectedIndex <= 0) return;
        var selected = sources[source.SelectedIndex - 1];
        loadingTables = true;
        try
        {
            table.ItemsSource = Array.Empty<string>();
            age.ItemsSource = observed.ItemsSource = Array.Empty<string>();
            unitColumns.ItemsSource = Array.Empty<string>();
            independent.IsChecked = false;
            owner.StatusText.Text = "Lendo tabelas da fonte selecionada…";
            string[] tables = await Task.Run(() => TableStore.Tables(session.Resolve(selected.Path)));
            table.ItemsSource = tables;
            table.SelectedIndex = Array.IndexOf(tables, selected.Sheet);
            if (table.SelectedIndex < 0 && tables.Length > 0) table.SelectedIndex = 0;
        }
        finally { loadingTables = false; }
        await LoadColumns();
    }

    private async Task LoadColumns()
    {
        validation.Visibility = source.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        loadedSource = null;
        if (source.SelectedIndex <= 0 || table.SelectedItem is not string sheet) return;
        var selected = sources[source.SelectedIndex - 1] with { Sheet = sheet };
        owner.StatusText.Text = "Lendo colunas da tabela selecionada…";
        string[] columns = await Task.Run(() => { using var cursor = new TableCursor(new(session.Resolve(selected.Path), selected.Sheet, selected.Header)); return cursor.Columns; });
        age.ItemsSource = observed.ItemsSource = columns;
        age.SelectedIndex = Array.FindIndex(columns, c => c is "Idade_anos" or "Idade_meses");
        if (age.SelectedIndex < 0 && columns.Length > 0) age.SelectedIndex = 0;
        dataUnit.SelectedIndex = age.SelectedItem?.ToString() == "Idade_meses" ? 1 : 0;
        observed.SelectedIndex = Array.FindIndex(columns, c => c is "Volume_m3_ha" or "VTCC_ha");
        if (observed.SelectedIndex < 0 && columns.Length > 1) observed.SelectedIndex = 1;
        year.ItemsSource = new[] { "(sem separação por ano)" }.Concat(columns).ToArray(); year.SelectedIndex = 0;
        unitColumns.ItemsSource = columns;
        independent.IsChecked = false;
        loadedSource = selected;
    }

    private async Task Run()
    {
        if (baseline.SelectedIndex < 0 || candidate.SelectedIndex < 0) throw new InvalidOperationException("Selecione a curva vigente e o ajuste novo.");
        if (comparable.IsChecked != true) throw new InvalidOperationException("Confirme a compatibilidade da produção, unidades e população das curvas.");
        var a = curves[baseline.SelectedIndex]; var b = curves[candidate.SelectedIndex];
        if (baseline.SelectedIndex == candidate.SelectedIndex) throw new InvalidOperationException("Selecione duas versões ou ajustes distintos.");
        GrowthCurve Curve(GrowthValidationView.CurveChoice c, ComboBox unit) => c.FromChart ? c.Curve with { AgeUnit = unit.SelectedItem!.ToString()! } : c.Curve;
        SourceChoice? selected = source.SelectedIndex > 0 ? loadedSource : null;
        if (source.SelectedIndex > 0 && selected == null)
            throw new InvalidOperationException("Selecione uma fonte e carregue uma tabela válida para avaliar os dados.");
        double yearNumber = selected != null && year.SelectedIndex > 0 ? Ui.Number(cutoff.Text) : 0;
        if (yearNumber != Math.Truncate(yearNumber) || yearNumber is < 0 or > 9999) throw new InvalidOperationException("Informe um ano histórico inteiro válido.");
        var options = new GrowthComparisonOptions(Ui.Number(minimum.Text), Ui.Number(maximum.Text), Ui.Number(reference.Text),
            Ui.Number(relative.Text), Ui.Number(absolute.Text), age.SelectedItem?.ToString() ?? "", observed.SelectedItem?.ToString() ?? "",
            dataUnit.SelectedItem?.ToString() ?? "anos", unitColumns.SelectedItems.Cast<string>().ToArray(),
            year.SelectedIndex > 0 ? year.SelectedItem!.ToString()! : "", (int)yearNumber, independent.IsChecked == true, productionUnit.Text.Trim());
        var definition = new JsonObject { ["kind"] = "growth_comparison", ["path"] = selected?.Path ?? "", ["sheet"] = selected?.Sheet ?? "", ["header_row"] = selected?.Header ?? 1,
            ["baseline"] = JsonSerializer.SerializeToNode(Curve(a, oldUnit)), ["updated"] = JsonSerializer.SerializeToNode(Curve(b, newUnit)),
            ["options"] = JsonSerializer.SerializeToNode(options) };
        owner.StartTableOperation("Comparando curvas de produção…");
        var report = await AnalysisDocuments.Compute(session, definition);
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log(report.Summary.S("decision"));
    }
}
