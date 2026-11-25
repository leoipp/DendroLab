using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class TableToolsView : ToolFormView
{
    internal sealed class InputRow
    {
        public bool Selected { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; init; } = "";
        public string Sheet { get; init; } = "";
    }

    internal sealed class GroupRow : System.ComponentModel.INotifyPropertyChanged
    {
        public bool Selected { get; set; } = true;
        public string Value { get; init; } = "";
        public string[] Values { get; init; } = [];
        public string DisplayValue => Values.Length > 1
            ? $"{Values[0]} (+{Values.Length - 1} estrato(s))"
            : Value;
        private string name = "";
        public string Name
        {
            get => name;
            set
            {
                if (name == value) return;
                name = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public long Rows { get; set; }
        public string Key { get; init; } = "";
        public string[] Keys { get; init; } = [];
    }

    private sealed class StratumDragHandle : TextBlock { }

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
    private readonly string? selectedPath;
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
    private readonly Stack<GroupRow[]> mergeUndo = new();
    private readonly DataGrid groupList;
    private readonly Button undoMergeButton;
    private readonly DataGrid inputGrid,
        filterGrid,
        derivedGrid;
    private Point? groupDragStart;
    private GroupRow? groupDragRow;
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
        "Termina com",
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
        FilterOperator.EndsWith,
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
        this.selectedPath = selectedPath;
        source = Ui.Combo([]);
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
        groupList = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = false,
            RowHeaderWidth = 0,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Height = 310,
            MinRowHeight = 48,
            AllowDrop = true,
            EnableColumnVirtualization = false,
        };
        var groupView = CollectionViewSource.GetDefaultView(groups);
        var groupCellStyle = new Style(typeof(DataGridCell), groupList.TryFindResource(typeof(DataGridCell)) as Style);
        groupCellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 2, 3, 2)));
        groupCellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        groupList.CellStyle = groupCellStyle;
        groupView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GroupRow.Key)));
        groupList.ItemsSource = groupView;
        var groupHeader = new FrameworkElementFactory(typeof(TextBlock));
        groupHeader.SetBinding(TextBlock.TextProperty, new Binding("Items[0].Name") { StringFormat = "Saída · {0}" });
        groupHeader.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        groupHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 8, 4, 5));
        var groupPanel = new FrameworkElementFactory(typeof(StackPanel));
        var groupBorder = new FrameworkElementFactory(typeof(Border));
        groupBorder.SetValue(Border.BackgroundProperty, Brushes.AliceBlue);
        groupBorder.SetValue(Border.BorderBrushProperty, Brushes.SteelBlue);
        groupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 1));
        groupBorder.AppendChild(groupHeader);
        groupPanel.AppendChild(groupBorder);
        groupPanel.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        var container = new Style(typeof(GroupItem));
        container.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GroupItem)) { VisualTree = groupPanel }));
        groupList.GroupStyle.Add(new GroupStyle { ContainerStyle = container });
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(GroupRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        groupList.Columns.Add(new DataGridTemplateColumn { Header = "Usar", Width = 36, MinWidth = 36, CellTemplate = new DataTemplate { VisualTree = check } });
        var values = new FrameworkElementFactory(typeof(TextBlock));
        values.SetBinding(TextBlock.TextProperty, new Binding(nameof(GroupRow.Values)) { Converter = new StratumValuesTooltipConverter() });
        values.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        values.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        groupList.Columns.Add(new DataGridTemplateColumn { Header = "Estratos de origem", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 80, CellTemplate = new DataTemplate { VisualTree = values } });
        var outputName = new FrameworkElementFactory(typeof(TextBox));
        outputName.SetBinding(TextBox.TextProperty, new Binding(nameof(GroupRow.Name))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        outputName.SetValue(TextBox.IsReadOnlyProperty, false);
        outputName.SetValue(FrameworkElement.MinHeightProperty, 32d);
        outputName.SetValue(Control.FontSizeProperty, 13d);
        outputName.SetValue(Control.PaddingProperty, new Thickness(6, 3, 6, 3));
        outputName.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        outputName.SetValue(Control.ForegroundProperty, Brushes.Black);
        outputName.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        outputName.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        outputName.SetValue(FrameworkElement.ToolTipProperty, "Edite o nome da tabela de saída");
        groupList.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Nome da saída", Width = new DataGridLength(1.35, DataGridLengthUnitType.Star), MinWidth = 100,
            CellTemplate = new DataTemplate { VisualTree = outputName },
        });
        groupList.Columns.Add(new DataGridTextColumn { Header = "Linhas", Width = 65, MinWidth = 55, IsReadOnly = true, Binding = new Binding(nameof(GroupRow.Rows)) { StringFormat = "{0:N0}" } });
        groupList.PreviewMouseLeftButtonDown += GroupMouseDown;
        groupList.PreviewMouseMove += GroupMouseMove;
        groupList.PreviewMouseLeftButtonUp += (_, _) => { groupDragStart = null; groupDragRow = null; };
        groupList.DragOver += GroupDragOver;
        groupList.Drop += GroupDrop;
        undoMergeButton = Ui.Button("Desfazer última união", UndoMerge);
        undoMergeButton.IsEnabled = false;
        filterGrid = Grid(135);
        filterGrid.MaxHeight = 135;
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
            Ui.AddHelp(panel, "Exemplo para [Código] = RDBOBA00123P20-001:\nESQUERDA([Código]; 6) → RDBOBA\nMEIO([Código]; 12; 3) → P20\nMEIO([Código]; 12; 1) → P\nSE(MEIO([Código]; 12; 1) = \"P\"; \"Plantio\"; \"Outro\")\nAs posições começam em 1. Use ponto nos decimais. Também: DIREITA, ANTES, DEPOIS, CONTEM, SUBSTITUIR, MAIUSCULA, MINUSCULA, APARAR, TAMANHO, +, -, *, / e & para juntar textos. Em contas (+, -, * e /), se algum valor usado estiver vazio ou for zero, o resultado fica vazio.");
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
            SetPrimaryAction(Ui.AsyncButton("Aplicar colunas na tabela", () => owner.Execute(SaveCalculatedTable)));
            Ui.AddHelp(panel, "Grava as novas colunas na própria tabela DLB selecionada. As outras tabelas do banco são preservadas.");
            }
            if (!calculateColumns)
            {
            Ui.Section(panel,"Filtros e resultados");
            var noFilters = Ui.Text("Nenhum filtro aplicado; todos os registros serão considerados.");
            noFilters.Margin = new Thickness(3, 2, 3, 8);
            filterGrid.Visibility = Visibility.Collapsed;
            var filterArea = new Grid();
            filterArea.Children.Add(noFilters);
            filterArea.Children.Add(filterGrid);
            Ui.Field(panel, "Filtros opcionais (todas as condições)", filterArea);
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
            void UpdateFilterLayout()
            {
                bool hasFilters = filters.Count > 0;
                filterGrid.Visibility = hasFilters ? Visibility.Visible : Visibility.Collapsed;
                noFilters.Visibility = hasFilters ? Visibility.Collapsed : Visibility.Visible;
                filterGrid.Height = Math.Min(135, 32 + filters.Count * 34);
            }
            filters.CollectionChanged += (_, _) => UpdateFilterLayout();
            panel.Children.Add(Ui.AsyncButton("Listar estratos", () => owner.Execute(Discover)));
            Ui.Section(panel, "Estratos encontrados");
            Ui.AddHelp(panel, "Marque os estratos que deseja gerar. Para combinar estratos, arraste a alça ⋮⋮ de um item sobre outro; o item de destino define o nome do resultado. A contagem de linhas será somada.");
            panel.Children.Add(groupList);
            panel.Children.Add(undoMergeButton);
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
        Ui.AddHelp(panel, "A junção cria automaticamente a coluna Fonte com o nome da tabela de origem de cada linha. Se já existir uma coluna Fonte, seus valores são preservados como Fonte_anterior. Cabeçalhos vazios recebem nomes como ‘Coluna 4’ e duplicados ganham sufixos como ‘_2’, mantendo a ordem das colunas.");
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
        if (!calculateColumns) SetPrimaryAction(
            Ui.AsyncButton(stratify ? "Gerar estratos" : "Juntar tabelas", () => owner.Execute(Run))
        );
        panel.Children.Add(status);
        SetForm(panel);
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

    private static DataTemplate GroupTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(DockPanel.LastChildFillProperty, true);
        row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var handle = new FrameworkElementFactory(typeof(StratumDragHandle));
        handle.SetValue(TextBlock.TextProperty, "⋮⋮");
        handle.SetValue(TextBlock.FontSizeProperty, 15d);
        handle.SetValue(TextBlock.WidthProperty, 23d);
        handle.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        handle.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        handle.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(91, 112, 137)));
        handle.SetValue(DockPanel.DockProperty, Dock.Left);
        handle.SetValue(FrameworkElement.ToolTipProperty, "Arraste este estrato sobre outro para combiná-los");
        row.AppendChild(handle);

        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 7, 0));
        check.SetValue(FrameworkElement.ToolTipProperty, "Incluir este estrato na geração");
        check.SetValue(DockPanel.DockProperty, Dock.Left);
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(GroupRow.Selected))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        row.AppendChild(check);

        var details = new FrameworkElementFactory(typeof(StackPanel));
        details.SetValue(DockPanel.DockProperty, Dock.Left);
        details.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        details.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(GroupRow.DisplayValue)));
        value.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(GroupRow.Values))
        {
            Converter = new StratumValuesTooltipConverter(),
        });
        value.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        value.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        details.AppendChild(value);
        var rows = new FrameworkElementFactory(typeof(TextBlock));
        rows.SetBinding(TextBlock.TextProperty, new Binding(nameof(GroupRow.Rows)) { StringFormat = "{0:N0} linhas" });
        rows.SetValue(TextBlock.FontSizeProperty, 11d);
        rows.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(91, 112, 137)));
        details.AppendChild(rows);
        var outputName = new FrameworkElementFactory(typeof(TextBox));
        outputName.SetBinding(TextBox.TextProperty, new Binding(nameof(GroupRow.Name))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        outputName.SetValue(FrameworkElement.MinWidthProperty, 100d);
        outputName.SetValue(FrameworkElement.WidthProperty, 160d);
        outputName.SetValue(FrameworkElement.MinHeightProperty, 32d);
        outputName.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        outputName.SetValue(FrameworkElement.ToolTipProperty, "Nome da tabela de saída");
        outputName.SetValue(DockPanel.DockProperty, Dock.Right);
        row.AppendChild(outputName);
        row.AppendChild(details);

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.White);
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(213, 223, 233)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 2, 2, 2));
        border.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
        border.AppendChild(row);

        return new DataTemplate(typeof(GroupRow)) { VisualTree = border };
    }

    private sealed class StratumValuesTooltipConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is string[] values ? string.Join(" + ", values) : "";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            Binding.DoNothing;
    }

    private void GroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;
        var item = ItemsControl.ContainerFromElement(groupList, e.OriginalSource as DependencyObject) as DataGridRow;
        groupDragRow = item?.Item as GroupRow;
        groupDragStart = groupDragRow is null ? null : e.GetPosition(groupList);
    }

    private void GroupMouseMove(object sender, MouseEventArgs e)
    {
        if (groupDragStart is not Point start || groupDragRow is not GroupRow row || e.LeftButton != MouseButtonState.Pressed)
            return;
        Point current = e.GetPosition(groupList);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        groupDragStart = null;
        groupDragRow = null;
        DragDrop.DoDragDrop(groupList, row, DragDropEffects.Move);
    }

    private void GroupDragOver(object sender, DragEventArgs e)
    {
        var target = GroupAt(e.OriginalSource as DependencyObject);
        e.Effects = e.Data.GetDataPresent(typeof(GroupRow)) && target is not null
            && !ReferenceEquals(target, e.Data.GetData(typeof(GroupRow)))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void GroupDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(GroupRow)) is GroupRow sourceRow && GroupAt(e.OriginalSource as DependencyObject) is GroupRow targetRow)
            MergeGroups(sourceRow, targetRow);
        e.Handled = true;
    }

    private GroupRow? GroupAt(DependencyObject? element) =>
        element is null
            ? null
            : (ItemsControl.ContainerFromElement(groupList, element) as DataGridRow)?.Item as GroupRow;

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void MergeGroups(GroupRow sourceRow, GroupRow targetRow)
    {
        if (ReferenceEquals(sourceRow, targetRow) || !groups.Contains(sourceRow) || !groups.Contains(targetRow))
            return;
        groupList.CommitEdit(DataGridEditingUnit.Cell, true);
        groupList.CommitEdit(DataGridEditingUnit.Row, true);
        mergeUndo.Push(groups.Select(CloneGroup).ToArray());
        undoMergeButton.IsEnabled = true;
        var merged = new GroupRow
        {
            Key = targetRow.Key,
            Keys = targetRow.Keys.Concat(sourceRow.Keys).Distinct(StringComparer.Ordinal).ToArray(),
            Value = targetRow.Value,
            Values = targetRow.Values.Concat(sourceRow.Values).ToArray(),
            Rows = targetRow.Rows + sourceRow.Rows,
            Name = targetRow.Name,
            Selected = targetRow.Selected || sourceRow.Selected,
        };
        groups.Remove(sourceRow);
        int targetIndex = groups.IndexOf(targetRow);
        groups[targetIndex] = merged;
    }

    private void UndoMerge()
    {
        if (mergeUndo.Count == 0)
            return;
        GroupRow[] snapshot = mergeUndo.Pop();
        groups.Clear();
        foreach (var row in snapshot)
            groups.Add(row);
        undoMergeButton.IsEnabled = mergeUndo.Count > 0;
    }

    private static GroupRow CloneGroup(GroupRow row) => new()
    {
        Key = row.Key,
        Keys = [.. row.Keys],
        Value = row.Value,
        Values = [.. row.Values],
        Rows = row.Rows,
        Name = row.Name,
        Selected = row.Selected,
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
        var files = session.Files.Where(session.IsTabular)
            .Select(path => (Path: path, Name: session.ContentName(path), Resolved: session.Resolve(path),
                Compact: Path.GetExtension(path).Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        InputRow[] discovered = await Task.Run(() =>
        {
            var rows = new List<InputRow>();
            var catalogs = new ProjectTableCatalogCache();
            foreach (var file in files)
            {
                if ((stratify || calculateColumns) && file.Compact)
                {
                    foreach (var table in catalogs.Tables(session, file.Path).Where(name =>
                        !name.StartsWith(Dendro.Core.Processing.ProcessingHistory.InternalPrefix, StringComparison.Ordinal)))
                        rows.Add(new() { Path = file.Path, Sheet = table, Name = file.Name + " · " + table });
                    continue;
                }
                rows.Add(new() { Path = file.Path, Name = file.Name, Selected = file.Path == selectedPath });
            }
            return rows.ToArray();
        });
        inputs.Clear();
        foreach (var row in discovered) inputs.Add(row);
        source.ItemsSource = inputs.Select(i => i.Name).ToArray();
        if (selectedPath != null)
        {
            string selectedSheet = session.ExcelOptions(selectedPath).S("sheet");
            source.SelectedIndex = inputs.ToList().FindIndex(i => i.Path == selectedPath &&
                (i.Sheet.Length == 0 || i.Sheet == selectedSheet));
        }
        if (source.SelectedIndex < 0 && inputs.Count > 0) source.SelectedIndex = 0;
        if ((stratify || calculateColumns) && inputs.Count > 0)
            await LoadColumns();
    }

    private async Task LoadColumns()
    {
        var table = Source(CurrentPath);
        sourceColumns = await Task.Run(() =>
        {
            using var cursor = new TableCursor(table);
            return TableTools.NormalizeHeaders(cursor.Columns);
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
        mergeUndo.Clear();
        undoMergeButton.IsEnabled = false;
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
                    Keys = [stratum.Key],
                    Value = stratum.Value,
                    Values = [stratum.Value],
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
            var selected = groups
                .Where(g => g.Selected)
                .Select(g => new StratumOutput(g.Key, g.Name, g.Keys))
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
