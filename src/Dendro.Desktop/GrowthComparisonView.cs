using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class GrowthComparisonView : DockPanel
{
    private sealed record SourceChoice(string Path, string Sheet, int Header, string Label);
    private sealed record CurveOption(string Id, int Index, string Label);
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private GrowthValidationView.CurveChoice[] curves = [];
    private readonly List<SourceChoice> sources = [];
    private bool loadingTables;
    private SourceChoice? loadedSource;
    private readonly ComboBox baseline = Ui.Combo([]), oldUnit = Ui.Combo(["anos", "meses"]),
        newUnit = Ui.Combo(["anos", "meses"]), source = Ui.Combo(["Somente comparar curvas"]),
        table = Ui.Combo([]), age = Ui.Combo([]), observed = Ui.Combo([]), dataUnit = Ui.Combo(["anos", "meses"]), year = Ui.Combo(["(sem separação por ano)"]);
    private readonly ListBox unitColumns = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 65, MaxHeight = 120 };
    private readonly ListBox candidates = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 80, MaxHeight = 150 };
    private CurveOption[] curveOptions = [];
    private readonly TextBox minimum = Ui.Input("0,5"), maximum = Ui.Input("15"), reference = Ui.Input("7"),
        productionUnit = Ui.Input("m³/ha"), relative = Ui.Input("5"), absolute = Ui.Input("0"), cutoff = Ui.Input((DateTime.Today.Year - 1).ToString());
    private readonly CheckBox comparable = new() { Content = new TextBlock { Text = "As curvas representam a mesma produção, na mesma unidade, regime e população. Nos gráficos, X é idade e Y é produção acumulada.", TextWrapping = TextWrapping.Wrap } },
        independent = new() { Content = new TextBlock { Text = "Esta base não foi usada para ajustar nem selecionar nenhum dos modelos; as unidades selecionadas são independentes entre si.", TextWrapping = TextWrapping.Wrap } };
    private readonly StackPanel validation = new();
    private readonly StackPanel baselineUnitField = new(), candidateUnitField = new();
    private readonly Canvas preview = new() { Height = 165, MinHeight = 145, ClipToBounds = true };
    private readonly WrapPanel previewLegend = new();
    private readonly TextBlock selectionSummary = Ui.Text("Selecione uma base e uma ou mais curvas para comparar.", 11);
    private readonly Button runButton;

    public GrowthComparisonView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner; this.session = session;
        var header = owner.ToolHeader("Comparar curvas de produção");
        header.Margin = new Thickness(10, 8, 10, 5);
        DockPanel.SetDock(header, Dock.Top); Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(12, 5, 12, 10), LastChildFill = false };
        runButton = Ui.AsyncButton("Comparar curvas", () => owner.Execute(Run));
        runButton.MinWidth = 145;
        DockPanel.SetDock(runButton, Dock.Right); footer.Children.Add(runButton);
        selectionSummary.Margin = new Thickness(0, 4, 10, 0); footer.Children.Add(selectionSummary);
        DockPanel.SetDock(footer, Dock.Bottom); Children.Add(footer);

        var tabs = new TabControl { Margin = new Thickness(8, 0, 8, 0) };
        Children.Add(tabs);

        var curveForm = Ui.Form();
        Ui.AddHelp(curveForm, "Escolha uma curva base e selecione uma ou mais candidatas. A prévia atualiza conforme as curvas ou a faixa de idades mudam.");
        Ui.Field(curveForm, "Curva base de comparação", baseline);
        Ui.Field(baselineUnitField, "Unidade de X da curva base", oldUnit);
        curveForm.Children.Add(baselineUnitField);
        Ui.Field(curveForm, "Curvas para comparar com a base (Ctrl/Shift para várias)", candidates);
        Ui.Field(candidateUnitField, "Unidade de X para candidatas vindas de gráficos", newUnit);
        curveForm.Children.Add(candidateUnitField);
        curveForm.Children.Add(Ui.Button("Atualizar modelos disponíveis", RefreshCurves));
        var previewHeader = Ui.Text("Prévia das curvas selecionadas", 12);
        previewHeader.FontWeight = FontWeights.SemiBold;
        productionUnit.TextChanged += (_, _) => previewHeader.Text = $"Prévia · Idade (anos) × Produção ({productionUnit.Text.Trim()})";
        curveForm.Children.Add(previewHeader);
        curveForm.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 233)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(5), Child = preview });
        curveForm.Children.Add(previewLegend);
        tabs.Items.Add(new TabItem { Header = "Curvas", Content = Ui.Scroll(curveForm) });

        var rangeForm = Ui.Form();
        rangeForm.Children.Add(comparable);
        Ui.Field(rangeForm, "Unidade comum da produção", productionUnit);
        Ui.Field(rangeForm, "Idade mínima comum aos ajustes (anos)", minimum);
        Ui.Field(rangeForm, "Idade máxima comum aos ajustes (anos)", maximum);
        Ui.Field(rangeForm, "Idade de referência / rotação (anos)", reference);
        Ui.Field(rangeForm, "Tolerância prática relativa (%)", relative);
        Ui.Field(rangeForm, "Tolerância prática absoluta (unidade da produção)", absolute);
        Ui.AddHelp(rangeForm, "Compatibilidade prática avalia 201 idades usando tolerância absoluta + percentual. Não é um teste de identidade estatística. Use uma faixa coberta pelos dois ajustes.");
        tabs.Items.Add(new TabItem { Header = "Faixa e tolerância", Content = Ui.Scroll(rangeForm) });

        var observedForm = Ui.Form();
        Ui.Field(observedForm, "Base comum de observações (opcional)", source);
        Ui.AddHelp(observedForm, "Escolha uma tabela do mesmo estrato, código e regime das curvas. O nome da curva não filtra os dados automaticamente.");
        Ui.Field(validation, "Tabela / worksheet", table);
        Ui.Field(validation, "Coluna de idade observada", age);
        Ui.Field(validation, "Unidade da idade observada", dataUnit);
        Ui.Field(validation, "Coluna da produção observada", observed);
        Ui.Field(validation, "Coluna do ano de medição / entrada (opcional)", year);
        Ui.Field(validation, "Último ano da base histórica", cutoff);
        Ui.Field(validation, "Unidades independentes (selecione uma ou mais)", unitColumns);
        validation.Children.Add(independent);
        Ui.AddHelp(validation, "Talhão + Parcela quando parcelas são independentes; use Talhão se as medições dentro do talhão dependem entre si. Sem unidades identificadas, o relatório é descritivo.");
        Ui.AddHelp(validation, "Não declare independência se os dados novos participaram do ajuste recente. Melhor ajuste nesses dados pode refletir apenas adaptação à base de calibração.");
        validation.Visibility = Visibility.Collapsed; observedForm.Children.Add(validation);
        Ui.AddHelp(observedForm, "Atribuir mudança aos dados novos exige controlar idade, sítio, regime, manejo e composição amostral. Bases anuais cumulativas podem compartilhar observações.");
        tabs.Items.Add(new TabItem { Header = "Dados observados", Content = Ui.Scroll(observedForm) });

        source.SelectionChanged += async (_, _) => { if (source.SelectedIndex >= 0) await owner.Execute(LoadSource); UpdateSummary(); };
        table.SelectionChanged += async (_, _) => { if (!loadingTables && table.SelectedIndex >= 0) await owner.Execute(LoadColumns); };
        baseline.SelectionChanged += (_, _) => { RefreshCandidateOptions(); UpdateUnitFields(); UpdatePreview(); UpdateSummary(); };
        candidates.SelectionChanged += (_, _) => { UpdateUnitFields(); UpdatePreview(); UpdateSummary(); };
        oldUnit.SelectionChanged += (_, _) => UpdatePreview();
        newUnit.SelectionChanged += (_, _) => UpdatePreview();
        foreach (var input in new[] { minimum, maximum, reference, productionUnit, relative, absolute, cutoff })
            input.TextChanged += (_, _) => { if (ReferenceEquals(input, minimum) || ReferenceEquals(input, maximum)) UpdatePreview(); UpdateSummary(); };
        comparable.Checked += (_, _) => UpdateSummary();
        comparable.Unchecked += (_, _) => UpdateSummary();
        preview.SizeChanged += (_, _) => UpdatePreview();
        baseline.DisplayMemberPath = nameof(CurveOption.Label);
        candidates.DisplayMemberPath = nameof(CurveOption.Label);
        RefreshCurves();
    }

    private void RefreshCurves()
    {
        curves = GrowthValidationView.Available(session).Where(c => c.Issue.Length == 0).ToArray();
        var duplicatedNames = curves.GroupBy(c => c.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(curve => curve.Id)).ToHashSet(StringComparer.Ordinal);
        curveOptions = curves.Select((curve, index) => new CurveOption(curve.Id, index,
            duplicatedNames.Contains(curve.Id) ? $"{curve.Name} · curva {index + 1}" : curve.Name)).ToArray();
        // Keep combo items as strings so WPF never falls back to the record's debug ToString().
        baseline.ItemsSource = curveOptions.Select(option => option.Label).ToArray();
        baseline.SelectedIndex = Array.FindIndex(curves, c => !c.FromChart && c.Name.EndsWith(" · vigente", StringComparison.Ordinal));
        if (baseline.SelectedIndex < 0 && curves.Length > 0) baseline.SelectedIndex = 0;
        RefreshCandidateOptions(); UpdateUnitFields(); UpdatePreview(); UpdateSummary();
    }

    private void RefreshCandidateOptions()
    {
        var selected = candidates.SelectedItems.OfType<CurveOption>().Select(option => option.Id).ToHashSet(StringComparer.Ordinal);
        candidates.SelectedItems.Clear();
        var available = curveOptions.Where(option => option.Index != baseline.SelectedIndex).ToArray();
        candidates.ItemsSource = available;
        foreach (var option in available.Where(option => selected.Contains(option.Id))) candidates.SelectedItems.Add(option);
        if (candidates.SelectedItems.Count == 0)
        {
            var preferred = curveOptions.FirstOrDefault(option => option.Index != baseline.SelectedIndex && curves[option.Index].FromChart);
            preferred ??= curveOptions.FirstOrDefault(option => option.Index != baseline.SelectedIndex);
            if (preferred != null) candidates.SelectedItems.Add(preferred);
        }
    }

    private void UpdateUnitFields()
    {
        baselineUnitField.Visibility = baseline.SelectedIndex >= 0 && curves[baseline.SelectedIndex].FromChart ? Visibility.Visible : Visibility.Collapsed;
        candidateUnitField.Visibility = candidates.SelectedItems.OfType<CurveOption>().Any(option => curves[option.Index].FromChart) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string CurveColor(int index)
    {
        if (index == 0) return "#21669D";
        double hue = (24 + (index - 1) * 137.507764) % 360 / 60;
        const double saturation = .72, lightness = .45;
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue % 2 - 1)), m = lightness - chroma / 2;
        (double r, double g, double b) = hue switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x),
        };
        return $"#{(byte)Math.Round((r + m) * 255):X2}{(byte)Math.Round((g + m) * 255):X2}{(byte)Math.Round((b + m) * 255):X2}";
    }

    private GrowthCurve ToCurve(GrowthValidationView.CurveChoice choice, ComboBox unit, string? displayName = null) =>
        choice.Curve with
        {
            AgeUnit = choice.FromChart ? (unit.SelectedItem?.ToString() ?? "anos") : choice.Curve.AgeUnit,
            Name = displayName ?? choice.Name,
        };

    private void UpdatePreview()
    {
        if (!IsLoaded && preview.ActualWidth <= 0) return;
        preview.Children.Clear(); previewLegend.Children.Clear();
        if (baseline.SelectedIndex < 0 || preview.ActualWidth < 60) return;
        if (!double.TryParse(minimum.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double min) ||
            !double.TryParse(maximum.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double max) || min <= 0 || max <= min)
        { previewLegend.Children.Add(Ui.Text("Informe uma faixa de idades válida para ver a prévia.", 11)); return; }

        var selected = new List<(GrowthCurve Curve, string Label, int Index)>();
        int baseIndex = baseline.SelectedIndex;
        selected.Add((ToCurve(curves[baseIndex], oldUnit, curveOptions[baseIndex].Label), "Base · " + curveOptions[baseIndex].Label, 0));
        selected.AddRange(candidates.SelectedItems.OfType<CurveOption>().Select((option, index) =>
            (ToCurve(curves[option.Index], newUnit, option.Label), option.Label, index + 1)));
        if (selected.Count < 2) { previewLegend.Children.Add(Ui.Text("Selecione ao menos uma curva candidata.", 11)); return; }
        try
        {
            var series = selected.Select(item => (item.Label, item.Index, Points: GrowthDiagnostics.Calculate(item.Curve, min, max, samples: 201).Points)).ToArray();
            var finite = series.SelectMany(item => item.Points).Where(point => double.IsFinite(point.Volume)).ToArray();
            if (finite.Length == 0) throw new InvalidDataException("Não há valores finitos nesta faixa.");
            double yMin = Math.Min(0, finite.Min(point => point.Volume)); double yMax = finite.Max(point => point.Volume);
            if (yMax <= yMin) yMax = yMin + 1;
            double width = Math.Max(120, preview.ActualWidth), height = Math.Max(130, preview.ActualHeight);
            const double left = 42, right = 8, top = 8, bottom = 30;
            double plotWidth = width - left - right, plotHeight = height - top - bottom;
            double X(double value) => left + (value - min) / (max - min) * plotWidth;
            double Y(double value) => top + (yMax - value) / (yMax - yMin) * plotHeight;
            for (int tick = 0; tick <= 3; tick++)
            {
                double y = top + plotHeight * tick / 3;
                preview.Children.Add(new Line { X1 = left, X2 = width - right, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(226, 233, 240)), StrokeThickness = 1 });
            }
            preview.Children.Add(new Line { X1 = left, X2 = left, Y1 = top, Y2 = height - bottom, Stroke = new SolidColorBrush(Color.FromRgb(135, 151, 165)), StrokeThickness = 1 });
            preview.Children.Add(new Line { X1 = left, X2 = width - right, Y1 = height - bottom, Y2 = height - bottom, Stroke = new SolidColorBrush(Color.FromRgb(135, 151, 165)), StrokeThickness = 1 });
            AddLabel("Idade (anos)", left + plotWidth / 2 - 35, height - 22);
            AddLabel(min.ToString("G4", CultureInfo.CurrentCulture), left, height - bottom + 2);
            AddLabel(max.ToString("G4", CultureInfo.CurrentCulture), width - right - 28, height - bottom + 2);
            AddLabel(yMax.ToString("G4", CultureInfo.CurrentCulture), 2, top - 2);
            AddLabel(yMin.ToString("G4", CultureInfo.CurrentCulture), 2, height - bottom - 10);
            foreach (var item in series)
            {
                var line = new Polyline { Stroke = (Brush)new BrushConverter().ConvertFromString(CurveColor(item.Index))!, StrokeThickness = item.Index == 0 ? 2.5 : 1.8 };
                foreach (var point in item.Points)
                    if (double.IsFinite(point.Volume)) line.Points.Add(new Point(X(point.Age), Y(point.Volume)));
                preview.Children.Add(line);
                var legendItem = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4) };
                legendItem.Children.Add(new Line { X1 = 0, X2 = 18, Y1 = 8, Y2 = 8, Width = 18, Height = 16, Stroke = line.Stroke, StrokeThickness = line.StrokeThickness, Margin = new Thickness(0, 0, 4, 0) });
                legendItem.Children.Add(Ui.Text(item.Label, 10)); previewLegend.Children.Add(legendItem);
            }
        }
        catch (Exception ex)
        {
            previewLegend.Children.Add(Ui.Text("Prévia indisponível: " + ex.Message, 10));
        }

        void AddLabel(string text, double x, double y)
        {
            var label = Ui.Text(text, 9); label.Margin = new Thickness(0); label.Foreground = new SolidColorBrush(Color.FromRgb(98, 117, 133));
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); preview.Children.Add(label);
        }
    }

    private void UpdateSummary()
    {
        int count = candidates.SelectedItems.Count;
        var missing = new List<string>();
        if (baseline.SelectedIndex < 0) missing.Add("selecione a curva base");
        if (count == 0) missing.Add("selecione ao menos uma candidata");
        if (comparable.IsChecked != true) missing.Add("confirme a compatibilidade na aba Faixa e tolerância");
        if (string.IsNullOrWhiteSpace(productionUnit.Text)) missing.Add("informe a unidade de produção");
        bool minOk = Number(minimum, out double min) && min > 0;
        bool maxOk = Number(maximum, out double max) && max > min;
        bool referenceOk = Number(reference, out double rotation) && rotation >= min && rotation <= max;
        if (!minOk || !maxOk || !referenceOk) missing.Add("revise a faixa e a idade de referência");
        if (!Number(relative, out double rel) || rel < 0 || !Number(absolute, out double abs) || abs < 0)
            missing.Add("use tolerâncias não negativas");
        if (source.SelectedIndex > 0 && loadedSource == null) missing.Add("carregue a fonte na aba Dados observados");
        bool ready = missing.Count == 0;
        string baseName = baseline.SelectedIndex < 0 ? "Sem curva base" : curveOptions[baseline.SelectedIndex].Label;
        selectionSummary.Text = ready
            ? $"Base: {baseName} · {count} curva(s) selecionada(s). Pronto para comparar."
            : $"Base: {baseName} · {count} curva(s) · Falta: {missing[0]}.";
        selectionSummary.ToolTip = ready ? "Configuração validada." : "Para comparar, " + string.Join("; ", missing) + ".";
        runButton.ToolTip = selectionSummary.ToolTip;
        runButton.IsEnabled = ready;
    }

    private static bool Number(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);

    internal Task Initialize()
    {
        sources.Clear();
        foreach (string path in session.Files.Where(session.IsTabular).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var option = session.ExcelOptions(path);
            string[] sheets = session.Meta.Arr("processing_results").OfType<JsonObject>()
                .Where(r => string.Equals(r.S("path"), path, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Arr("sheets")).Select(s => s.Display()).Distinct(StringComparer.Ordinal).ToArray();
            if (sheets.Length == 0) sheets = [option.S("sheet")];
            foreach (string sheet in sheets) sources.Add(new(path, sheet, session.IsSpreadsheet(path) ? (int)option.N("header_row", 1) : 1,
                session.ContentName(path) + (sheet.Length > 0 ? " · " + sheet : "")));
        }
        source.ItemsSource = new[] { "Somente comparar curvas" }.Concat(sources.Select(s => s.Label)).ToArray(); source.SelectedIndex = 0;
        UpdateSummary();
        return Task.CompletedTask;
    }

    private async Task LoadSource()
    {
        loadedSource = null; validation.Visibility = source.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (source.SelectedIndex <= 0) { UpdateSummary(); return; }
        var selected = sources[source.SelectedIndex - 1]; loadingTables = true;
        try
        {
            table.ItemsSource = Array.Empty<string>(); age.ItemsSource = observed.ItemsSource = Array.Empty<string>(); unitColumns.ItemsSource = Array.Empty<string>(); independent.IsChecked = false;
            owner.StatusText.Text = "Lendo tabelas da fonte selecionada…";
            string[] tables = await Task.Run(() => TableStore.Tables(session.Resolve(selected.Path)));
            table.ItemsSource = tables; table.SelectedIndex = Array.IndexOf(tables, selected.Sheet); if (table.SelectedIndex < 0 && tables.Length > 0) table.SelectedIndex = 0;
        }
        finally { loadingTables = false; }
        await LoadColumns();
        UpdateSummary();
    }

    private async Task LoadColumns()
    {
        validation.Visibility = source.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed; loadedSource = null;
        if (source.SelectedIndex <= 0 || table.SelectedItem is not string sheet) return;
        var selected = sources[source.SelectedIndex - 1] with { Sheet = sheet }; owner.StatusText.Text = "Lendo colunas da tabela selecionada…";
        string[] columns = await Task.Run(() => { using var cursor = new TableCursor(new(session.Resolve(selected.Path), selected.Sheet, selected.Header)); return cursor.Columns; });
        age.ItemsSource = observed.ItemsSource = columns;
        age.SelectedIndex = Array.FindIndex(columns, c => c is "Idade_anos" or "Idade_meses"); if (age.SelectedIndex < 0 && columns.Length > 0) age.SelectedIndex = 0;
        dataUnit.SelectedIndex = age.SelectedItem?.ToString() == "Idade_meses" ? 1 : 0;
        observed.SelectedIndex = Array.FindIndex(columns, c => c is "Volume_m3_ha" or "VTCC_ha"); if (observed.SelectedIndex < 0 && columns.Length > 1) observed.SelectedIndex = 1;
        year.ItemsSource = new[] { "(sem separação por ano)" }.Concat(columns).ToArray(); year.SelectedIndex = 0;
        unitColumns.ItemsSource = columns; independent.IsChecked = false; loadedSource = selected;
        UpdateSummary();
    }

    private async Task Run()
    {
        if (baseline.SelectedIndex < 0 || candidates.SelectedItems.Count == 0) throw new InvalidOperationException("Selecione uma curva base e pelo menos uma curva para comparar.");
        if (comparable.IsChecked != true) throw new InvalidOperationException("Confirme a compatibilidade da produção, unidades e população das curvas.");
        var a = curves[baseline.SelectedIndex];
        var selectedCandidates = candidates.SelectedItems.OfType<CurveOption>().Select(option => option.Index).Distinct().ToArray();
        if (selectedCandidates.Contains(baseline.SelectedIndex)) throw new InvalidOperationException("A curva base não pode aparecer também na lista de comparação.");
        SourceChoice? selected = source.SelectedIndex > 0 ? loadedSource : null;
        if (source.SelectedIndex > 0 && selected == null) throw new InvalidOperationException("Selecione uma fonte e carregue uma tabela válida para avaliar os dados.");
        double yearNumber = selected != null && year.SelectedIndex > 0 ? Ui.Number(cutoff.Text) : 0;
        if (yearNumber != Math.Truncate(yearNumber) || yearNumber is < 0 or > 9999) throw new InvalidOperationException("Informe um ano histórico inteiro válido.");
        var options = new GrowthComparisonOptions(Ui.Number(minimum.Text), Ui.Number(maximum.Text), Ui.Number(reference.Text),
            Ui.Number(relative.Text), Ui.Number(absolute.Text), age.SelectedItem?.ToString() ?? "", observed.SelectedItem?.ToString() ?? "",
            dataUnit.SelectedItem?.ToString() ?? "anos", unitColumns.SelectedItems.Cast<string>().ToArray(),
            year.SelectedIndex > 0 ? year.SelectedItem!.ToString()! : "", (int)yearNumber, independent.IsChecked == true, productionUnit.Text.Trim());
        var definition = new JsonObject { ["kind"] = "growth_comparison", ["path"] = selected?.Path ?? "", ["sheet"] = selected?.Sheet ?? "", ["header_row"] = selected?.Header ?? 1,
            ["baseline"] = JsonSerializer.SerializeToNode(ToCurve(a, oldUnit, curveOptions[baseline.SelectedIndex].Label)),
            ["candidates"] = new JsonArray(selectedCandidates.Select(i => (JsonNode?)JsonSerializer.SerializeToNode(ToCurve(curves[i], newUnit, curveOptions[i].Label))).ToArray()),
            ["options"] = JsonSerializer.SerializeToNode(options) };
        owner.StartTableOperation("Comparando curvas de produção…");
        var report = await AnalysisDocuments.Compute(session, definition);
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log(report.Summary.S("decision"));
    }
}
