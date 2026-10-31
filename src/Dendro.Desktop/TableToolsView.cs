using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class TableToolsView : UserControl
{
    internal sealed class InputRow
    {
        public bool Selected { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; init; } = "";
        public string Sheet { get; init; } = "";
    }

    internal sealed class GroupRow
    {
        public bool Selected { get; set; } = true;
        public string Value { get; init; } = "";
        public string Name { get; set; } = "";
        public long Rows { get; init; }
        public string Key { get; init; } = "";
    }

    internal sealed class FilterRow
    {
        public string Column { get; set; } = "";
        public string Operator { get; set; } = "=";
        public string Value { get; set; } = "";
    }

    internal sealed class DerivedColumnRow
    {
        public string Name { get; init; } = "";
        public string[] SourceColumns { get; init; } = [];
        public string Separator { get; init; } = " · ";
        public string Expression { get; init; } = "";
        public string SourcesDisplay => Expression.Length > 0 ? Expression : string.Join("; ", SourceColumns);
    }

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly bool stratify;
    private readonly bool calculateColumns;
    private readonly ComboBox source,
        column = Ui.Combo([]);
    private readonly ListBox stratificationColumns = new() { SelectionMode = SelectionMode.Extended, MinHeight = 100, MaxHeight = 170 };
    private readonly ListBox derivedSources = new() { SelectionMode = SelectionMode.Extended, MinHeight = 90, MaxHeight = 140 };
    private readonly TextBox name = Ui.Input("Tabelas_unidas"),
        derivedName = Ui.Input(),
        derivedExpression = Ui.Input(),
        derivedSeparator = Ui.Input(" · ");
    private readonly TextBlock calculatedPreview = Ui.Text("");
    private readonly CheckBox commonColumns = new()
    {
        Content = new TextBlock { Text = "Usar apenas colunas comuns a todas as tabelas", TextWrapping = TextWrapping.Wrap },
        IsChecked = false,
    };
    private readonly ObservableCollection<InputRow> inputs = new();
    private readonly ObservableCollection<GroupRow> groups = new();
    private readonly ObservableCollection<FilterRow> filters = new();
    private readonly ObservableCollection<DerivedColumnRow> derivedColumns = new();
    private readonly DataGrid inputGrid,
        groupGrid,
        filterGrid,
        derivedGrid;
    private readonly TextBlock status = Ui.Text("");
    private string? discoveredSignature;
    private string[] sourceColumns = [];
    private bool syncingColumnSelection;
    private static readonly string[] Operators =
    [
        "=",
        "≠",
        ">",
        "≥",
        "<",
        "≤",
        "Contém",
        "Começa com",
        "Vazio",
        "Preenchido",
    ];
    private static readonly FilterOperator[] FilterOperators =
    [
        FilterOperator.Equal,
        FilterOperator.NotEqual,
        FilterOperator.Greater,
        FilterOperator.GreaterOrEqual,
        FilterOperator.Less,
        FilterOperator.LessOrEqual,
        FilterOperator.Contains,
        FilterOperator.StartsWith,
        FilterOperator.Empty,
        FilterOperator.NotEmpty,
    ];

    public TableToolsView(
        MainWindow owner,
        ProjectSession session,
        bool stratify,
        string? selectedPath = null,
        bool calculateColumns = false
    )
    {
        this.owner = owner;
        this.session = session;
        this.stratify = stratify;
        this.calculateColumns = calculateColumns;
        foreach (var path in session.Files.Where(session.IsTabular))
        {
            if ((stratify || calculateColumns) && Path.GetExtension(path).Equals(".dlb", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new CompactBankReader(session.Resolve(path));
                foreach (var table in reader.Catalog.Tables.Where(t => !t.Name.StartsWith(Dendro.Core.Processing.ProcessingHistory.InternalPrefix, StringComparison.Ordinal)))
                    inputs.Add(new() { Path = path, Sheet = table.Name,
                        Name = session.ContentName(path) + " · " + table.Name });
                continue;
            }
            inputs.Add(
                new()
                {
                    Path = path,
                    Name = session.ContentName(path),
                    Selected = path == selectedPath,
                }
            );
        }
        source = Ui.Combo(inputs.Select(i => i.Name));
        if (selectedPath != null)
            source.SelectedIndex = inputs.ToList().FindIndex(i => i.Path == selectedPath &&
                (i.Sheet.Length == 0 || i.Sheet == session.ExcelOptions(selectedPath).S("sheet")));
        if (source.SelectedIndex < 0 && inputs.Count > 0) source.SelectedIndex = 0;
        var panel = Ui.Form();
        panel.Children.Add(owner.ToolHeader(calculateColumns ? "Gerar coluna" : stratify ? "Estratificar" : "Juntar tabelas"));
        Ui.Section(panel, calculateColumns ? "Definir coluna calculada" : stratify ? "Definir estratos" : "Selecionar tabelas para juntar");
        inputGrid = Grid(220);
        inputGrid.ItemsSource = inputs;
        inputGrid.Columns.Add(
            new DataGridCheckBoxColumn { Header = "Usar", Binding = new Binding("Selected") }
        );
        inputGrid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Tabela de origem",
                Binding = new Binding("Name"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            }
        );
        groupGrid = Grid(230);
        groupGrid.ItemsSource = groups;
        groupGrid.Columns.Add(
            new DataGridCheckBoxColumn { Header = "Gerar", Binding = new Binding("Selected") }
        );
        groupGrid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Estrato",
                Binding = new Binding("Value"),
                IsReadOnly = true,
            }
        );
        groupGrid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Nome da saída",
                Binding = new Binding("Name"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            }
        );
        groupGrid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Linhas",
                Binding = new Binding("Rows"),
                IsReadOnly = true,
            }
        );
        filterGrid = Grid(135);
        filterGrid.ItemsSource = filters;
        filterGrid.Columns.Add(
            new DataGridComboBoxColumn
            {
                Header = "Coluna",
                SelectedItemBinding = new Binding("Column"),
            }
        );
        filterGrid.Columns.Add(
            new DataGridComboBoxColumn
            {
                Header = "Condição",
                ItemsSource = Operators,
                SelectedItemBinding = new Binding("Operator"),
            }
        );
        filterGrid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Valor",
                Binding = new Binding("Value"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            }
        );
        derivedGrid = Grid(110);
        derivedGrid.ItemsSource = derivedColumns;
        derivedGrid.Columns.Add(new DataGridTextColumn { Header = "Nova coluna", Binding = new Binding("Name"), IsReadOnly = true });
        derivedGrid.Columns.Add(new DataGridTextColumn { Header = "Expressão / origem", Binding = new Binding("SourcesDisplay"), IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        derivedGrid.Columns.Add(new DataGridTextColumn { Header = "Separador", Binding = new Binding("Separator"), IsReadOnly = true });
        if (stratify || calculateColumns)
        {
            name.Text = "Estrato";
            Ui.Field(panel, "Tabela de origem", source);
            if (!calculateColumns)
            {
            Ui.Field(panel, "Colunas para estratificar (Ctrl/Shift para selecionar várias)", stratificationColumns);
            Ui.Field(panel, "Coluna principal (seleção simples)", column);
            }
            if (calculateColumns)
            {
            Ui.Field(panel, "Colunas de origem da nova coluna (selecione várias)", derivedSources);
            Ui.Field(panel, "Nome da nova coluna", derivedName);
            Ui.Field(panel, "Expressão (use [Nome da coluna])", derivedExpression);
            panel.Children.Add(Ui.Button("Inserir coluna na expressão", () => {
                if (derivedSources.SelectedItem is not string selected) throw new InvalidOperationException("Selecione uma coluna de origem.");
                derivedExpression.SelectedText = "[" + selected + "]";
                derivedExpression.Focus();
            }));
            Ui.AddHelp(panel, "Exemplo para [Código] = RDBOBA00123P20-001:\nESQUERDA([Código]; 6) → RDBOBA\nMEIO([Código]; 12; 3) → P20\nMEIO([Código]; 12; 1) → P\nSE(MEIO([Código]; 12; 1) = \"P\"; \"Plantio\"; \"Outro\")\nAs posições começam em 1. Use ponto nos decimais. Também: DIREITA, ANTES, DEPOIS, CONTEM, SUBSTITUIR, MAIUSCULA, MINUSCULA, APARAR, TAMANHO, +, -, *, / e & para juntar textos.");
            Ui.Field(panel, "Separador entre valores", derivedSeparator);
            Ui.AddHelp(panel, "Se a expressão ficar vazia, as colunas selecionadas serão concatenadas com o separador. As novas colunas ficam disponíveis para cálculo, filtro e estratificação.");
            Ui.Section(panel,"Revisar e gravar colunas");
            var derivedButtons = new StackPanel { Orientation = Orientation.Horizontal };
            derivedButtons.Children.Add(Ui.Button("Criar coluna derivada", AddDerivedColumn));
            derivedButtons.Children.Add(Ui.Button("Remover coluna derivada", RemoveDerivedColumn));
            panel.Children.Add(derivedButtons);
            Ui.Field(panel, "Colunas derivadas", derivedGrid);
            panel.Children.Add(Ui.AsyncButton("Prévia das novas colunas", () => owner.Execute(async () => {
                var table = Source(CurrentPath); var specs = DerivedSpecs();
                if (specs.Length == 0) throw new InvalidOperationException("Adicione uma coluna antes de visualizar.");
                var preview = await Task.Run(() => TableTools.PreviewColumns(table, specs));
                calculatedPreview.Text = preview.Length == 0 ? "A tabela não contém linhas." : string.Join("\n", preview);
            })));
            Ui.AddHelp(panel, "A prévia mostra até cinco linhas.");
            panel.Children.Add(calculatedPreview);
            panel.Children.Add(Ui.AsyncButton("Aplicar colunas na tabela", () => owner.Execute(SaveCalculatedTable)));
            Ui.AddHelp(panel, "Grava as novas colunas na própria tabela DLB selecionada. As outras tabelas do banco são preservadas.");
            }
            if (!calculateColumns)
            {
            Ui.Section(panel,"Filtros e resultados");
            Ui.Field(panel, "Filtros opcionais (todas as condições)", filterGrid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(
                Ui.Button(
                    "Adicionar filtro",
                    () => filters.Add(new() { Column = column.SelectedItem as string ?? "" })
                )
            );
            buttons.Children.Add(
                Ui.Button(
                    "Remover filtro",
                    () =>
                    {
                        if (filterGrid.SelectedItem is FilterRow f)
                            filters.Remove(f);
                    }
                )
            );
            panel.Children.Add(buttons);
            panel.Children.Add(Ui.AsyncButton("Listar estratos", () => owner.Execute(Discover)));
            Ui.Field(panel, "Estratos e nomes dos resultados", groupGrid);
            column.SelectionChanged += (_, _) =>
            {
                if (syncingColumnSelection || column.SelectedItem is not string selected || !stratificationColumns.Items.Contains(selected))
                    return;
                stratificationColumns.SelectedItems.Clear();
                stratificationColumns.SelectedItems.Add(selected);
            };
            }
            source.SelectionChanged += async (_, _) => await owner.Execute(LoadColumns);
        }
        else
        {
            Ui.Section(panel,"Opções da junção");
            Ui.Field(panel, "Tabelas de origem", inputGrid);
            Ui.AddHelp(panel, "A junção cria automaticamente a coluna Fonte com o nome da tabela de origem de cada linha. Se já existir uma coluna Fonte, seus valores são preservados como Fonte_anterior.");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(
                Ui.Button(
                    "Marcar todas",
                    () =>
                    {
                        foreach (var row in inputs)
                            row.Selected = true;
                        inputGrid.Items.Refresh();
                    }
                )
            );
            buttons.Children.Add(
                Ui.Button(
                    "Limpar seleção",
                    () =>
                    {
                        foreach (var row in inputs)
                            row.Selected = false;
                        inputGrid.Items.Refresh();
                    }
                )
            );
            panel.Children.Add(buttons);
            System.Windows.Automation.AutomationProperties.SetAutomationId(commonColumns, "Merge.CommonColumns");
            panel.Children.Add(commonColumns);
            Ui.AddHelp(panel, "Marcado: A, B, C + A, B, C, D → A, B, C. Usa nomes exatamente iguais e mantém a ordem da primeira tabela. Desmarcado: exige as mesmas colunas em todas as origens.");
            Ui.Field(panel, "Nome do resultado", name);
        }
        Ui.AddHelp(panel, "Resultados salvos no próprio arquivo DLB do projeto, sem gerar Excel automaticamente. Salve o projeto antes de executar. Use Exportar na tabela quando precisar de Excel ou CSV.");
        if (!calculateColumns) panel.Children.Add(
            Ui.AsyncButton(stratify ? "Gerar estratos" : "Juntar tabelas", () => owner.Execute(Run))
        );
        panel.Children.Add(status);
        Content = Ui.Scroll(panel);
    }

    private static DataGrid Grid(double height) =>
        new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            Height = height,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = false,
            MinColumnWidth = 72,
            ColumnWidth = 100,
        };

    private TableSource Source(string path)
    {
        var options = session.ExcelOptions(path);
        if (source.SelectedIndex >= 0 && inputs[source.SelectedIndex] is var selected && selected.Path == path && selected.Sheet.Length > 0)
            return new(session.Resolve(path), selected.Sheet, 1);
        return new(session.Resolve(path), options.S("sheet"), (int)options.N("header_row", 1));
    }

    private string CurrentPath =>
        source.SelectedIndex >= 0
            ? inputs[source.SelectedIndex].Path
            : throw new InvalidOperationException("Selecione uma tabela de origem.");

    internal async Task Initialize()
    {
        if ((stratify || calculateColumns) && inputs.Count > 0)
            await LoadColumns();
    }

    private async Task LoadColumns()
    {
        var table = Source(CurrentPath);
        sourceColumns = await Task.Run(() =>
        {
            using var cursor = new TableCursor(table);
            return cursor.Columns;
        });
        derivedColumns.Clear();
        calculatedPreview.Text = "";
        RefreshColumnChoices(sourceColumns.Length > 0 ? [sourceColumns[0]] : []);
        filters.Clear();
        groups.Clear();
        discoveredSignature = null;
    }

    private string[] AvailableColumns => sourceColumns.Concat(derivedColumns.Select(item => item.Name)).ToArray();

    private string[] SelectedStratificationColumns =>
        stratificationColumns.SelectedItems.Cast<string>().ToArray();

    private DerivedColumn[] DerivedSpecs() => derivedColumns
        .Select(item => new DerivedColumn(item.Name, item.SourceColumns, item.Separator, item.Expression))
        .ToArray();

    private void RefreshColumnChoices(IEnumerable<string> selected)
    {
        string[] choices = AvailableColumns;
        var wanted = selected.ToHashSet(StringComparer.Ordinal);
        syncingColumnSelection = true;
        try
        {
            stratificationColumns.ItemsSource = choices;
            foreach (string item in choices.Where(wanted.Contains))
                stratificationColumns.SelectedItems.Add(item);
            column.ItemsSource = choices;
            column.SelectedIndex = choices.Length > 0 ? 0 : -1;
            derivedSources.ItemsSource = choices;
            ((DataGridComboBoxColumn)filterGrid.Columns[0]).ItemsSource = choices;
        }
        finally
        {
            syncingColumnSelection = false;
        }
    }

    private void AddDerivedColumn()
    {
        string newName = derivedName.Text.Trim();
        string expression = derivedExpression.Text.Trim();
        string[] sources = derivedSources.SelectedItems.Cast<string>().ToArray();
        if (newName.Length == 0 || (sources.Length == 0 && expression.Length == 0))
            throw new InvalidOperationException("Informe o nome da nova coluna e uma expressão ou colunas para concatenar.");
        if (AvailableColumns.Contains(newName, StringComparer.Ordinal))
            throw new InvalidOperationException("Já existe uma coluna com esse nome.");
        string separator = derivedSeparator.Text;
        if (separator.Length > 20)
            throw new InvalidOperationException("O separador pode ter até 20 caracteres.");
        if (expression.Length > 0) sources = new ColumnExpression(expression, AvailableColumns).Dependencies;
        var row = new DerivedColumnRow { Name = newName, SourceColumns = sources, Separator = separator, Expression = expression };
        derivedColumns.Add(row);
        RefreshColumnChoices(SelectedStratificationColumns.Append(newName));
        derivedName.Text = "";
        derivedExpression.Text = "";
        calculatedPreview.Text = "";
        derivedSources.SelectedItems.Clear();
        groups.Clear();
        discoveredSignature = null;
    }

    private async Task SaveCalculatedTable()
    {
        string parent = CurrentPath;
        if (session.IsHistoricalTable(parent)) throw new InvalidOperationException("Uma versão do histórico é preservada. Copie a tabela para criar colunas fora do processamento original.");
        var table = Source(parent); var specs = DerivedSpecs();
        if (!Path.GetExtension(table.Path).Equals(".dlb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Para aplicar colunas, copie a tabela para o banco DLB do projeto e selecione essa cópia.");
        string folder = session.ProjectWorkFolder(), outputName = "colunas_" + Guid.NewGuid().ToString("N");
        var cancellation = owner.OperationCancellation;
        owner.StartTableOperation("Calculando novas colunas…");
        var result = await Task.Run(() => TableTools.CalculateColumns(table, specs, folder, outputName, cancellation));
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.Run(() => CompactBankFile.ReplaceTable(table, result.Path));
        }
        finally { if (File.Exists(result.Path)) File.Delete(result.Path); }
        session.Touch();
        await LoadColumns();
        await owner.RefreshUpdatedTable(parent, table.Table);
        owner.Log($"Tabela {table.Table} atualizada: {specs.Length} nova(s) coluna(s), {result.Rows:N0} linhas.");
    }

    private void RemoveDerivedColumn()
    {
        if (derivedGrid.SelectedItem is not DerivedColumnRow selected)
            throw new InvalidOperationException("Selecione a coluna derivada que deseja remover.");
        if (derivedColumns.Any(item => item.SourceColumns.Contains(selected.Name, StringComparer.Ordinal)))
            throw new InvalidOperationException("Remova primeiro as colunas derivadas que dependem desta coluna.");
        derivedColumns.Remove(selected);
        foreach (var filter in filters.Where(filter => filter.Column == selected.Name).ToArray())
            filters.Remove(filter);
        RefreshColumnChoices(SelectedStratificationColumns.Where(name => name != selected.Name));
        groups.Clear();
        discoveredSignature = null;
    }

    private TableFilter[] Filters()
    {
        filterGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        filterGrid.CommitEdit(DataGridEditingUnit.Row, true);
        return filters
            .Select(f => new TableFilter(
                f.Column,
                FilterOperators[Array.IndexOf(Operators, f.Operator)],
                f.Value
            ))
            .ToArray();
    }

    private string Signature(TableFilter[] criteria) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                Path = CurrentPath,
                Options = session.ExcelOptions(CurrentPath),
                Columns = SelectedStratificationColumns,
                Derived = DerivedSpecs(),
                Filters = criteria,
            }
        );

    private async Task Discover()
    {
        owner.StartTableOperation("Listando estratos…");
        var criteria = Filters();
        string[] selected = SelectedStratificationColumns;
        if (selected.Length == 0)
            throw new InvalidOperationException("Selecione uma ou mais colunas para estratificar.");
        var table = Source(CurrentPath);
        var signature = Signature(criteria);
        var cancellation = owner.OperationCancellation;
        var derived = DerivedSpecs();
        var strata = await Task.Run(() => TableTools.Discover(table, selected, criteria, derived, cancellation));
        groups.Clear();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stratum in strata)
        {
            string stem = TableTools.SuggestedName(stratum.Value),
                proposed = stem;
            int number = 2;
            while (!used.Add(proposed))
                proposed = stem + "_" + number++;
            groups.Add(
                new()
                {
                    Key = stratum.Key,
                    Value = stratum.Value,
                    Rows = stratum.Rows,
                    Name = proposed,
                }
            );
        }
        discoveredSignature = signature;
        status.Text = $"{groups.Count:N0} estratos encontrados.";
        owner.Progress.IsIndeterminate = false;
        owner.SetProgress(100);
        owner.StatusText.Text = status.Text;
    }

    private async Task Run()
    {
        var progress = owner.StartTableOperation(stratify ? "Preparando estratificação…" : "Preparando junção…");
        var cancellation = owner.OperationCancellation;
        string outputFolder = session.ProjectWorkFolder();
        TableResult[] results;
        string? parent = null;
        if (stratify)
        {
            var criteria = Filters();
            if (discoveredSignature != Signature(criteria))
                throw new InvalidOperationException(
                    "Clique em Listar estratos após definir as colunas e os filtros."
                );
            groupGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            groupGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = groups
                .Where(g => g.Selected)
                .Select(g => new StratumOutput(g.Key, g.Name))
                .ToArray();
            parent = CurrentPath;
            var table = Source(parent);
            string[] cols = SelectedStratificationColumns;
            var derived = DerivedSpecs();
            results = await Task.Run(() =>
                TableTools.Stratify(table, cols, criteria, selected, outputFolder,
                    cancellation: cancellation, compact: true, derivedColumns: derived)
            );
        }
        else
        {
            inputGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            inputGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selectedInputs = inputs.Where(i => i.Selected).ToArray();
            var tables = session.TableSources(selectedInputs.Select(i => i.Path));
            var sourceNames = selectedInputs.Select(i => i.Name).ToArray();
            string outputName = name.Text;
            bool commonOnly = commonColumns.IsChecked == true;
            results =
            [
                await Task.Run(() => TableTools.Merge(tables, outputFolder, outputName, cancellation: cancellation,
                    compact: true, commonColumnsOnly: commonOnly, progress: progress, sourceNames: sourceNames)),
            ];
        }
        owner.StatusText.Text = "Atualizando Conteúdos…";
        await session.AddTableResults(results, parent, parent == null ? null : Source(parent).Table);
        status.Text = $"{results.Length:N0} resultado(s), {results.Sum(r => r.Rows):N0} linhas.";
        owner.Progress.IsIndeterminate = false;
        owner.SetProgress(100);
        owner.Log(status.Text);
    }
}
