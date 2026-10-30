using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Configuration;
using Dendro.Core.Statistics;

namespace Dendro.Desktop;

internal sealed class GrowthValidationView : ScrollViewer
{
    internal sealed class CurveChoice
    {
        public bool Selected { get; set; }
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
        Height = 255,
        EnableColumnVirtualization = false,
        MinColumnWidth = 45,
    };
    private readonly TextBox minimum = Ui.Input("0,5"),
        maximum = Ui.Input("15");
    private readonly ComboBox units = Ui.Combo(["anos", "meses"]),
        method = Ui.Combo([
            "ICA instantâneo (derivada da curva)",
            "ICA anual (diferença em 1 ano)",
        ]);
    private readonly CheckBox confirmAge = new()
    {
        Content =
            "Nos ajustes de gráficos selecionados, X representa idade e Y representa produção acumulada.",
        IsChecked = false,
    };

    public GrowthValidationView(
        MainWindow owner,
        ProjectSession session,
        JsonObject? initialChart = null
    )
    {
        this.owner = owner;
        this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var form = Ui.Form();
        Content = form;
        form.Children.Add(owner.ToolHeader("ICA e IMA"));
        Ui.AddHelp(form, "Selecione curvas ajustadas ou versões cadastradas em Crescimento e produção. As idades do intervalo e os incrementos do resultado são expressos em anos.");
        grid.Columns.Add(
            new DataGridCheckBoxColumn
            {
                Header = "Usar",
                Binding = new Binding("Selected"),
                Width = 45,
            }
        );
        grid.Columns.Add(
            new DataGridTextColumn
            {
                Header = "Curva",
                Binding = new Binding("Name"),
                IsReadOnly = true,
                Width = 240,
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
        grid.ItemsSource = choices;
        form.Children.Add(grid);
        form.Children.Add(Ui.Button("Atualizar curvas disponíveis", Refresh));
        Ui.Field(form, "Unidade de X nos ajustes de gráficos", units);
        form.Children.Add(confirmAge);
        Ui.Field(form, "Idade mínima (anos)", minimum);
        Ui.Field(form, "Idade máxima (anos)", maximum);
        Ui.Field(form, "Definição do ICA", method);
        Ui.AddHelp(form, "IMA = produção / idade. O cruzamento do ICA instantâneo com o IMA pode ser máximo ou mínimo; ausência de cruzamento vale apenas para o intervalo informado.");
        form.Children.Add(Ui.AsyncButton("Analisar ICA e IMA", () => owner.Execute(Run)));
        Refresh();
        if (initialChart != null)
        {
            var fits = initialChart["fits"] as JsonArray ?? [];
            string prefix = session.ContentName(initialChart.S("path"), initialChart) + " · ";
            var selectedNames = Dendro
                .Core.Regression.FitDisplay.Visible(initialChart)
                .Select(i => prefix + fits[i].S("group") + " · " + fits[i].S("model"))
                .ToHashSet();
            foreach (var choice in choices)
                choice.Selected =
                    choice.FromChart
                    && choice.Path == initialChart.S("path")
                    && selectedNames.Contains(choice.Name);
        }
    }

    private void Refresh()
    {
        choices.Clear();
        foreach (var choice in Available(session)) choices.Add(choice);
    }

    internal static CurveChoice[] Available(ProjectSession session)
    {
        var choices = new List<CurveChoice>();
        foreach (var chart in session.Meta.Arr("charts").OfType<JsonObject>())
        foreach (var fit in chart["fits"] as JsonArray ?? [])
        {
            if (!fit.B("converged"))
                continue;
            string name =
                session.ContentName(chart.S("path"), chart)
                + " · "
                + fit.S("group")
                + " · "
                + fit.S("model");
            choices.Add(
                new()
                {
                    Name = name,
                    Path = chart.S("path"),
                    FromChart = true,
                    Curve = new(
                        name,
                        fit.S("formula"),
                        Enumerable
                            .Range(0, (int)fit.N("parameters", 2))
                            .Select(i => fit.N("b" + i))
                            .ToArray(),
                        "anos"
                    ),
                }
            );
        }
        foreach (
            var model in session
                .Data!.Obj("curvas_crescimento")
                .Arr("equacoes")
                .OfType<JsonObject>()
        )
        {
            var inputs = ModelDefinition.Inputs(model, true);
            var input = inputs.Count == 1 ? inputs[0] : null;
            string unit = input?.S("coluna") switch
            {
                "idade_meses" => "meses",
                "idade_anos" => "anos",
                _ => input?.S("unidade") ?? model.S("unidade_idade", "meses"),
            };
            string issue =
                inputs.Count != 1 || unit is not ("anos" or "meses")
                    ? "ICA/IMA requer uma única entrada de idade, em anos ou meses."
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
                        input?.N("fator", 1) ?? 1
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
        if (selected.Any(c => c.FromChart) && confirmAge.IsChecked != true)
            throw new InvalidOperationException(
                "Confirme que X é idade e Y é produção acumulada nos gráficos selecionados."
            );
        var curves = selected
            .Select(c =>
                c.FromChart
                    ? c.Curve with
                    {
                        AgeUnit = units.SelectedItem?.ToString() ?? "anos",
                    }
                    : c.Curve
            )
            .ToArray();
        string path =
            selected.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? selected[0].Path
                : "";
        var definition = new JsonObject
        {
            ["kind"] = "increments",
            ["path"] = path,
            ["curves"] = JsonSerializer.SerializeToNode(curves),
            ["min_age"] = Ui.Number(minimum.Text),
            ["max_age"] = Ui.Number(maximum.Text),
            ["annual"] = method.SelectedIndex == 1,
        };
        owner.Progress.IsIndeterminate = true;
        var report = await AnalysisDocuments.Compute(session, definition);
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log("Diagnóstico ICA e IMA concluído.");
    }
}
