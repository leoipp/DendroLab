using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class RegressionApplicationView : ScrollViewer
{
    private const int CatalogPageSize = 500;
    private sealed record SourceChoice(string Name, string ProjectPath, TableSource Table);

    private sealed class ModelRow(SavedRegressionModel model) : INotifyPropertyChanged
    {
        private bool selected;
        private string x1 = "", x2 = "", x3 = "", y1 = "", targetX1 = "", targetX2 = "", targetX3 = "";
        public SavedRegressionModel Model { get; } = model;
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
    private readonly CheckBox projection = new() { Content = "Forma de projeção", ToolTip = "Calcula Y₂ = Y₁ × f(X1ᵢ₂, X2ᵢ₂, X3ᵢ₂) / f(X1ᵢ₁, X2ᵢ₁, X3ᵢ₁). Informe os preditores de cada ocasião." };
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
    private SourceChoice[] sources = [];
    private bool loading;
    private int catalogPage;

    internal RegressionApplicationView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var form = Ui.Form();
        Content = form;
        form.Children.Add(owner.ToolHeader("Aplicar regressões"));
        Ui.AddHelp(form, "Aplique equações ajustadas e salvas no projeto a uma base. Cada regressão pode usar colunas X1, X2 e X3 próprias. A tabela original é mantida; o resultado é salvo como uma nova tabela no projeto.");
        Ui.Section(form, "Base de aplicação");
        Ui.Field(form, "Tabela de entrada", source);
        form.Children.Add(projection);
        Ui.AddHelp(form, "Desmarcado: calcula Y previsto = f(X); Y observado é uma referência opcional. Marcado: projeta Y2 = Y1 × f(i2)/f(i1), usando Y como Y1 obrigatório. Mapeie todos os preditores nas ocasiões i1 e i2.");
        Ui.Section(form, "Regressões salvas no projeto");
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Aplicar", Binding = new Binding(nameof(ModelRow.Selected)), Width = 52 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Regressão", Binding = new Binding(nameof(ModelRow.Name)), IsReadOnly = true, Width = 110 });
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
        var catalogToolbar = new DockPanel { Margin = new Thickness(4, 1, 4, 3), LastChildFill = true };
        DockPanel.SetDock(navigation, Dock.Right);
        catalogToolbar.Children.Add(navigation);
        catalogToolbar.Children.Add(catalogActions);
        form.Children.Add(catalogToolbar);
        grid.ItemsSource = models.Take(CatalogPageSize).ToArray();
        form.Children.Add(grid);
        Ui.AddHelp(form, "Confira a fórmula e mapeie cada variável para a coluna equivalente. Colunas ausentes ou valores não numéricos geram resultado vazio apenas naquela linha.");
        Ui.Section(form, "Resultado");
        Ui.Field(form, "Nome da nova tabela", outputName);
        form.Children.Add(Ui.AsyncButton("Aplicar regressões e criar tabela", () => owner.Execute(Apply)));
        form.Children.Add(status);
        source.SelectionChanged += async (_, _) => { if (!loading) await owner.Execute(LoadColumns); };
        grid.SelectionChanged += (_, _) => remove.IsEnabled = grid.SelectedItems.Count > 0;
        remove.Click += (_, _) => { _ = owner.Execute(RemoveSelectedModels); };
        RefreshModels();
    }

    private void UpdateGridMode()
    {
        bool project = projection.IsChecked == true;
        if (y1Column != null)
        {
            y1Column.Visibility = Visibility.Visible;
            y1Column.Header = project ? "Y1 · observado" : "Y · observado";
        }
        if (x1Column != null) x1Column.Header = project ? "X1 · i1" : "X1 na base";
        if (x2Column != null) x2Column.Header = project ? "X2 · i1" : "X2 na base";
        if (x3Column != null) x3Column.Header = project ? "X3 · i1" : "X3 na base";
        foreach (var column in new[] { targetX1Column, targetX2Column, targetX3Column })
            if (column != null) column.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        int predictorCount = models.Count == 0 ? 1 : models.Max(model => model.Model.Predictors);
        if (x2Column != null) x2Column.Visibility = predictorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (x3Column != null) x3Column.Visibility = predictorCount > 2 ? Visibility.Visible : Visibility.Collapsed;
        if (targetX2Column != null) targetX2Column.Visibility = project && predictorCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (targetX3Column != null) targetX3Column.Visibility = project && predictorCount > 2 ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (var column in grid.Columns.OfType<DataGridComboBoxColumn>()) column.ItemsSource = columnChoices;
        foreach (var row in models)
            for (int predictor = 0; predictor < row.Model.Predictors; predictor++)
            {
                string current = row.Predictor(predictor);
                string saved = row.Model.SourcePredictors.ElementAtOrDefault(predictor) ?? "";
                string chosen = info.Columns.Contains(current, StringComparer.Ordinal) ? current
                    : info.Columns.Contains(saved, StringComparer.Ordinal) ? saved
                    : info.Numeric.ElementAtOrDefault(predictor) ?? info.Columns.FirstOrDefault() ?? "";
                row.SetPredictor(predictor, chosen);
            }
        foreach (var row in models)
        {
            if (!info.Columns.Contains(row.Y1, StringComparer.Ordinal)) row.Y1 = "";
            for (int predictor = 0; predictor < row.Model.Predictors; predictor++)
                if (!info.Columns.Contains(row.TargetPredictor(predictor), StringComparer.Ordinal))
                    row.SetTargetPredictor(predictor, "");
        }
        UpdateGridMode();
        grid.Items.Refresh();
        status.Text = models.Count == 0 ? "Nenhuma regressão salva no projeto." : "Mapeie os preditores e marque uma ou mais regressões para aplicar.";
    }

    private async Task Apply()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length)
            throw new InvalidOperationException("Selecione a base em que as regressões serão aplicadas.");
        var chosen = models.Where(row => row.Selected).ToArray();
        if (chosen.Length == 0)
            throw new InvalidOperationException("Marque pelo menos uma regressão para aplicar.");
        bool useProjection = projection.IsChecked == true;
        var specifications = chosen.Select(row => new RegressionApplicationSpec(
            row.Model,
            Enumerable.Range(0, row.Model.Predictors).Select(row.Predictor).ToArray(),
            row.Y1,
            Enumerable.Range(0, row.Model.Predictors).Select(row.TargetPredictor).ToArray()
        )).ToArray();
        if (specifications.Any(s => s.PredictorColumns.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("Escolha as colunas de entrada para X1, X2 e X3 das regressões selecionadas.");
        if (useProjection && specifications.Any(s => string.IsNullOrWhiteSpace(s.InitialValueColumn) || s.TargetPredictorColumns == null || s.TargetPredictorColumns.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("Na forma de projeção, escolha a coluna Y1 e os preditores de i2 para cada regressão selecionada.");
        string tableName = TableTools.SuggestedName(outputName.Text);
        var selectedSource = sources[source.SelectedIndex];
        var cancellation = owner.OperationCancellation;
        var progress = owner.StartTableOperation("Aplicando regressões à base…");
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
        status.Text = $"{result.Rows:N0} linhas mantidas · {specifications.Length:N0} regressão(ões) aplicada(s) · {result.InvalidPredictions:N0} predições vazias por dados ausentes ou inválidos.";
        owner.Log(status.Text);
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
