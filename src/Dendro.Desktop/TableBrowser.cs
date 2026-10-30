using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

/// <summary>Bounded table preview; database pages use the row index.</summary>
internal sealed class TableBrowser : DockPanel, IDisposable
{
    private sealed record Clause(TableFilter Filter, string Label);
    private static readonly (string Label, FilterOperator Operator)[] Operators = [
        ("Igual a", FilterOperator.Equal), ("Diferente de", FilterOperator.NotEqual), ("Contém", FilterOperator.Contains),
        ("Começa com", FilterOperator.StartsWith), ("Maior que", FilterOperator.Greater), ("Maior ou igual a", FilterOperator.GreaterOrEqual),
        ("Menor que", FilterOperator.Less), ("Menor ou igual a", FilterOperator.LessOrEqual), ("Está vazio", FilterOperator.Empty), ("Não está vazio", FilterOperator.NotEmpty)];
    private readonly ComboBox filterColumn = Ui.Combo([]), filterOperator = Ui.Combo(Operators.Select(o => o.Label));
    private readonly TextBox filterValue = Ui.Input();
    private readonly Expander filters = new() { Header = "Filtros de colunas", Margin = new Thickness(8) };
    private readonly ObservableCollection<Clause> clauses = new();
    private readonly ListBox activeFilters = new() { MaxHeight = 100, DisplayMemberPath = "Label" };
    private readonly Button export;
    private FilteredTable? filtered;
    private TableSort? sort;
    internal event Action<long[]>? RowsSelected;
    private bool syncingSelection;
    private long[] linkedSelection=[];
    internal IEnumerable<long> ViewSourceRows()=>filtered?.SourceRowNumbers()??Enumerable.Range(0,checked((int)Total)).Select(i=>(long)source.HeaderRow+i+1);
    internal void SelectSourceRows(IEnumerable<long> rows)
    {
        linkedSelection=rows.ToArray();if(grid.Content is not DataGrid table||displayedPage==null)return;
        var ids=linkedSelection.ToHashSet();syncingSelection=true;
        try {table.SelectedItems.Clear();foreach(DataRowView row in table.Items){int index=row.Row.Table.Rows.IndexOf(row.Row);long id=displayedPage.SourceRows.Length>index?displayedPage.SourceRows[index]:source.HeaderRow+Offset+index+1;if(ids.Contains(id))table.SelectedItems.Add(row);}}
        finally {syncingSelection=false;}
    }
    private CompactTable? version;
    private FilteredPage? displayedPage;
    private readonly Button deleteSelected, deleteFiltered;
    internal Func<long, Task<bool>>? DeleteConfirmation { get; set; }
    internal Func<string[], Task<string[]?>>? ColumnRemovalSelection { get; set; }
    private bool CanEdit => !readOnly && Path.GetExtension(source.Path).Equals(".dlb", StringComparison.OrdinalIgnoreCase) && source.HeaderRow == 1
        && !owner.Session.IsHistoricalTable(exportReference ?? owner.Session.ProjectReference(source.Path));
    private readonly MainWindow owner;
    private readonly TableSource source;
    private readonly string? exportReference;
    private readonly bool readOnly;
    private Action? releaseSource;
    private readonly ContentControl grid = new();
    private readonly TextBlock caption = Ui.Text("");
    private readonly Button previous,
        next;
    internal long Offset { get; private set; }
    internal long Total { get; private set; }

    public TableBrowser(MainWindow owner, TableSource source, string? exportReference = null, bool readOnly = false, Action? releaseSource = null, bool selectionOnly = false)
    {
        this.owner = owner;
        this.source = source;
        this.exportReference = exportReference;
        this.readOnly = readOnly; this.releaseSource = releaseSource;
        Background = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(filterColumn, "TableFilter.Column");
        System.Windows.Automation.AutomationProperties.SetAutomationId(filterOperator, "TableFilter.Operator");
        System.Windows.Automation.AutomationProperties.SetAutomationId(filterValue, "TableFilter.Value");
        var filterPanel = new StackPanel();
        var fields = new WrapPanel();
        void Field(string label, FrameworkElement control, double width)
        {
            var cell = new StackPanel { Width = width, Margin = new Thickness(0, 0, 10, 4) };
            Ui.Field(cell, label, control); fields.Children.Add(cell);
        }
        Field("Coluna", filterColumn, 220); Field("Condição", filterOperator, 180); Field("Valor", filterValue, 200);
        filterPanel.Children.Add(fields);
        Ui.AddHelp(filterPanel, "Clique na seta do cabeçalho para filtrar. Clique no nome para ordenar, ou use o botão direito para escolher A → Z, Z → A, menor → maior ou maior → menor. Opera em toda a tabela, inclusive outras páginas, e respeita os filtros. Vazios ficam no fim. Filtros de várias colunas são combinados por E. Os números à esquerda continuam indicando a linha original. A exportação usa o resultado filtrado e ordenado; a origem permanece intacta.");
        var actions = new WrapPanel();
        actions.Children.Add(Ui.AsyncButton("Aplicar filtro", () => owner.Execute(AddFilter)));
        actions.Children.Add(Ui.AsyncButton("Remover filtro selecionado", () => owner.Execute(() => ApplyFilters(clauses.Where(c => !ReferenceEquals(c, activeFilters.SelectedItem)).ToArray()))));
        actions.Children.Add(Ui.AsyncButton("Limpar filtros", () => owner.Execute(() => ApplyFilters([]))));
        actions.Children.Add(Ui.AsyncButton("Limpar ordenação", () => owner.Execute(() => ApplyFilters(clauses.ToArray(), null, true))));
        filterPanel.Children.Add(actions);
        activeFilters.ItemsSource = clauses; filterPanel.Children.Add(activeFilters);
        filterOperator.SelectionChanged += (_, _) => filterValue.IsEnabled = Operators[filterOperator.SelectedIndex].Operator is not (FilterOperator.Empty or FilterOperator.NotEmpty);
        filters.Content = filterPanel; SetDock(filters, Dock.Top); Children.Add(filters);
        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8),
        };
        previous = Ui.AsyncButton(
            "Anterior",
            () => owner.Execute(() => Load(Math.Max(0, Offset - 500)))
        );
        next = Ui.AsyncButton("Próxima", () => owner.Execute(() => Load(Offset + 500)));
        bar.Children.Add(previous);
        bar.Children.Add(next);
        caption.Margin = new Thickness(10, 3, 10, 6);
        bar.Children.Add(caption);
        export = Ui.AsyncButton("Exportar tabela…", () => owner.Execute(Export));
        bar.Children.Add(export);
        deleteSelected = new Button { Content = "Excluir selecionadas…" };
        deleteFiltered = new Button { Content = "Excluir filtradas…" };
        deleteSelected.Click += async (_, _) => { await owner.Execute(() => DeleteRows(false)); UpdateDeleteButtons(); };
        deleteFiltered.Click += async (_, _) => { await owner.Execute(() => DeleteRows(true)); UpdateDeleteButtons(); };
        bar.Children.Add(deleteSelected); bar.Children.Add(deleteFiltered);
        if(selectionOnly)deleteSelected.Visibility=deleteFiltered.Visibility=Visibility.Collapsed;
        SetDock(bar, Dock.Bottom);
        Children.Add(bar);
        Children.Add(grid);
    }

    private Task AddFilter()
    {
        if (filterColumn.SelectedItem is not string column) throw new InvalidOperationException("Selecione uma coluna para filtrar.");
        var operation = Operators[filterOperator.SelectedIndex];
        var filter = new TableFilter(column, operation.Operator, filterValue.Text);
        string label = column + " · " + operation.Label + (operation.Operator is not (FilterOperator.Empty or FilterOperator.NotEmpty) ? " · " + filterValue.Text : "");
        return ApplyFilters(clauses.Append(new Clause(filter, label)).ToArray());
    }

    private async Task ApplyFilters(Clause[] nextFilters, TableSort? nextSort = null, bool updateSort = false)
    {
        var cancellation = owner.OperationCancellation;
        var order = updateSort ? nextSort : sort;
        var progress = owner.StartTableOperation(order == null ? "Filtrando a tabela completa…" : "Ordenando a tabela completa…");
        FilteredTable? replacement = null;
        try
        {
            bool editable = CanEdit;
            var nextVersion = editable ? await Task.Run(() => TableRowEditor.Version(source), cancellation) : null;
            if (nextFilters.Length > 0 || order != null)
                replacement = await Task.Run(() => FilteredTable.Create(source, nextFilters.Select(c => c.Filter).ToArray(), cancellation, progress, sort: order), cancellation);
            var nextPage = await Task.Run(() => replacement?.Page() ?? new FilteredPage(TableStore.Page(source), []), cancellation);
            cancellation.ThrowIfCancellationRequested();
            var previousFilter = filtered; filtered = replacement; replacement = null;
            version = nextVersion;
            sort = order;
            clauses.Clear(); foreach (var clause in nextFilters) clauses.Add(clause);
            ShowPage(nextPage);
            previousFilter?.Dispose();
        }
        finally { replacement?.Dispose(); }
    }

    private async Task Export()
    {
        await owner.Session.RefreshHistory(true);
        owner.Session.RequireApprovedExport(exportReference ?? owner.Session.ProjectReference(source.Path));
        string? path = Ui.SaveFile("Excel|*.xlsx|CSV|*.csv", "Tabela.xlsx");
        if (path == null) return;
        await Task.Run(() => Dendro.Core.Configuration.ProjectStore.AtomicWrite(path, temporary =>
        {
            using var cursor = new TableCursor(filtered?.Source ?? source);
            if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                using var writer = new StreamWriter(temporary, false, new System.Text.UTF8Encoding(true));
                string Cell(object? cell) => "\"" + TableCursor.Text(cell).Replace("\"", "\"\"") + "\"";
                writer.WriteLine(string.Join(";", cursor.Columns.Select(c => Cell(c))));
                foreach (var row in cursor.Rows()) writer.WriteLine(string.Join(";", row.Select(Cell)));
            }
            else
            {
                if (cursor.TotalRows.HasValue && !TableTools.FitsExcel(cursor.TotalRows.Value, cursor.Columns.Length))
                    throw new InvalidOperationException("A tabela ultrapassa o limite do Excel. Exporte em CSV.");
                StreamingExcelWriter.Write(temporary, cursor.Columns, cursor.Rows());
            }
        }, true));
        owner.Log("Tabela exportada: " + path);
    }

    internal async Task Load(long offset = 0)
    {
        if (filtered == null && CanEdit) version = await Task.Run(() => TableRowEditor.Version(source));
        var page = await Task.Run(() => filtered?.Page(offset) ?? new FilteredPage(TableStore.Page(source, offset), []));
        ShowPage(page);
    }

    private void ShowPage(FilteredPage result)
    {
        var page = result.Page;
        displayedPage = result;
        Offset = page.Offset;
        Total = page.Total;
        if (filterColumn.ItemsSource is not string[] { Length: > 0 })
        {
            filterColumn.ItemsSource = page.Data.Rows[0].Select(c => c?.ToString() ?? "").ToArray();
            filterColumn.SelectedIndex = 0;
        }
        var table = Ui.Grid(page.Data.ToJson());
        table.SelectionMode = DataGridSelectionMode.Extended;
        table.SelectionUnit = DataGridSelectionUnit.FullRow;
        table.CanUserDeleteRows = false;
        table.IsReadOnly = true;
        table.SelectionChanged += (_, _) =>
        {
            UpdateDeleteButtons();if(syncingSelection)return;
            var rows=table.SelectedItems.OfType<DataRowView>().Select(r=>{int i=r.Row.Table.Rows.IndexOf(r.Row);return result.SourceRows.Length>i?result.SourceRows[i]:source.HeaderRow+Offset+i+1;}).ToArray();
            linkedSelection=rows;RowsSelected?.Invoke(rows);
        };
        table.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Delete || !CanEdit) return;
            e.Handled = true; await owner.Execute(() => DeleteRows(false)); UpdateDeleteButtons();
        };
        table.HeadersVisibility = DataGridHeadersVisibility.All;
        long SourceRow(DataRowView row)
        {
            int index = row.Row.Table.Rows.IndexOf(row.Row);
            return result.SourceRows.Length > index ? result.SourceRows[index] : source.HeaderRow + Offset + index + 1;
        }
        table.LoadingRow += (_, e) => { if (e.Row.Item is DataRowView row) e.Row.Header = SourceRow(row).ToString("N0"); };
        table.Sorting += async (_, e) =>
        {
            e.Handled = true;
            string column = e.Column.Header?.ToString() ?? "";
            int c = page.Data.Column(column);
            var format = ColumnFormatting(column);
            var kind = format is ColumnFormat.Number or ColumnFormat.Date or ColumnFormat.Time or ColumnFormat.DateTime
                || format == ColumnFormat.Automatic && page.Data.Rows.Skip(1).Any(r => r[c] is double) ? TableSortKind.Number : TableSortKind.Text;
            await owner.Execute(() => ApplyFilters(clauses.ToArray(), new(column,kind,sort?.Column==column && !sort.Descending),true));
        };
        table.AutoGeneratingColumn += (_, e) =>
        {
            string name = e.Column.Header?.ToString() ?? "";
            ColumnFormat format = ColumnFormatting(name);
            if (e.Column is DataGridTextColumn textColumn)
                textColumn.Binding = new Binding(e.PropertyName) { Converter = new TableCellFormatting(format), StringFormat = "{0:G6}" };
            var formats = new ContextMenu();
            foreach (var option in new[] { ("Ordenar A → Z", TableSortKind.Text, false), ("Ordenar Z → A", TableSortKind.Text, true),
                ("Ordenar menor → maior", TableSortKind.Number, false), ("Ordenar maior → menor", TableSortKind.Number, true) })
            {
                var item = new MenuItem { Header = option.Item1 };
                item.Click += async (_, _) => await owner.Execute(() => ApplyFilters(clauses.ToArray(),new(name,option.Item2,option.Item3),true));
                formats.Items.Add(item);
            }
            formats.Items.Add(new Separator());
            e.Column.SortDirection = sort?.Column == name ? sort.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending : null;
            foreach (var item in new[] { (ColumnFormat.Automatic, "Automático"), (ColumnFormat.Number, "Número"), (ColumnFormat.Text, "Texto"),
                (ColumnFormat.Date, "Data"), (ColumnFormat.Time, "Hora"), (ColumnFormat.DateTime, "Data e hora") })
            {
                var choice = new MenuItem { Header = item.Item2, IsCheckable = true, IsChecked = SavedColumnFormat(name) == item.Item1 };
                choice.Click += (_, _) => { SaveColumnFormat(name, item.Item1); ShowPage(result); };
                formats.Items.Add(choice);
            }
            formats.Items.Add(new Separator());
            foreach (var action in new[] { ("Remover coluna…", false), ("Remover colunas…", true) })
            {
                var remove = new MenuItem { Header = action.Item1, IsEnabled = CanEdit,
                    ToolTip = CanEdit ? "Remove os dados das colunas escolhidas em todas as linhas da tabela, com confirmação." : "Copie tabelas de Excel ou do histórico para uma tabela DLB editável." };
                remove.Click += async (_, _) => await owner.Execute(() => RemoveColumns(name, action.Item2));
                formats.Items.Add(remove);
            }
            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), table.TryFindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)) as Style);
            headerStyle.Setters.Add(new Setter(ContextMenuProperty, formats)); e.Column.HeaderStyle = headerStyle;
            var template = new DataTemplate();
            var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding());
            text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); stack.AppendChild(text);
            var button = new FrameworkElementFactory(typeof(Button)); button.SetValue(ContentControl.ContentProperty, "⌄");
            button.SetValue(WidthProperty, 22d); button.SetValue(HeightProperty, 22d); button.SetValue(MinWidthProperty, 0d); button.SetValue(MinHeightProperty, 0d);
            button.SetValue(Control.PaddingProperty, new Thickness(0)); button.SetValue(MarginProperty, new Thickness(6, 0, 0, 0));
            button.SetValue(ToolTipProperty, "Filtrar " + name);
            if (clauses.Any(c => c.Filter.Column == name)) button.SetValue(Control.ForegroundProperty, Brushes.DodgerBlue);
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) => { filterColumn.SelectedItem = name; filters.IsExpanded = true; filterValue.Focus(); }));
            stack.AppendChild(button); template.VisualTree = stack; e.Column.HeaderTemplate = template;
        };
        grid.Content = table;
        caption.Text =
            $"{(Total == 0 ? 0 : Offset + 1):N0}–{Math.Min(Offset + 500, Total):N0} de {Total:N0} linhas"
            + (clauses.Count > 0 && filtered != null ? $" filtradas (base: {filtered.SourceCount:N0})" : "")
            + (sort != null ? $" · {sort.Column} {(sort.Descending ? "↓" : "↑")}" : "");
        export.Content = clauses.Count == 0 ? "Exportar tabela…" : "Exportar resultado filtrado…";
        activeFilters.Visibility = clauses.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        previous.IsEnabled = Offset > 0;
        next.IsEnabled = Offset + 500 < Total;
        UpdateDeleteButtons();
        SelectSourceRows(linkedSelection);
    }

    private void UpdateDeleteButtons()
    {
        deleteSelected.IsEnabled = CanEdit && grid.Content is DataGrid table && table.SelectedItems.Count > 0;
        deleteFiltered.IsEnabled = CanEdit && filtered != null && clauses.Count > 0 && Total > 0;
        string help = CanEdit ? "Selecione linhas pelo número à esquerda; Ctrl e Shift permitem selecionar várias. A exclusão pede confirmação e modifica a tabela no banco."
            : readOnly ? "Este relatório é somente para consulta. Corrija o cadastro ou os dados da tabela de origem."
            : owner.Session.IsHistoricalTable(exportReference ?? owner.Session.ProjectReference(source.Path))
                ? "As versões do processamento são preservadas. Copie esta tabela para o banco para editar os dados fora do histórico."
                : "Para excluir linhas, copie esta tabela para o banco DLB do projeto e abra a cópia.";
        deleteSelected.ToolTip = help;
        deleteFiltered.ToolTip = CanEdit ? "Exclui todas as linhas que correspondem aos filtros ativos, incluindo as outras páginas." : help;
        ToolTipService.SetShowOnDisabled(deleteSelected, true); ToolTipService.SetShowOnDisabled(deleteFiltered, true);
    }

    private async Task DeleteRows(bool allFiltered)
    {
        if (!CanEdit || version == null) throw new InvalidOperationException("A exclusão está disponível nas tabelas editáveis do banco DLB. Copie tabelas de Excel ou do histórico para o banco antes de editar.");
        if (allFiltered && (filtered == null || clauses.Count == 0)) throw new InvalidOperationException("Aplique um filtro antes de excluir linhas filtradas.");
        if (grid.Content is not DataGrid table || displayedPage == null) return;
        long[] selected = table.SelectedItems.OfType<DataRowView>().Select(row =>
        {
            int index = row.Row.Table.Rows.IndexOf(row.Row);
            return displayedPage.SourceRows.Length > index ? displayedPage.SourceRows[index] : source.HeaderRow + Offset + index + 1;
        }).Distinct().Order().ToArray();
        long count = allFiltered && filtered != null ? Total : selected.LongLength;
        if (count == 0) throw new InvalidOperationException("Selecione linhas ou aplique um filtro antes de excluir.");
        if (!(DeleteConfirmation != null ? await DeleteConfirmation(count) : ConfirmDeletion(count, allFiltered))) return;
        var cancellation = owner.OperationCancellation;
        var progress = owner.StartTableOperation($"Excluindo {count:N0} linhas…");
        IEnumerable<long> rows = allFiltered && filtered != null ? filtered.SourceRowNumbers().Order() : selected;
        var deleted = await Task.Run(() => TableRowEditor.Delete(source, version, rows, cancellation, progress), cancellation);
        filtered?.Dispose(); filtered = null; clauses.Clear(); sort = null;
        owner.Session.Touch(); await Load();
        owner.Log($"{deleted.Removed:N0} linha(s) excluída(s) · {deleted.Remaining:N0} preservada(s). Filtros limpos após a exclusão.");
    }

    private bool ConfirmDeletion(long count, bool allFiltered)
    {
        var content = Ui.Form();
        content.Children.Add(Ui.Text($"Excluir {count:N0} linha(s) da tabela?", 20));
        content.Children.Add(Ui.Text(allFiltered ? "Serão excluídas todas as linhas do filtro, inclusive as de outras páginas." : "Serão excluídas as linhas selecionadas na página atual."));
        content.Children.Add(Ui.Text("A exclusão será gravada no banco e não pode ser desfeita por esta tela."));
        var accept = new Button { Content = $"Excluir {count:N0} linha(s)" }; var cancel = new Button { Content = "Cancelar" };
        var window = Ui.Dialog("Confirmar exclusão de linhas", Ui.WithToolbar(content, accept, cancel), 580, 340);
        accept.Click += (_, _) => window.DialogResult = true; cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true;
    }

    private async Task RemoveColumns(string column, bool multiple)
    {
        if (!CanEdit || version == null) throw new InvalidOperationException("A remoção de colunas está disponível em tabelas DLB editáveis.");
        string[]? selected = ColumnRemovalSelection != null ? await ColumnRemovalSelection([column]) : SelectColumns(column, multiple);
        if (selected == null || selected.Length == 0) return;
        var cancellation = owner.OperationCancellation; var progress = owner.StartTableOperation($"Removendo {selected.Length} coluna(s)…");
        long rows = await Task.Run(() => TableRowEditor.RemoveColumns(source, version, selected, cancellation, progress), cancellation);
        filtered?.Dispose(); filtered = null; clauses.Clear(); sort = null; filterColumn.ItemsSource = Array.Empty<string>();
        owner.Session.Touch(); await Load();
        owner.Log($"{selected.Length} coluna(s) removida(s): {string.Join(", ", selected)}. {rows:N0} linhas preservadas.");
    }

    private string[]? SelectColumns(string initial, bool multiple)
    {
        var form = Ui.Form(); form.Children.Add(Ui.Text(multiple ? "Selecione as colunas para remover" : "Confirmar remoção da coluna", 20));
        var boxes = version!.Columns.Select(c => new CheckBox { Content = c, IsChecked = c == initial, Margin = new Thickness(0, 4, 0, 4) }).ToArray();
        if (!multiple) boxes = boxes.Where(b => b.IsChecked == true).ToArray();
        var choices = new StackPanel(); foreach (var box in boxes) choices.Children.Add(box);
        form.Children.Add(new ScrollViewer { Content = choices, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        form.Children.Add(Ui.Text("Os dados dessas colunas serão removidos de todas as linhas, inclusive das que não aparecem no filtro. As linhas e as demais colunas serão preservadas."));
        var accept = new Button(); var cancel = new Button { Content = "Cancelar" };
        void Update() { int count = boxes.Count(b => b.IsChecked == true); accept.Content = $"Remover {count} coluna(s)"; accept.IsEnabled = count > 0 && count < version.Columns.Length; }
        foreach (var box in boxes) box.Click += (_, _) => Update(); Update();
        var dialog = Ui.Dialog("Remover colunas", Ui.WithToolbar(form, accept, cancel), 650, 550);
        accept.Click += (_, _) => dialog.DialogResult = true; cancel.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true ? boxes.Where(b => b.IsChecked == true).Select(b => b.Content.ToString()!).ToArray() : null;
    }

    private string FormatReference => exportReference ?? owner.Session.ProjectReference(source.Path);
    private ColumnFormat SavedColumnFormat(string column)
    {
        string value = owner.Session.Meta.Arr("table_column_formats").FirstOrDefault(f => f.S("path") == FormatReference && f.S("sheet") == source.Table && f.S("column") == column).S("format");
        return Enum.TryParse<ColumnFormat>(value, out var format) ? format : ColumnFormat.Automatic;
    }
    private ColumnFormat ColumnFormatting(string column)
    {
        var format = SavedColumnFormat(column); if (format != ColumnFormat.Automatic) return format;
        if (column == owner.Session.Data.At("arquivo_entrada.colunas").S("data_medicao") || column == owner.Session.Data.At("cadastro.colunas").S("data_plantio")) return ColumnFormat.Date;
        return TableCellFormatting.Guess(column);
    }
    private void SaveColumnFormat(string column, ColumnFormat format)
    {
        var settings = owner.Session.Meta.Arr("table_column_formats");
        foreach (var old in settings.Where(f => f.S("path") == FormatReference && f.S("sheet") == source.Table && f.S("column") == column).ToArray()) settings.Remove(old);
        settings.Add(new JsonObject { ["path"] = FormatReference, ["sheet"] = source.Table, ["column"] = column, ["format"] = format.ToString() });
        owner.Session.Touch();
    }

    public void Dispose() { filtered?.Dispose(); filtered = null; releaseSource?.Invoke(); releaseSource = null; }
}
