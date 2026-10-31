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
    private readonly ObservableCollection<Clause> clauses = new();
    private readonly Button export;
    private FilteredTable? filtered;
    private TableSort? sort;
    private IReadOnlySet<long>? includedSourceRows;
    internal event Action<long[]>? RowsSelected;
    private bool syncingSelection;
    private long[] linkedSelection=[];
    internal IEnumerable<long> ViewSourceRows()=>filtered?.SourceRowNumbers()??Enumerable.Range(0,checked((int)Total)).Select(i=>(long)source.HeaderRow+i+1);
    internal Task SetIncludedSourceRows(IEnumerable<long> rows)
    {
        includedSourceRows = rows.ToHashSet();
        return ApplyFilters(clauses.ToArray());
    }
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
    private readonly WrapPanel bar;
    internal long Offset { get; private set; }
    internal long Total { get; private set; }

    public TableBrowser(MainWindow owner, TableSource source, string? exportReference = null, bool readOnly = false, Action? releaseSource = null, bool selectionOnly = false)
    {
        this.owner = owner;
        this.source = source;
        this.exportReference = exportReference;
        this.readOnly = readOnly; this.releaseSource = releaseSource;
        Background = Brushes.White;
        ToolTip = "Use o ícone de filtro no cabeçalho da coluna. Clique no nome da coluna para ordenar; clique com o botão direito para mais opções de ordenação e formato.";
        bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        previous = Ui.IconButton(
            "Página anterior",
            CommandIconKind.ArrowLeft,
            () => { _ = owner.Execute(() => Load(Math.Max(0, Offset - 500))); }
        );
        next = Ui.IconButton(
            "Próxima página",
            CommandIconKind.ArrowRight,
            () => { _ = owner.Execute(() => Load(Offset + 500)); }
        );
        foreach (var button in new[] { previous, next })
        {
            button.Width = button.MinWidth = 32;
            button.Height = button.MinHeight = 30;
            button.Padding = new Thickness(5);
            button.Margin = new Thickness(1);
        }
        caption.Margin = new Thickness(6, 3, 6, 3);
        caption.VerticalAlignment = VerticalAlignment.Center;
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(previous);
        navigation.Children.Add(caption);
        navigation.Children.Add(next);
        export = Ui.IconButton("Exportar tabela…", CommandIconKind.Export, () => { _ = owner.Execute(Export); });
        bar.Children.Add(export);
        deleteSelected = Ui.IconButton("Excluir selecionadas…", CommandIconKind.Remove, () => { _ = owner.Execute(async () => { await DeleteRows(false); UpdateDeleteButtons(); }); });
        deleteFiltered = Ui.IconButton("Excluir filtradas…", CommandIconKind.Remove, () => { _ = owner.Execute(async () => { await DeleteRows(true); UpdateDeleteButtons(); }); });
        bar.Children.Add(deleteSelected); bar.Children.Add(deleteFiltered);
        if(selectionOnly)deleteSelected.Visibility=deleteFiltered.Visibility=Visibility.Collapsed;
        var toolbar = new DockPanel { Margin = new Thickness(4, 1, 4, 3), LastChildFill = true };
        DockPanel.SetDock(navigation, Dock.Right);
        toolbar.Children.Add(navigation);
        toolbar.Children.Add(bar);
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        Children.Add(grid);
    }

    internal void AddToolbarAction(UIElement action)
    {
        int index = bar.Children.IndexOf(export);
        bar.Children.Insert(index < 0 ? bar.Children.Count : index, action);
    }

    private Task AddFilter(string column, ComboBox filterOperator, TextBox filterValue)
    {
        var operation = Operators[filterOperator.SelectedIndex];
        var filter = new TableFilter(column, operation.Operator, filterValue.Text);
        string label = column + " · " + operation.Label + (operation.Operator is not (FilterOperator.Empty or FilterOperator.NotEmpty) ? " · " + filterValue.Text : "");
        return ApplyFilters(clauses.Append(new Clause(filter, label)).ToArray());
    }

    private FrameworkElement CreateFilterHeader(string name)
    {
        var popup = new System.Windows.Controls.Primitives.Popup();
        var header = new DockPanel { LastChildFill = true, MinHeight = 26 };
        bool hasFilter = clauses.Any(c => c.Filter.Column == name);
        var filterButton = new Button
        {
            Content = new CommandIcon { Kind = CommandIconKind.Filter, Width = 15, Height = 15, Tint = hasFilter ? Brushes.DodgerBlue : null },
            Width = 25, Height = 24, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(3), Margin = new Thickness(3, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ToolTip = "Filtrar a coluna " + name,
        };
        System.Windows.Automation.AutomationProperties.SetName(filterButton, "Filtrar " + name);
        System.Windows.Controls.DockPanel.SetDock(filterButton, Dock.Right);
        header.Children.Add(filterButton);
        header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var operation = Ui.Combo(Operators.Select(o => o.Label)); operation.SelectedIndex = 0;
        var value = Ui.Input();
        System.Windows.Automation.AutomationProperties.SetAutomationId(operation, "TableFilter.Operator." + name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(value, "TableFilter.Value." + name);
        var active = new ListBox { ItemsSource = clauses, MaxHeight = 100, DisplayMemberPath = "Label", MinWidth = 280 };
        var removeSelected = Ui.AsyncButton("Remover filtro selecionado", () => owner.Execute(() =>
            ApplyFilters(clauses.Where(c => !ReferenceEquals(c, active.SelectedItem)).ToArray())));
        removeSelected.IsEnabled = false;
        active.SelectionChanged += (_, _) => removeSelected.IsEnabled = active.SelectedItem is Clause;

        var body = new StackPanel { Width = 320 };
        body.Children.Add(Ui.Text("Filtrar coluna: " + name, 14));
        Ui.Field(body, "Condição", operation);
        Ui.Field(body, "Valor", value);
        body.Children.Add(Ui.AsyncButton("Aplicar filtro", () => owner.Execute(async () =>
        {
            await AddFilter(name, operation, value);
            popup.IsOpen = false;
        })));
        body.Children.Add(Ui.Text("Filtros ativos · condições combinadas por E", 11));
        body.Children.Add(active);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(removeSelected);
        actions.Children.Add(Ui.AsyncButton("Limpar todos os filtros", () => owner.Execute(async () =>
        {
            await ApplyFilters([]);
            popup.IsOpen = false;
        })));
        var clearSort = Ui.AsyncButton("Limpar ordenação", () => owner.Execute(async () =>
        {
            await ApplyFilters(clauses.ToArray(), null, true);
            popup.IsOpen = false;
        }));
        clearSort.IsEnabled = sort != null;
        actions.Children.Add(clearSort);
        body.Children.Add(actions);
        Ui.AddHelp(body, "Os filtros atuam na tabela inteira, inclusive nas outras páginas. Clique no nome da coluna para ordenar; o menu do botão direito oferece outros sentidos de ordenação e formatos.");

        popup.PlacementTarget = filterButton;
        popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        popup.StaysOpen = false;
        popup.AllowsTransparency = true;
        popup.Child = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(190, 202, 217)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10),
            Child = body,
        };
        operation.SelectionChanged += (_, _) => value.IsEnabled = Operators[operation.SelectedIndex].Operator is not (FilterOperator.Empty or FilterOperator.NotEmpty);
        filterButton.Click += (_, e) => { e.Handled = true; popup.IsOpen = !popup.IsOpen; };
        return header;
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
            if (nextFilters.Length > 0 || order != null || includedSourceRows != null)
                replacement = await Task.Run(() => FilteredTable.Create(source, nextFilters.Select(c => c.Filter).ToArray(), cancellation, progress, sort: order, includedSourceRows: includedSourceRows), cancellation);
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
            string column = e.Column.SortMemberPath;
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
            e.Column.SortMemberPath = e.PropertyName;
            e.Column.Header = CreateFilterHeader(name);
        };
        grid.Content = table;
        caption.Text =
            $"{(Total == 0 ? 0 : Offset + 1):N0}–{Math.Min(Offset + 500, Total):N0} de {Total:N0} linhas"
            + (clauses.Count > 0 && filtered != null ? $" filtradas (base: {filtered.SourceCount:N0})" : "")
            + (sort != null ? $" · {sort.Column} {(sort.Descending ? "↓" : "↑")}" : "");
        UpdateExportButton();
        previous.IsEnabled = Offset > 0;
        next.IsEnabled = Offset + 500 < Total;
        UpdateDeleteButtons();
        SelectSourceRows(linkedSelection);
    }

    private void UpdateExportButton()
    {
        string label = clauses.Count == 0 ? "Exportar tabela…" : "Exportar resultado filtrado…";
        export.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(export, label);
        ToolTipService.SetShowOnDisabled(export, true);
    }

    private void UpdateDeleteButtons()
    {
        deleteSelected.IsEnabled = CanEdit && grid.Content is DataGrid table && table.SelectedItems.Count > 0;
        deleteFiltered.IsEnabled = CanEdit && filtered != null && clauses.Count > 0 && Total > 0;
        string help = CanEdit ? "Selecione linhas pelo número à esquerda; Ctrl e Shift permitem selecionar várias. A exclusão pede confirmação e modifica a tabela no arquivo DLB do projeto."
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
        content.Children.Add(Ui.Text("A exclusão será gravada no arquivo DLB do projeto e não pode ser desfeita por esta tela."));
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
        filtered?.Dispose(); filtered = null; clauses.Clear(); sort = null;
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
