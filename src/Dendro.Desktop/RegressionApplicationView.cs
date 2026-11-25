using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class RegressionApplicationView : ToolFormView
{
    private const int CatalogPageSize = 500;
    private sealed record SourceChoice(string Name, string ProjectPath, TableSource Table);

    private sealed class ModelRow(
        SavedRegressionModel model,
        string sourceType = "saved",
        string ageUnit = "",
        double referenceAge = double.NaN,
        string responseColumn = "") : INotifyPropertyChanged
    {
        private bool selected;
        private string x1 = "", x2 = "", x3 = "", y1 = "", targetX1 = "", targetX2 = "", targetX3 = "", stratumColumn = "", stratumValue = "";
        public SavedRegressionModel Model { get; } = model;
        public string SourceType { get; } = sourceType;
        public string AgeUnit { get; } = ageUnit;
        public double ReferenceAge { get; } = referenceAge;
        public string ResponseColumn { get; } = responseColumn;
        public string Name => Model.Name;
        public string Predictors => Model.Predictors + "P";
        public string Formula => Model.Formula;
        public string Coefficients => string.Join("; ", Model.Coefficients.Select((value, index) => $"b{index}={value:G8}"));
        public bool Selected { get => selected; set { selected = value; Changed(); } }
        public string X1 { get => x1; set { x1 = value; Changed(); } }
        public string X2 { get => x2; set { x2 = value; Changed(); } }
        public string X3 { get => x3; set { x3 = value; Changed(); } }
        public string Y1 { get => y1; set { y1 = value; Changed(); } }
        public string TargetX1 { get => targetX1; set { targetX1 = value; Changed(); } }
        public string TargetX2 { get => targetX2; set { targetX2 = value; Changed(); } }
        public string TargetX3 { get => targetX3; set { targetX3 = value; Changed(); } }
        public string StratumColumn { get => stratumColumn; set { stratumColumn = value; Changed(); } }
        public string StratumValue { get => stratumValue; set { stratumValue = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
        public string Predictor(int index) => index switch { 0 => X1, 1 => X2, 2 => X3, _ => "" };
        public string TargetPredictor(int index) => index switch { 0 => TargetX1, 1 => TargetX2, 2 => TargetX3, _ => "" };
        public void SetPredictor(int index, string value)
        {
            switch (index) { case 0: X1 = value; break; case 1: X2 = value; break; case 2: X3 = value; break; }
        }
        public void SetTargetPredictor(int index, string value)
        {
            switch (index) { case 0: TargetX1 = value; break; case 1: TargetX2 = value; break; case 2: TargetX3 = value; break; }
        }
    }

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source = Ui.Combo([]);
    private readonly ComboBox equationSource = Ui.Combo(["Regressões salvas", "Crescimento e produção", "Índice de sítio"]);
    private readonly ComboBox bulkField = Ui.Combo(["X1"], "X1");
    private readonly ComboBox bulkColumn = Ui.Combo([""]);
    private readonly Button applyBulk = Ui.Button("Aplicar às selecionadas", () => { });
    private readonly CheckBox projection = new() { Content = "Projetar para outra ocasião", ToolTip = "Calcula Y₂ = Y₁ × f(X1ᵢ₂, X2ᵢ₂, X3ᵢ₂) / f(X1ᵢ₁, X2ᵢ₁, X3ᵢ₁). Informe os preditores e Y1 da ocasião atual." };
    private readonly TextBox outputName = Ui.Input("Regressoes_aplicadas");
    private readonly DataGrid grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = false,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        MinHeight = 220,
        MaxHeight = 410,
        MinColumnWidth = 75,
        RowHeight = 38,
    };
    private readonly ObservableCollection<ModelRow> models = new();
    private DataGridColumn? y1Column, x1Column, x2Column, x3Column, targetX1Column, targetX2Column, targetX3Column;
    private readonly Button remove = Ui.IconButton("Remover regressão salva", CommandIconKind.Remove, () => { });
    private readonly TextBlock status = Ui.Text("");
    private readonly TextBlock catalogPageCaption = Ui.Text("");
    private readonly Button catalogPrevious;
    private readonly Button catalogNext;
    private DockPanel? catalogToolbar;
    private SourceChoice[] sources = [];
    private bool loading;
    private int catalogPage;

    internal RegressionApplicationView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;

        var form = Ui.Form();
        SetForm(form);
        form.Children.Add(owner.ToolHeader("Aplicar ajustes"));
        Ui.AddHelp(form, "Escolha regressões salvas, equações vigentes de Crescimento e produção ou modelos vigentes de Índice de sítio. Mapeie os preditores para as colunas da base; a tabela original é preservada e as previsões são criadas em uma nova tabela.");
        Ui.Section(form, "Base de aplicação");
        Ui.Field(form, "Origem do modelo", equationSource);
        Ui.Field(form, "Tabela de entrada", source);
        form.Children.Add(projection);
        Ui.AddHelp(form, "Regressões e curvas de crescimento: desmarcado calcula Y previsto = f(X); marcado projeta Y2 = Y1 × f(i2)/f(i1), usando Y1 e X1/X2/X3 nas duas ocasiões. Para Índice de sítio, desmarcado calcula IS = Y observado × f(idade-base)/f(idade atual); marcado projeta Y observado para a idade i2.");
        Ui.Section(form, "Equações disponíveis");
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Aplicar", Binding = new Binding(nameof(ModelRow.Selected)), Width = 52 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Equação · código", Binding = new Binding(nameof(ModelRow.Name)), IsReadOnly = true, Width = 180 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Estrato · coluna", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.StratumColumn)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Estrato · valor", Binding = new Binding(nameof(ModelRow.StratumValue)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 115 });
        y1Column = new DataGridComboBoxColumn { Header = "Y1 · valor atual", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.Y1)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 120 };
        grid.Columns.Add(y1Column);
        x1Column = new DataGridComboBoxColumn { Header = "X1 na base", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.X1)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 120 };
        grid.Columns.Add(x1Column);
        x2Column = new DataGridComboBoxColumn { Header = "X2 na base", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.X2)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 };
        grid.Columns.Add(x2Column);
        x3Column = new DataGridComboBoxColumn { Header = "X3 na base", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.X3)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 };
        grid.Columns.Add(x3Column);
        targetX1Column = new DataGridComboBoxColumn { Header = "X1 · i2", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.TargetX1)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 120 };
        grid.Columns.Add(targetX1Column);
        targetX2Column = new DataGridComboBoxColumn { Header = "X2 · i2", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.TargetX2)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 };
        grid.Columns.Add(targetX2Column);
        targetX3Column = new DataGridComboBoxColumn { Header = "X3 · i2", ItemsSource = Array.Empty<string>(), SelectedItemBinding = new Binding(nameof(ModelRow.TargetX3)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 };
        grid.Columns.Add(targetX3Column);
        grid.Columns.Add(new DataGridTextColumn { Header = "Variáveis", Binding = new Binding(nameof(ModelRow.Predictors)), IsReadOnly = true, Width = 62 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Coeficientes", Binding = new Binding(nameof(ModelRow.Coefficients)), IsReadOnly = true, Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Fórmula", Binding = new Binding(nameof(ModelRow.Formula)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        projection.Checked += (_, _) => UpdateGridMode();
        projection.Unchecked += (_, _) => UpdateGridMode();
        UpdateGridMode();
        var catalogActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        catalogActions.Children.Add(Ui.IconButton("Atualizar catálogo", CommandIconKind.Restore, RefreshModels));
        catalogActions.Children.Add(remove);
        catalogPrevious = CatalogPageButton("Página anterior", CommandIconKind.ArrowLeft, () => ShowCatalogPage(catalogPage - 1));
        catalogNext = CatalogPageButton("Próxima página", CommandIconKind.ArrowRight, () => ShowCatalogPage(catalogPage + 1));
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(catalogPrevious);
        catalogPageCaption.Margin = new Thickness(6, 3, 6, 3);
        catalogPageCaption.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(catalogPageCaption);
        navigation.Children.Add(catalogNext);
        var catalogToolbar = this.catalogToolbar = new DockPanel { Margin = new Thickness(4, 1, 4, 3), LastChildFill = true };
        DockPanel.SetDock(navigation, Dock.Right);
        catalogToolbar.Children.Add(navigation);
        catalogToolbar.Children.Add(catalogActions);
        form.Children.Add(catalogToolbar);
        Ui.Section(form, "Preenchimento em lote");
        var bulkRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        bulkField.Width = 155;
        bulkField.Margin = new Thickness(2, 2, 6, 2);
        bulkColumn.Width = 220;
        bulkColumn.Margin = new Thickness(2, 2, 6, 2);
        applyBulk.Margin = new Thickness(2);
        bulkRow.Children.Add(Ui.Text("Campo", 12));
        bulkRow.Children.Add(bulkField);
        bulkRow.Children.Add(Ui.Text("Coluna", 12));
        bulkRow.Children.Add(bulkColumn);
        bulkRow.Children.Add(applyBulk);
        form.Children.Add(bulkRow);
        Ui.AddHelp(form, "Selecione várias equações na tabela com Ctrl ou Shift. Escolha um campo e uma coluna e aplique o mesmo mapeamento às linhas selecionadas que usam esse campo.");
        grid.ItemsSource = models.Take(CatalogPageSize).ToArray();
        form.Children.Add(grid);
        Ui.AddHelp(form, "Confira a fórmula e mapeie cada variável para a coluna equivalente. Em Estrato · coluna/valor, escolha a coluna e informe um valor numérico ou textual para limitar aquela equação; deixe a coluna vazia para aplicar à base toda. As demais linhas permanecem na tabela com previsão vazia.");
        Ui.Section(form, "Resultado");
        Ui.Field(form, "Nome da nova tabela", outputName);
        SetPrimaryAction(Ui.AsyncButton("Aplicar ajustes e criar tabela", () => owner.Execute(Apply)));
        form.Children.Add(status);
        source.SelectionChanged += async (_, _) => { if (!loading) await owner.Execute(LoadColumns); };
        equationSource.SelectionChanged += async (_, _) =>
        {
            RefreshModels();
            if (source.SelectedIndex >= 0) await owner.Execute(LoadColumns);
        };
        grid.SelectionChanged += (_, _) => remove.IsEnabled = grid.SelectedItems.Count > 0;
        applyBulk.Click += (_, _) => ApplyBulkMapping();
        remove.Click += (_, _) => { _ = owner.Execute(RemoveSelectedModels); };
        RefreshModels();
    }

    private void UpdateGridMode()
    {
        bool project = projection.IsChecked == true;
        bool siteIndex = equationSource.SelectedIndex == 2;
        if (y1Column != null)
        {
            y1Column.Visibility = Visibility.Visible;
            y1Column.Header = siteIndex
                ? project ? "Y1 · DAPd/Hd" : "Y · DAPd/Hd"
                : project ? "Y1 · observado" : "Y · observado";
        }
        if (x1Column != null) x1Column.Header = siteIndex
            ? project ? "Idade · i1" : "Idade atual"
            : project ? "X1 · i1" : "X1 na base";
        if (x2Column != null) x2Column.Header = project ? "X2 · i1" : "X2 na base";
        if (x3Column != null) x3Column.Header = project ? "X3 · i1" : "X3 na base";
        foreach (var column in new[] { targetX1Column, targetX2Column, targetX3Column })
            if (column != null) column.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        int predictorCount = models.Count == 0 ? 1 : models.Max(model => model.Model.Predictors);
        if (x2Column != null) x2Column.Visibility = predictorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (x3Column != null) x3Column.Visibility = predictorCount > 2 ? Visibility.Visible : Visibility.Collapsed;
        if (targetX2Column != null) targetX2Column.Visibility = project && predictorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (targetX3Column != null) targetX3Column.Visibility = project && predictorCount > 2 ? Visibility.Visible : Visibility.Collapsed;
        string selectedBulkField = bulkField.SelectedItem?.ToString() ?? "X1";
        var bulkFields = project
            ? new[] { "X1", "X2", "X3", "Y1", "X1 · i2", "X2 · i2", "X3 · i2" }
            : new[] { "X1", "X2", "X3", "Y1" };
        bulkField.ItemsSource = bulkFields;
        bulkField.SelectedItem = bulkFields.Contains(selectedBulkField, StringComparer.Ordinal) ? selectedBulkField : "X1";
        grid.Items.Refresh();
    }

    internal async Task Initialize(string? initialPath = null, string? initialTable = null)
    {
        loading = true;
        try
        {
            sources = await Task.Run(BuildSources);
            source.ItemsSource = sources.Select(s => s.Name).ToArray();
            int index = initialPath == null ? -1 : Array.FindIndex(sources, s =>
                string.Equals(s.ProjectPath, initialPath, StringComparison.OrdinalIgnoreCase)
                && (initialTable == null || s.Table.Table == initialTable));
            if (index < 0 && initialPath != null)
                index = Array.FindIndex(sources, s => string.Equals(s.ProjectPath, initialPath, StringComparison.OrdinalIgnoreCase));
            source.SelectedIndex = index >= 0 ? index : sources.Length > 0 ? 0 : -1;
        }
        finally { loading = false; }
        if (source.SelectedIndex >= 0) await LoadColumns();
        else status.Text = "Adicione uma base tabular ao projeto para começar.";
    }

    private SourceChoice[] BuildSources()
    {
        var result = new List<SourceChoice>();
        var catalogs = new ProjectTableCatalogCache();
        foreach (string path in session.Files.Where(session.IsTabular))
        {
            string resolved = session.Resolve(path);
            string[] tables;
            try { tables = catalogs.Tables(session, path); }
            catch (IOException) { continue; }
            catch (InvalidDataException) { continue; }
            foreach (string table in tables)
            {
                var options = session.ExcelOptions(path);
                var tableSource = new TableSource(resolved, table,
                    session.IsSpreadsheet(path) ? (int)options.N("header_row", 1) : 1);
                result.Add(new(session.ContentName(path) + (tables.Length > 1 ? " · " + table : ""), path, tableSource));
            }
        }
        return result.ToArray();
    }

    private void RefreshModels()
    {
        if (equationSource.SelectedIndex is 1 or 2)
        {
            RefreshCatalogModels(equationSource.SelectedIndex == 1 ? "growth" : "site");
            return;
        }
        if (catalogToolbar != null) catalogToolbar.Visibility = Visibility.Visible;
        remove.Visibility = Visibility.Visible;
        var catalog = session.Meta.Arr(RegressionApplication.StorageKey);
        RegressionApplication.ValidateCatalog(catalog);
        var selectedIds = models.Where(model => model.Selected)
            .Select(model => model.Model.Id)
            .ToHashSet(StringComparer.Ordinal);
        models.Clear();
        foreach (var node in catalog.OfType<JsonObject>().Reverse())
        {
            var row = new ModelRow(RegressionApplication.Read(node)) { Selected = selectedIds.Contains(node.S("id")) };
            models.Add(row);
        }
        if (models.Count > 0 && !models.Any(m => m.Selected)) models[0].Selected = true;
        if (catalog.Count == 0)
            status.Text = "Nenhuma regressão foi salva ainda. No resultado do Ajuste de regressão, clique com o botão direito em um ajuste e escolha Salvar regressão para aplicação.";
        else
            status.Text = $"Catálogo atualizado: {catalog.Count:N0} regressão(ões) salva(s) no projeto.";
        catalogPage = Math.Clamp(catalogPage, 0, Math.Max(0, (models.Count - 1) / CatalogPageSize));
        ShowCatalogPage(catalogPage);
        UpdateGridMode();
    }

    private void RefreshCatalogModels(string sourceType)
    {
        if (catalogToolbar != null) catalogToolbar.Visibility = Visibility.Collapsed;
        remove.Visibility = Visibility.Collapsed;
        var selectedIds = models.Where(model => model.Selected)
            .Select(model => model.Model.Id).ToHashSet(StringComparer.Ordinal);
        models.Clear();
        var catalog = sourceType == "growth"
            ? session.Data!.Obj("curvas_crescimento").Arr("equacoes")
            : session.Meta.Arr("site_index_models");
        int skipped = 0;
        foreach (var group in catalog.OfType<JsonObject>()
            .Where(row => row.S("codigo").Length > 0)
            .GroupBy(row => row.S("codigo"), StringComparer.OrdinalIgnoreCase))
        {
            var equation = group.OrderByDescending(row => row.B("vigente", true))
                .ThenByDescending(row => row.N("ano")).First();
            try
            {
                var catalogModel = CreateCatalogModel(equation, sourceType);
                var row = new ModelRow(catalogModel.Model, sourceType, catalogModel.AgeUnit,
                    catalogModel.ReferenceAge, catalogModel.ResponseColumn)
                {
                    Selected = selectedIds.Contains(catalogModel.Model.Id),
                };
                models.Add(row);
            }
            catch (InvalidDataException)
            {
                skipped++;
            }
        }
        if (models.Count > 0 && !models.Any(model => model.Selected)) models[0].Selected = true;
        catalogPage = 0;
        ShowCatalogPage(catalogPage);
        UpdateGridMode();
        string label = sourceType == "growth" ? "Crescimento e produção" : "Índice de sítio";
        status.Text = models.Count == 0
            ? $"Nenhuma equação aplicável encontrada em {label}. Confira o catálogo e os símbolos das fórmulas."
            : $"{models.Count:N0} equação(ões) vigente(s) de {label} por código." + (skipped > 0 ? $" {skipped:N0} equação(ões) incompatível(is) foram ignoradas." : "");
    }

    private sealed record CatalogModel(SavedRegressionModel Model, string AgeUnit, double ReferenceAge, string ResponseColumn);

    private static CatalogModel CreateCatalogModel(JsonObject equation, string sourceType)
    {
        bool growth = sourceType == "growth";
        string code = equation.S("codigo");
        string formulaText = growth ? ModelDefinition.FormulaText(equation, true) : equation.S("formula");
        JsonArray inputs = growth ? ModelDefinition.Inputs(equation, true) : equation["entradas"] as JsonArray ?? [];
        var inputRows = inputs.OfType<JsonObject>().ToArray();
        string[] inputSymbols = inputRows.Select(input => input.S("simbolo")).Where(symbol => symbol.Length > 0).ToArray();
        Formula formula = new(formulaText, ModelDefinition.Coefficients.Concat(inputSymbols).Concat(["x", "x1", "x2", "x3", "t"]));
        var dependencies = formula.Dependencies;
        string[] variableSymbols = dependencies.Where(symbol => !ModelDefinition.IsCoefficient(symbol)).ToArray();
        var ageInput = inputRows.FirstOrDefault(input => input.S("coluna").StartsWith("idade", StringComparison.OrdinalIgnoreCase)
            || input.S("coluna").StartsWith("age", StringComparison.OrdinalIgnoreCase));
        string[] ageAliases = ["t", "x", "x1"];
        string declaredAgeSymbol = ageInput?.S("simbolo") ?? "";
        string ageSymbol = variableSymbols.Contains(declaredAgeSymbol, StringComparer.Ordinal)
            ? declaredAgeSymbol
            : variableSymbols.FirstOrDefault(symbol => ageAliases.Contains(symbol, StringComparer.Ordinal)) ?? "";
        if (ageSymbol.Length == 0 || !variableSymbols.Contains(ageSymbol, StringComparer.Ordinal))
            throw new InvalidDataException("A fórmula não contém uma entrada de idade reconhecível.");

        string[] extraSymbols = inputSymbols.Where(symbol => variableSymbols.Contains(symbol, StringComparer.Ordinal)
                && symbol != ageSymbol && !ageAliases.Contains(symbol, StringComparer.Ordinal))
            .Concat(variableSymbols.Where(symbol => symbol != ageSymbol && !ageAliases.Contains(symbol, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (extraSymbols.Length > 2)
            throw new InvalidDataException("A aplicação aceita até três preditores, incluindo a idade.");
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string alias in variableSymbols.Where(symbol => ageAliases.Contains(symbol, StringComparer.Ordinal)))
            replacements[alias] = "x1";
        replacements[ageSymbol] = "x1";
        for (int index = 0; index < extraSymbols.Length; index++)
            replacements[extraSymbols[index]] = "x" + (index + 2);
        string normalizedFormula = formulaText;
        foreach (var replacement in replacements.OrderByDescending(pair => pair.Key.Length))
            normalizedFormula = Regex.Replace(normalizedFormula,
                $"(?<![A-Za-z0-9_]){Regex.Escape(replacement.Key)}(?![A-Za-z0-9_])",
                replacement.Value);

        int coefficientCount = dependencies.Where(ModelDefinition.IsCoefficient)
            .Select(symbol => int.Parse(symbol.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) + 1)
            .DefaultIfEmpty(0).Max();
        if (equation["coefficients"] is JsonArray savedCoefficients)
            coefficientCount = Math.Max(coefficientCount, savedCoefficients.Count);
        if (coefficientCount is < 1 or > 4)
            throw new InvalidDataException("A equação precisa usar de um a quatro coeficientes para esta ferramenta.");
        var coefficients = equation["coefficients"] is JsonArray values
            ? values.Select(value => JsonExtensions.Number(value)).ToArray()
            : Enumerable.Range(0, coefficientCount).Select(index => equation["b" + index] == null
                ? double.NaN : equation.N("b" + index)).ToArray();
        if (coefficients.Length != coefficientCount || coefficients.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Há coeficientes ausentes ou inválidos.");

        string[] sourcePredictors = [ageInput?.S("coluna") ?? ""];
        sourcePredictors = [.. sourcePredictors, .. extraSymbols.Select(symbol => inputRows.FirstOrDefault(input => input.S("simbolo") == symbol)?.S("coluna") ?? symbol)];
        string ageUnit = growth ? equation.S("unidade_idade", ageInput?.S("unidade", "meses") ?? "meses")
            : equation.S("age_unit", ageInput?.S("unidade", "anos") ?? "anos");
        if (ageUnit is not ("anos" or "meses")) ageUnit = "meses";
        double referenceAge = growth ? double.NaN : equation.N("reference_age", double.NaN);
        string response = growth ? "" : equation.S("source_y");
        if (!growth && (!double.IsFinite(referenceAge) || referenceAge <= 0 || response is not ("DAPd" or "Hd")))
            throw new InvalidDataException("O modelo de sítio não contém idade-base ou resposta DAPd/Hd válidas.");

        string name = $"{(growth ? "" : "IS · ")}{code} · {equation.S("modelo_nome", equation.S("nome", equation.S("modelo")))} · {equation.N("ano"):0}";
        if (name.Length > 120) name = name[..120];
        var model = new SavedRegressionModel(
            $"{sourceType}:{code}:{equation.N("ano"):0}", name, 1 + extraSymbols.Length,
            normalizedFormula, coefficients, sourcePredictors);
        RegressionApplication.Validate(model);
        return new(model, ageUnit, referenceAge, response);
    }

    private static Button CatalogPageButton(string tooltip, CommandIconKind icon, Action action)
    {
        var button = Ui.IconButton(tooltip, icon, action);
        button.Width = button.MinWidth = 32;
        button.Height = button.MinHeight = 30;
        button.Padding = new Thickness(5);
        button.Margin = new Thickness(1);
        return button;
    }

    private void ShowCatalogPage(int index)
    {
        int pages = Math.Max(1, (models.Count + CatalogPageSize - 1) / CatalogPageSize);
        catalogPage = Math.Clamp(index, 0, pages - 1);
        grid.ItemsSource = models.Skip(catalogPage * CatalogPageSize).Take(CatalogPageSize).ToArray();
        int first = models.Count == 0 ? 0 : catalogPage * CatalogPageSize + 1;
        int last = Math.Min((catalogPage + 1) * CatalogPageSize, models.Count);
        catalogPageCaption.Text = $"{first:N0}–{last:N0} de {models.Count:N0}";
        catalogPrevious.IsEnabled = catalogPage > 0;
        catalogNext.IsEnabled = (catalogPage + 1) * CatalogPageSize < models.Count;
        remove.IsEnabled = grid.SelectedItems.Count > 0;
    }

    private async Task LoadColumns()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length) return;
        var selected = sources[source.SelectedIndex];
        var info = await Task.Run(() =>
        {
            using var cursor = new TableCursor(selected.Table);
            var sample = cursor.Rows().Take(100).ToArray();
            var numeric = Enumerable.Range(0, cursor.Columns.Length)
                .Where(column => sample.Any(row => TryNumber(row[column], out _)))
                .Select(column => cursor.Columns[column]).ToArray();
            return (cursor.Columns, Numeric: numeric.Length > 0 ? numeric : cursor.Columns);
        });
        string[] columnChoices = ["", .. info.Columns];
        bulkColumn.ItemsSource = columnChoices;
        foreach (var column in grid.Columns.OfType<DataGridComboBoxColumn>()) column.ItemsSource = columnChoices;
        string? ageColumn = info.Columns.FirstOrDefault(column => column.Contains("idade", StringComparison.OrdinalIgnoreCase)
            || column.Contains("age", StringComparison.OrdinalIgnoreCase));
        foreach (var row in models)
            for (int predictor = 0; predictor < row.Model.Predictors; predictor++)
            {
                string current = row.Predictor(predictor);
                string saved = row.Model.SourcePredictors.ElementAtOrDefault(predictor) ?? "";
                string chosen = info.Columns.Contains(current, StringComparer.Ordinal) ? current
                    : info.Columns.Contains(saved, StringComparer.Ordinal) ? saved
                    : predictor == 0 && row.SourceType != "saved" ? ageColumn ?? info.Numeric.FirstOrDefault() ?? ""
                    : info.Numeric.ElementAtOrDefault(predictor) ?? info.Columns.FirstOrDefault() ?? "";
                row.SetPredictor(predictor, chosen);
            }
        foreach (var row in models)
        {
            if (row.SourceType == "site" && info.Columns.Contains(row.ResponseColumn, StringComparer.Ordinal))
                row.Y1 = row.ResponseColumn;
            else if (!info.Columns.Contains(row.Y1, StringComparer.Ordinal)) row.Y1 = "";
            if (!info.Columns.Contains(row.StratumColumn, StringComparer.Ordinal)) row.StratumColumn = "";
            for (int predictor = 0; predictor < row.Model.Predictors; predictor++)
                if (!info.Columns.Contains(row.TargetPredictor(predictor), StringComparer.Ordinal))
                    row.SetTargetPredictor(predictor, "");
        }
        UpdateGridMode();
        grid.Items.Refresh();
        string modelKind = equationSource.SelectedIndex switch
        {
            1 => "equações de Crescimento e produção",
            2 => "modelos de Índice de sítio",
            _ => "regressões salvas",
        };
        status.Text = models.Count == 0 ? $"Nenhuma {modelKind} aplicável no projeto."
            : $"Mapeie os preditores e marque uma ou mais {modelKind} para aplicar.";
    }

    private async Task Apply()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length)
            throw new InvalidOperationException("Selecione a base em que as equações serão aplicadas.");
        var chosen = models.Where(row => row.Selected).ToArray();
        if (chosen.Length == 0)
            throw new InvalidOperationException("Marque pelo menos uma equação para aplicar.");
        bool useProjection = projection.IsChecked == true;
        bool calculateSiteIndex = equationSource.SelectedIndex == 2 && !useProjection;
        var specifications = chosen.Select(row => new RegressionApplicationSpec(
            row.Model,
            Enumerable.Range(0, row.Model.Predictors).Select(row.Predictor).ToArray(),
            row.Y1,
            Enumerable.Range(0, row.Model.Predictors).Select(row.TargetPredictor).ToArray(),
            Enumerable.Range(0, row.Model.Predictors).Select(index => index == 0 ? AgeFactor(row.Predictor(index), row.AgeUnit) : 1d).ToArray(),
            Enumerable.Range(0, row.Model.Predictors).Select(index => index == 0 ? AgeFactor(row.TargetPredictor(index), row.AgeUnit) : 1d).ToArray(),
            row.ReferenceAge,
            calculateSiteIndex && row.SourceType == "site",
            row.StratumColumn,
            row.StratumValue
        )).ToArray();
        if (specifications.Any(s => s.PredictorColumns.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("Escolha as colunas de entrada para X1, X2 e X3 das equações selecionadas.");
        if (useProjection && specifications.Any(s => string.IsNullOrWhiteSpace(s.InitialValueColumn) || s.TargetPredictorColumns == null || s.TargetPredictorColumns.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("Na forma de projeção, escolha Y1 e os preditores de i2 para cada equação selecionada.");
        if (calculateSiteIndex && chosen.Any(row => row.SourceType == "site" && string.IsNullOrWhiteSpace(row.Y1)))
            throw new InvalidOperationException("Para calcular IS, selecione a coluna observada indicada pela equação (DAPd ou Hd).");
        string tableName = TableTools.SuggestedName(outputName.Text);
        var selectedSource = sources[source.SelectedIndex];
        var cancellation = owner.OperationCancellation;
        var progress = owner.StartTableOperation("Aplicando equações à base…");
        var result = await Task.Run(() => RegressionApplication.Apply(
            selectedSource.Table, specifications, session.ProjectWorkFolder(), tableName, cancellation, progress,
            useProjection));
        try
        {
            await session.AddTableResults([result.Table], selectedSource.ProjectPath, selectedSource.Table.Table);
        }
        finally
        {
            if (File.Exists(result.Table.Path)) File.Delete(result.Table.Path);
        }
        string path = Path.Combine(session.Meta.S("database_path"), Path.GetFileName(result.Table.Path));
        session.RenameContent(path, result.Table.Table);
        await owner.OpenTable(path, result.Table.Table);
        status.Text = $"{result.Rows:N0} linhas mantidas · {specifications.Length:N0} equação(ões) aplicada(s) · {result.InvalidPredictions:N0} predições vazias por dados ausentes ou inválidos · {result.FilteredPredictions:N0} células vazias fora do estrato selecionado.";
        owner.Log(status.Text);
    }

    private void ApplyBulkMapping()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = grid.SelectedItems.Cast<ModelRow>().ToArray();
        if (selected.Length == 0)
        {
            status.Text = "Selecione uma ou mais equações na tabela para preencher em lote.";
            return;
        }
        string field = bulkField.SelectedItem?.ToString() ?? "X1";
        string column = bulkColumn.SelectedItem?.ToString() ?? "";
        int applied = 0, skipped = 0;
        foreach (var row in selected)
        {
            bool canApply = true;
            switch (field)
            {
                case "Y1": row.Y1 = column; break;
                case "X1": row.SetPredictor(0, column); break;
                case "X2": canApply = row.Model.Predictors > 1; if (canApply) row.SetPredictor(1, column); break;
                case "X3": canApply = row.Model.Predictors > 2; if (canApply) row.SetPredictor(2, column); break;
                case "X1 · i2": canApply = projection.IsChecked == true && row.Model.Predictors > 0; if (canApply) row.SetTargetPredictor(0, column); break;
                case "X2 · i2": canApply = projection.IsChecked == true && row.Model.Predictors > 1; if (canApply) row.SetTargetPredictor(1, column); break;
                case "X3 · i2": canApply = projection.IsChecked == true && row.Model.Predictors > 2; if (canApply) row.SetTargetPredictor(2, column); break;
                default: canApply = false; break;
            }
            if (canApply) applied++; else skipped++;
        }
        grid.Items.Refresh();
        status.Text = $"Mapeamento de {field} aplicado a {applied:N0} linha(s) selecionada(s)."
            + (skipped > 0 ? $" {skipped:N0} ignorada(s) porque não usam esse campo nesse modo." : "");
    }

    private static double AgeFactor(string column, string modelUnit)
    {
        bool months = column.Contains("mes", StringComparison.OrdinalIgnoreCase);
        bool years = column.Contains("ano", StringComparison.OrdinalIgnoreCase);
        if (months && modelUnit == "anos") return 1d / 12;
        if (years && modelUnit == "meses") return 12;
        return 1;
    }

    private async Task RemoveSelectedModels()
    {
        var selected = grid.SelectedItems.Cast<ModelRow>().ToArray();
        if (selected.Length == 0) return;
        if (!Ui.Confirm("Remover regressões salvas", $"Remover {selected.Length} regressão(ões) selecionada(s) do catálogo deste projeto?", "Remover", true)) return;
        var catalog = session.Meta.Arr(RegressionApplication.StorageKey);
        var ids = selected.Select(row => row.Model.Id).ToHashSet(StringComparer.Ordinal);
        int removed = 0;
        for (int index = catalog.Count - 1; index >= 0; index--)
        {
            if (catalog[index] is JsonObject node && ids.Contains(node.S("id")))
            {
                catalog.RemoveAt(index);
                removed++;
            }
        }
        if (removed > 0) session.Touch();
        RefreshModels();
        if (source.SelectedIndex >= 0) await LoadColumns();
        status.Text = removed == 0
            ? "As regressões selecionadas não foram encontradas no catálogo atual. Atualize a lista."
            : $"{removed:N0} regressão(ões) removida(s) do catálogo em memória. Salve o projeto para persistir a alteração.";
    }

    private static bool TryNumber(object? value, out double number)
    {
        if (value is double numberValue && double.IsFinite(numberValue)) { number = numberValue; return true; }
        if (value == null) { number = double.NaN; return false; }
        string text = TableCursor.Text(value).Trim();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
            || double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), out number) && double.IsFinite(number);
    }
}
