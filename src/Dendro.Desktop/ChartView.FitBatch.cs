using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal sealed partial class ChartView
{
    private sealed record FitBatchTarget(JsonObject Chart, CheckBox Check);
    private sealed record FitBatchModel(JsonObject Model, CheckBox Check);

    private async Task GenerateFitBatch()
    {
        if (!fitting)
            throw new InvalidOperationException("O ajuste em lote está disponível em Ajuste de regressão.");

        _ = Collect();
        var charts = session.Meta.Arr("charts").OfType<JsonObject>()
            .Select(chart => chart.DeepClone().AsObject()).ToArray();
        if (charts.Length == 0)
            throw new InvalidOperationException("Crie ao menos um gráfico antes de iniciar o ajuste em lote.");

        var targetChecks = new List<FitBatchTarget>();
        foreach (var chart in charts)
        {
            var check = new CheckBox
            {
                Content = new TextBlock { Text = GraphLabel(chart), TextWrapping = TextWrapping.Wrap },
                IsChecked = Identity(chart) == Identity(config),
                Margin = new Thickness(4, 3, 4, 3),
            };
            targetChecks.Add(new(chart, check));
        }

        var modelChecks = (metadata["models"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(model => new FitBatchModel(
                model.DeepClone().AsObject(),
                new CheckBox
                {
                    Content = $"{model.S("name")} ({(int)model.N("predictors", 1)}P)",
                    Margin = new Thickness(4, 3, 4, 3),
                }
            )).ToArray();
        if (modelChecks.Length == 0)
            throw new InvalidOperationException("Não há modelos de regressão cadastrados.");

        string[] columns = x2.Items.Cast<object>().Select(item => item.ToString() ?? "")
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var x2Batch = Ui.Combo(columns, x2.SelectedItem?.ToString());
        var x3Batch = Ui.Combo(columns, x3.SelectedItem?.ToString());
        var x2FieldBatch = new StackPanel();
        Ui.Field(x2FieldBatch, "Variável X2 usada nos modelos 2P e 3P", x2Batch);
        var x3FieldBatch = new StackPanel();
        Ui.Field(x3FieldBatch, "Variável X3 usada nos modelos 3P", x3Batch);

        var showCurrentBatch = new CheckBox
        {
            Content = "Mostrar curva vigente do catálogo em cada gráfico",
            IsChecked = config.B("show_current_curve"),
            Margin = new Thickness(2, 5, 2, 5),
        };
        var currentSourceBatch = Ui.Combo(["Regressão salva", "Índice de sítio", "Crescimento e produção"]);
        currentSourceBatch.SelectedIndex = Math.Clamp((int)config.N("current_curve_source"), 0, 2);
        currentSourceBatch.IsEnabled = showCurrentBatch.IsChecked == true;
        showCurrentBatch.Checked += (_, _) => currentSourceBatch.IsEnabled = true;
        showCurrentBatch.Unchecked += (_, _) => currentSourceBatch.IsEnabled = false;

        var count = Ui.Text("");
        var message = Ui.Text("");
        void UpdateBatchSelection()
        {
            var selectedModels = modelChecks.Where(item => item.Check.IsChecked == true).ToArray();
            int maxPredictors = selectedModels.Select(item => (int)item.Model.N("predictors", 1)).DefaultIfEmpty(1).Max();
            x2FieldBatch.Visibility = maxPredictors >= 2 ? Visibility.Visible : Visibility.Collapsed;
            x3FieldBatch.Visibility = maxPredictors >= 3 ? Visibility.Visible : Visibility.Collapsed;
            count.Text = $"{targetChecks.Count(item => item.Check.IsChecked == true)} gráfico(s) · {selectedModels.Length} modelo(s) selecionado(s).";
        }
        foreach (var item in modelChecks)
        {
            item.Check.Checked += (_, _) =>
            {
                message.Text = "";
                UpdateBatchSelection();
            };
            item.Check.Unchecked += (_, _) => UpdateBatchSelection();
        }
        foreach (var item in targetChecks)
        {
            item.Check.Checked += (_, _) => UpdateBatchSelection();
            item.Check.Unchecked += (_, _) => UpdateBatchSelection();
        }

        var modelList = new StackPanel();
        foreach (var item in modelChecks)
            modelList.Children.Add(item.Check);
        var graphList = new StackPanel();
        foreach (var item in targetChecks)
            graphList.Children.Add(item.Check);
        var modelBox = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(213, 223, 233)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = System.Windows.Media.Brushes.White,
            Padding = new Thickness(8),
            Child = Ui.Scroll(modelList),
            Height = 190,
        };
        var graphBox = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(213, 223, 233)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = System.Windows.Media.Brushes.White,
            Padding = new Thickness(8),
            Child = Ui.Scroll(graphList),
            Height = 160,
        };
        var form = Ui.Form();
        Ui.Section(form, "Configurar ajuste em lote");
        Ui.AddHelp(form, "Selecione os modelos e os gráficos que deseja ajustar. Cada resultado será aberto como um documento de Ajuste de regressão; eixos, filtros e grupos vêm do gráfico de origem.");
        Ui.Field(form, "Modelos", modelBox);
        Ui.Section(form, "Gráficos de origem");
        var selection = new StackPanel { Orientation = Orientation.Horizontal };
        selection.Children.Add(Ui.Button("Marcar gráficos", () =>
        {
            foreach (var target in targetChecks)
                target.Check.IsChecked = true;
        }));
        selection.Children.Add(Ui.Button("Limpar gráficos", () =>
        {
            foreach (var target in targetChecks)
                target.Check.IsChecked = false;
        }));
        form.Children.Add(selection);
        Ui.Field(form, "Gráficos selecionáveis", graphBox);
        Ui.Section(form, "Variáveis dos modelos");
        form.Children.Add(x2FieldBatch);
        form.Children.Add(x3FieldBatch);
        Ui.Section(form, "Curva de referência vigente");
        form.Children.Add(showCurrentBatch);
        Ui.Field(form, "Catálogo", currentSourceBatch);
        Ui.AddHelp(form, "O nome de cada gráfico em Conteúdos deve corresponder ao ID do modelo. Em Regressão salva, usa-se a versão salva mais recente com ID ou nome correspondente.");
        form.Children.Add(count);
        form.Children.Add(message);
        Window? dialog = null;
        var cancel = Ui.Button("Cancelar", () => dialog!.DialogResult = false);
        cancel.IsCancel = true;
        var run = Ui.Button("Ajustar gráficos selecionados", () =>
        {
            int selectedGraphs = targetChecks.Count(item => item.Check.IsChecked == true);
            int selectedModels = modelChecks.Count(item => item.Check.IsChecked == true);
            if (selectedGraphs == 0)
                message.Text = "Selecione ao menos um gráfico.";
            else if (selectedModels == 0)
                message.Text = "Selecione ao menos um modelo.";
            else if (modelChecks.Any(item => item.Check.IsChecked == true && item.Model.N("predictors", 1) >= 2)
                && x2Batch.SelectedItem is not string)
                message.Text = "Selecione a variável X2.";
            else if (modelChecks.Any(item => item.Check.IsChecked == true && item.Model.N("predictors", 1) >= 3)
                && x3Batch.SelectedItem is not string)
                message.Text = "Selecione a variável X3.";
            else dialog!.DialogResult = true;
        });
        run.IsDefault = true;
        dialog = Ui.Dialog("Ajuste de regressão em lote", Ui.WithToolbar(Ui.Scroll(form), cancel, run), 760, 760);
        UpdateBatchSelection();
        if (dialog.ShowDialog() != true)
            return;

        var selectedTargets = targetChecks.Where(item => item.Check.IsChecked == true).ToArray();
        var selected = modelChecks.Where(item => item.Check.IsChecked == true)
            .Select(item => item.Model).ToArray();
        int predictorCount = selected.Max(item => (int)item.N("predictors", 1));
        int[] predictorCounts = selected.Select(item => (int)item.N("predictors", 1)).Distinct().Order().ToArray();
        string[] modelIds = selected.Select(item => item.S("id")).ToArray();
        var cancellation = owner.OperationCancellation;
        owner.StartTableOperation("Preparando ajuste de regressão em lote…");
        var completed = new List<JsonObject>();
        int failures = 0;

        for (int i = 0; i < selectedTargets.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var target = selectedTargets[i].Chart;
            owner.StatusText.Text = $"Ajustando gráfico {i + 1}/{selectedTargets.Length}: {GraphLabel(target)}";
            try
            {
                var chart = target.DeepClone().AsObject();
                var xVariables = new JsonArray(chart.S("x"));
                if (predictorCount >= 2)
                    xVariables.Add(x2Batch.SelectedItem?.ToString());
                if (predictorCount >= 3)
                    xVariables.Add(x3Batch.SelectedItem?.ToString());
                if (xVariables.Select(item => item?.GetValue<string>() ?? "")
                    .Distinct(StringComparer.Ordinal).Count() != predictorCount)
                    throw new InvalidOperationException("X1, X2 e X3 devem ser colunas diferentes.");
                chart["x_variables"] = xVariables;
                chart["predictor_count"] = predictorCount;
                chart["fit_predictors"] = new JsonArray(predictorCounts.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                chart["fit_model_ids"] = new JsonArray(modelIds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                chart["fitting"] = true;
                chart["show_current_curve"] = showCurrentBatch.IsChecked == true;
                chart["current_curve_source"] = Math.Max(0, currentSourceBatch.SelectedIndex);
                chart["frontier"] = 0;
                chart["selected_fit"] = -1;
                chart["selected_fits"] = new JsonArray();
                chart.Remove("fits");
                chart.Remove("fit_signature");
                var request = chart.DeepClone().AsObject();
                request["path"] = session.Resolve(chart.S("path"));
                var result = await WorkspaceOperations.Run(new()
                {
                    ["op"] = "fit",
                    ["project"] = session.Data!.DeepClone(),
                    ["chart"] = request,
                    ["model"] = -2,
                }, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                var fitted = result["result"]!["chart"]!.DeepClone().AsObject();
                fitted["path"] = target.S("path");
                fitted["fitting"] = true;
                fitted["x_variables"] = xVariables.DeepClone();
                fitted["predictor_count"] = predictorCount;
                fitted["show_current_curve"] = showCurrentBatch.IsChecked == true;
                fitted["current_curve_source"] = Math.Max(0, currentSourceBatch.SelectedIndex);
                var displayModel = (metadata["models"] as JsonArray ?? []).OfType<JsonObject>()
                    .FirstOrDefault(model => model.N("predictors", 1) == predictorCount);
                if (displayModel is not null)
                {
                    fitted["model"] = 0;
                    fitted["model_id"] = displayModel.S("id");
                    fitted["formula"] = displayModel.S("formula");
                    fitted["parameters"] = displayModel.N("parameters", 2);
                }
                if (fitted["fits"] is JsonArray fits)
                {
                    fitted["selected_fits"] = new JsonArray(Enumerable.Range(0, fits.Count)
                        .Select(index => (JsonNode?)JsonValue.Create(index)).ToArray());
                    fitted["selected_fit"] = fits.Count > 0 ? 0 : -1;
                }
                session.StoreChart(fitted);
                completed.Add(fitted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                owner.Log($"Falha no ajuste de {GraphLabel(target)}: {ex.Message}");
            }
            owner.SetProgress((i + 1) * 92.0 / selectedTargets.Length);
        }

        if (completed.Count == 0)
            throw new InvalidOperationException($"Nenhum gráfico foi ajustado. {failures} falha(s); consulte o Histórico.");
        for (int i = 0; i < completed.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            await owner.OpenChart(completed[i].S("path"), true, completed[i], false);
            owner.SetProgress(92 + (i + 1) * 8.0 / completed.Count);
        }
        owner.StatusText.Text = $"Ajuste em lote concluído: {completed.Count} gráfico(s) aberto(s), {failures} falha(s).";
        owner.Log(owner.StatusText.Text);
    }
}
