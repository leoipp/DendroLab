using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class FrontierView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private JsonObject[] graphs = [];
    private readonly ComboBox source = new(), method = new(), partition = new(), side = new();
    private readonly TextBox bins = Ui.Input("20"), minimum = Ui.Input("3"), percentile = Ui.Input("90"), tail = Ui.Input("10");
    private readonly CheckBox observations = new() { Content = "Mostrar observações do gráfico de origem", IsChecked = true };

    internal FrontierView(MainWindow owner, ProjectSession session, JsonObject? initial = null)
    {
        this.owner = owner;
        this.session = session;
        var form = Ui.Form();
        form.Children.Add(owner.ToolHeader("Fronteira de eficiência"));
        Ui.Section(form,"Gráfico e método");
        Ui.Field(form, "Gráfico de origem", source);
        side.ItemsSource = new[] { "Superior", "Inferior", "Superior e inferior" }; side.SelectedIndex = 0;
        method.ItemsSource = new[] { "Máximo / mínimo observado", "Percentil de Y", "Média dos extremos da cauda" }; method.SelectedIndex = 0;
        partition.ItemsSource = new[] { "Intervalos iguais em X", "Quantidade igual de observações" }; partition.SelectedIndex = 0;
        Ui.Field(form, "Fronteira", side);
        Ui.Field(form, "Método de cálculo", method);
        Ui.Field(form, "Divisão das faixas", partition);
        Ui.Field(form, "Número de faixas (1–1.000)", bins);
        Ui.Field(form, "Mínimo de observações por faixa", minimum);
        Ui.Field(form, "Percentil superior (50–100; inferior = 100 − percentil)", percentile);
        Ui.Field(form, "Percentual da cauda (> 0 até 50%)", tail);
        void MethodChanged() { percentile.IsEnabled = method.SelectedIndex == 1; tail.IsEnabled = method.SelectedIndex == 2; }
        method.SelectionChanged += (_, _) => MethodChanged(); MethodChanged();
        form.Children.Add(observations);
        Ui.Section(form,"Gerar resultado");
        Ui.AddHelp(form, "Usa X, Y, estratos, filtro de grupo e exclusões do gráfico. Máximos/mínimos mantêm o X observado; percentis usam o X médio da faixa; médias de cauda usam o X médio dos pontos selecionados. Faixas insuficientes são omitidas.");
        form.Children.Add(Ui.AsyncButton("Calcular fronteira", () => owner.Execute(Calculate)));
        Content = form;
        void RefreshSources()
        {
            string? selected = source.SelectedIndex >= 0 && source.SelectedIndex < graphs.Length ? ChartView.Identity(graphs[source.SelectedIndex]) : null;
            graphs = session.Meta.Arr("charts").OfType<JsonObject>().Select(g => g.DeepClone().AsObject()).ToArray();
            source.ItemsSource = graphs.Select(g => session.ContentName(g.S("path"), g) + " · " + g.S("sheet")).ToArray();
            source.SelectedIndex = Math.Max(0, Array.FindIndex(graphs, g => ChartView.Identity(g) == selected));
        }
        RefreshSources();
        source.DropDownOpened += (_, _) => RefreshSources();
        if (initial != null) source.SelectedIndex = Array.FindIndex(graphs, g => ChartView.Identity(g) == ChartView.Identity(initial));
    }

    internal async Task Calculate()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= graphs.Length)
            throw new InvalidOperationException("Crie e selecione um gráfico de origem.");
        var origin = session.Meta.Arr("charts").OfType<JsonObject>().FirstOrDefault(g =>
            ChartView.Identity(g) == ChartView.Identity(graphs[source.SelectedIndex]))
            ?? throw new InvalidOperationException("O gráfico de origem foi removido.");
        int Integer(TextBox input)
        {
            double value = Ui.Number(input.Text);
            if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 1 || value > int.MaxValue)
                throw new InvalidOperationException("Faixas e mínimo por faixa devem ser números inteiros positivos.");
            return (int)value;
        }
        var options = new FrontierOptions((FrontierMethod)method.SelectedIndex, (FrontierPartition)partition.SelectedIndex,
            Integer(bins), Integer(minimum), side.SelectedIndex + 1,
            method.SelectedIndex == 1 ? Ui.Number(percentile.Text) : 90,
            method.SelectedIndex == 2 ? Ui.Number(tail.Text) : 10, observations.IsChecked == true);
        var definition = new JsonObject {
            ["kind"] = "frontier", ["path"] = origin.S("path"), ["sheet"] = origin.S("sheet"),
            ["header_row"] = origin.N("header_row", 1), ["chart"] = origin.DeepClone(),
            ["options"] = JsonSerializer.SerializeToNode(options),
        };
        var report = await AnalysisDocuments.Compute(session, definition);
        AnalysisDocuments.Show(owner, session, report, definition);
        owner.Log($"Fronteira calculada: {report.Summary.N("points"):N0} pontos; {report.Summary.N("skipped_bins"):N0} faixas omitidas.");
    }
}
