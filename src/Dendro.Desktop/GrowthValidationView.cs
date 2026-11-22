using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Configuration;
using Dendro.Core.Statistics;

namespace Dendro.Desktop;

internal sealed class GrowthValidationView : ToolFormView
{
    internal sealed class CurveChoice
    {
        public string Id { get; init; } = "";
        public bool Selected { get; set; }
        public string PlotGroup { get; set; } = "";
        public string Name { get; init; } = "";
        public string Unit => FromChart ? "Gráfico" : Curve.AgeUnit;
        public GrowthCurve Curve { get; init; } = null!;
        public bool FromChart { get; init; }
        public string Path { get; init; } = "";
        public string Issue { get; init; } = "";
    }

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ObservableCollection<CurveChoice> choices = new();
    private readonly DataGrid grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = false,
        CanUserAddRows = false,
        Height = 310,
        MinRowHeight = 36,
        RowHeaderWidth = 0,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        EnableColumnVirtualization = false,
        MinColumnWidth = 45,
        AllowDrop = true,
    };
    private Point? dragStart;
    private CurveChoice? dragChoice;
    private readonly TextBox minimum = Ui.Input("0,5"),
        maximum = Ui.Input("15");
    private readonly ComboBox method = Ui.Combo([
            "ICA instantâneo (derivada da curva)",
            "ICA anual (diferença em 1 ano)",
        ]);

    public GrowthValidationView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;

        var form = Ui.Form();
        SetForm(form);
        form.Children.Add(owner.ToolHeader("ICA e IMA"));
        Ui.AddHelp(form, "Selecione modelos cadastrados em Crescimento e produção. As idades do intervalo e os incrementos do resultado são expressos em anos.");
        Ui.Section(form,"Curvas de origem");
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        check.SetValue(Control.PaddingProperty, new Thickness(0));
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Selected")
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Usar",
            CellTemplate = new DataTemplate { VisualTree = check },
            Width = 45,
        });
        grid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Curva",
                Binding = new Binding("Name"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 170,
            }
        );
        grid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Gráfico",
                Binding = new Binding("PlotGroup"),
                IsReadOnly = true,
                Width = 85,
            }
        );
        grid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Idade",
                Binding = new Binding("Unit"),
                IsReadOnly = true,
                Width = 70,
            }
        );
        var view = CollectionViewSource.GetDefaultView(choices);
        view.GroupDescriptions.Add(new PropertyGroupDescription("PlotGroup"));
        grid.ItemsSource = view;
        var groupHeader = new FrameworkElementFactory(typeof(TextBlock));
        groupHeader.SetBinding(TextBlock.TextProperty, new Binding("Name") { StringFormat = "{0} — modelos no mesmo gráfico" });
        groupHeader.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        groupHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 8, 4, 5));
        var groupPanel = new FrameworkElementFactory(typeof(StackPanel));
        var headerBorder = new FrameworkElementFactory(typeof(Border));
        headerBorder.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.AliceBlue);
        headerBorder.SetValue(Border.BorderBrushProperty, System.Windows.Media.Brushes.SteelBlue);
        headerBorder.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 1));
        headerBorder.AppendChild(groupHeader);
        groupPanel.AppendChild(headerBorder);
        groupPanel.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        var groupStyle = new Style(typeof(GroupItem));
        groupStyle.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(GroupItem)) { VisualTree = groupPanel }));
        grid.GroupStyle.Add(new GroupStyle { ContainerStyle = groupStyle });
        grid.PreviewMouseLeftButtonDown += GridMouseDown;
        grid.PreviewMouseMove += GridMouseMove;
        grid.PreviewMouseLeftButtonUp += (_, _) => { dragStart = null; dragChoice = null; };
        grid.DragOver += GridDragOver;
        grid.Drop += GridDrop;
        form.Children.Add(grid);
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Button("Selecionar todos", () =>
        {
            foreach (var choice in choices) choice.Selected = true;
            grid.Items.Refresh();
        }));
        actions.Children.Add(Ui.Button("Atualizar curvas disponíveis", Refresh));
        form.Children.Add(actions);
        Ui.AddHelp(form, "Cada curva começa em um gráfico separado. Arraste uma curva sobre outra para reuni-las; ao arrastar uma selecionada, as curvas selecionadas do mesmo gráfico também são movidas.");
        Ui.Section(form,"Intervalo e cálculo");
        var ages = new Grid();
        ages.ColumnDefinitions.Add(new ColumnDefinition());
        ages.ColumnDefinitions.Add(new ColumnDefinition());
        var minimumPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var maximumPanel = new StackPanel();
        Ui.Field(minimumPanel, "Idade mínima (anos)", minimum);
        Ui.Field(maximumPanel, "Idade máxima (anos)", maximum);
        Grid.SetColumn(maximumPanel, 1);
        ages.Children.Add(minimumPanel);
        ages.Children.Add(maximumPanel);
        form.Children.Add(ages);
        Ui.Field(form, "Definição do ICA", method);
        Ui.AddHelp(form, "IMA = produção / idade. O cruzamento do ICA instantâneo com o IMA pode ser máximo ou mínimo; ausência de cruzamento vale apenas para o intervalo informado.");
        SetPrimaryAction(Ui.AsyncButton("Analisar ICA e IMA", () => owner.Execute(Run)));
        Refresh();
    }

    private void Refresh()
    {
        choices.Clear();
        int index = 1;
        foreach (var choice in Available(session, includeChartFits: false))
        {
            choice.PlotGroup = $"Gráfico {index++}";
            choices.Add(choice);
        }
    }

    private void GridMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
        dragChoice = row?.Item as CurveChoice;
        dragStart = dragChoice is null ? null : e.GetPosition(grid);
    }

    private void GridMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragStart is not Point start || dragChoice is not CurveChoice source || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;
        Point current = e.GetPosition(grid);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        dragStart = null;
        dragChoice = null;
        DragDrop.DoDragDrop(grid, source, DragDropEffects.Move);
    }

    private void GridDragOver(object sender, DragEventArgs e)
    {
        var target = ChoiceAt(e.OriginalSource as DependencyObject);
        e.Effects = e.Data.GetDataPresent(typeof(CurveChoice)) && target is not null
            && !ReferenceEquals(target, e.Data.GetData(typeof(CurveChoice)))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void GridDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CurveChoice)) is CurveChoice source && ChoiceAt(e.OriginalSource as DependencyObject) is CurveChoice target)
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            string from = source.PlotGroup;
            string to = target.PlotGroup;
            var moving = source.Selected
                ? choices.Where(c => c.Selected && c.PlotGroup == from).ToArray()
                : [source];
            foreach (var choice in moving) choice.PlotGroup = to;
            CollectionViewSource.GetDefaultView(choices).Refresh();
        }
        e.Handled = true;
    }

    private CurveChoice? ChoiceAt(DependencyObject? element) => element is null
        ? null
        : (ItemsControl.ContainerFromElement(grid, element) as DataGridRow)?.Item as CurveChoice;

    internal static CurveChoice[] Available(ProjectSession session, bool includeChartFits = true)
    {
        var choices = new List<CurveChoice>();
        foreach (var chart in (includeChartFits ? session.Meta.Arr("charts").OfType<JsonObject>() : Enumerable.Empty<JsonObject>()))
        {
            var fits = chart["fits"] as JsonArray ?? [];
            for (int fitIndex = 0; fitIndex < fits.Count; fitIndex++)
            {
                var fit = fits[fitIndex] as JsonObject;
                if (fit is null || !fit.B("converged")) continue;
                string name = session.ContentName(chart.S("path"), chart) + " · " + fit.S("group") + " · " + fit.S("model");
                var fixedInputs = new Dictionary<string, double>(StringComparer.Ordinal);
                int predictorCount = (int)fit.N("predictor_count", 1);
                for (int predictor = 1; predictor < predictorCount; predictor++)
                {
                    string symbol = "x" + (predictor + 1);
                    if (fit[$"{symbol}_mean"] is not null && double.IsFinite(fit.N($"{symbol}_mean")))
                        fixedInputs[symbol] = fit.N($"{symbol}_mean");
                }
                string formula = System.Text.RegularExpressions.Regex.Replace(fit.S("formula"), @"\b(?:x1|x|t)\b", "t");
                choices.Add(new()
                {
                    Id = ChartView.Identity(chart) + ":fit:" + fitIndex,
                    Name = name,
                    Path = chart.S("path"),
                    FromChart = true,
                    Curve = new(name, formula,
                        Enumerable.Range(0, (int)fit.N("parameters", 2)).Select(i => fit.N("b" + i)).ToArray(),
                        "anos", "t", 1, fixedInputs),
                });
            }
        }
        var equations = session
                .Data!.Obj("curvas_crescimento")
                .Arr("equacoes")
                .OfType<JsonObject>()
                .ToArray();
        for (int equationIndex = 0; equationIndex < equations.Length; equationIndex++)
        {
            var model = equations[equationIndex];
            var inputs = ModelDefinition.Inputs(model, true);
            var ageInputs = inputs.OfType<JsonObject>().Where(i => i.S("simbolo") == "t").ToArray();
            var input = ageInputs.Length == 1 ? ageInputs[0] : null;
            var fixedInputs = new Dictionary<string, double>(StringComparer.Ordinal);
            var savedFixedInputs = model["fixed_inputs"] as JsonObject;
            foreach (var extra in inputs.OfType<JsonObject>().Where(i => i.S("simbolo") != "t"))
                if (savedFixedInputs?[extra.S("simbolo")] is not null && double.IsFinite(savedFixedInputs.N(extra.S("simbolo"))))
                    fixedInputs[extra.S("simbolo")] = savedFixedInputs.N(extra.S("simbolo"));
            string unit = input?.S("coluna") switch
            {
                "idade_meses" => "meses",
                "idade_anos" => "anos",
                _ => input?.S("unidade") ?? model.S("unidade_idade", "meses"),
            };
            string issue =
                input is null || unit is not ("anos" or "meses")
                    ? "ICA/IMA requer uma entrada t de idade, em anos ou meses."
                    : inputs.OfType<JsonObject>().Any(i => i.S("simbolo") != "t" && !fixedInputs.ContainsKey(i.S("simbolo")))
                        ? "As entradas adicionais precisam de valores fixos para comparar curvas."
                    : "";
            string name =
                model.S("codigo")
                + " · "
                + ModelDefinition.Name(model, true)
                + " · "
                + model.N("ano")
                + (model.B("vigente") ? " · vigente" : "");
            choices.Add(
                new()
                {
                    Id = $"model:{model.S("codigo")}:{model.S("modelo")}:{model.N("ano")}:{equationIndex}",
                    Name = name,
                    Issue = issue,
                    Curve = new(
                        name,
                        ModelDefinition.FormulaText(model, true),
                        Enumerable
                            .Range(0, model.ContainsKey("b3") ? 4 : 3)
                            .Select(i => model.N("b" + i))
                            .ToArray(),
                        unit,
                        input?.S("simbolo", "t") ?? "t",
                        input?.N("fator", 1) ?? 1,
                        fixedInputs
                    ),
                }
            );
        }
        return choices.ToArray();
    }

    private async Task Run()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = choices.Where(c => c.Selected).ToArray();
        if (selected.Length is < 1 or > 12)
            throw new InvalidOperationException("Selecione de 1 a 12 curvas.");
        if (selected.Any(c => c.Issue.Length > 0))
            throw new InvalidOperationException(selected.First(c => c.Issue.Length > 0).Issue);
        owner.Progress.IsIndeterminate = true;
        var results = new List<(AnalysisReport Report, JsonObject Definition)>();
        foreach (var group in selected.GroupBy(c => c.PlotGroup))
        {
            string title = "ICA e IMA · " + string.Join(" / ", group.Select(c => c.Name));
            var definition = new JsonObject
            {
                ["kind"] = "increments",
                ["path"] = "",
                ["title"] = title,
                ["curves"] = JsonSerializer.SerializeToNode(group.Select(c => c.Curve).ToArray()),
                ["plot_groups"] = new JsonArray(group.Select(c => (JsonNode?)JsonValue.Create(c.PlotGroup)).ToArray()),
                ["min_age"] = Ui.Number(minimum.Text),
                ["max_age"] = Ui.Number(maximum.Text),
                ["annual"] = method.SelectedIndex == 1,
            };
            results.Add((await AnalysisDocuments.Compute(session, definition), definition));
        }
        foreach (var result in results)
            AnalysisDocuments.Show(owner, session, result.Report, result.Definition);
        owner.Log($"ICA e IMA: {results.Count} gráfico(s), cada um com diagnóstico separado.");
    }
}
