using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed partial class ChartView
{
    private sealed record BatchTarget(string Path, string Sheet, int HeaderRow, CheckBox Check);

    private async Task GenerateBatch()
    {
        var template = Collect();
        if (template.S("x").Length == 0 || template.S("y").Length == 0)
            throw new InvalidOperationException("Selecione X e Y antes de gerar o lote.");
        owner.StartTableOperation("Lendo tabelas disponíveis para gráficos…");
        var cancellation = owner.OperationCancellation;
        var targets = new List<BatchTarget>();
        var choices = new StackPanel();
        var required = new[] { template.S("x"), template.S("y"), template.S("stratum") }
            .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string path in session.Files.Where(session.IsTabular).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var result = await WorkspaceOperations.Run(new() { ["op"] = "sheets", ["path"] = session.Resolve(path) });
                foreach (string sheet in result["result"]!.AsArray().Select(s => s.Display()))
                {
                    cancellation.ThrowIfCancellationRequested();
                    int header = session.IsSpreadsheet(path) ? (int)session.ExcelOptions(path).N("header_row", 1) : 1;
                    var missing = await Task.Run(() => {
                        using var cursor = new TableCursor(new(session.Resolve(path), sheet, header));
                        return required.Except(cursor.Columns, StringComparer.Ordinal).ToArray();
                    });
                    string label = session.ContentName(path) + " · " + sheet;
                    var check = new CheckBox {
                        Content = new TextBlock { Text = label + (missing.Length > 0 ? " — faltam: " + string.Join(", ", missing) : ""), TextWrapping = TextWrapping.Wrap },
                        IsEnabled = missing.Length == 0, IsChecked = missing.Length == 0 && path == template.S("path") && sheet == template.S("sheet"),
                    };
                    choices.Children.Add(check);
                    targets.Add(new(path, sheet, header, check));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner.Log($"Não foi possível listar {session.ContentName(path)}: {ex.Message}");
            }
        }
        cancellation.ThrowIfCancellationRequested();
        if (!targets.Any(t => t.Check.IsEnabled))
            throw new InvalidOperationException("Nenhuma tabela contém as colunas necessárias para esse gráfico.");

        var panel = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(Ui.Text($"Gráficos em lote · {template.S("y")} × {template.S("x")}", 16));
        Ui.AddHelp(top, "Selecione as tabelas. O lote usa os mesmos eixos, filtros e aparência. Cada título inclui a tabela e sua aba de origem. Títulos já existentes recebem um número para preservar os gráficos anteriores.");
        var count = Ui.Text("");
        void UpdateCount() => count.Text = $"{targets.Count(t => t.Check.IsChecked == true)} tabela(s) selecionada(s).";
        foreach (var target in targets) { target.Check.Checked += (_, _) => UpdateCount(); target.Check.Unchecked += (_, _) => UpdateCount(); }
        var selection = new WrapPanel();
        selection.Children.Add(Ui.Button("Marcar compatíveis", () => { foreach (var target in targets.Where(t => t.Check.IsEnabled)) target.Check.IsChecked = true; }));
        selection.Children.Add(Ui.Button("Limpar seleção", () => { foreach (var target in targets) target.Check.IsChecked = false; }));
        top.Children.Add(selection); top.Children.Add(count); UpdateCount();
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(Ui.Scroll(choices));
        var dialog = Ui.Dialog("Gerar gráficos em lote", panel, 760, 570);
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false); cancel.IsCancel = true;
        var generate = Ui.Button("Gerar selecionados", () => {
            if (targets.Any(t => t.Check.IsChecked == true)) dialog.DialogResult = true;
            else count.Text = "Selecione pelo menos uma tabela.";
        }); generate.IsDefault = true;
        footer.Children.Add(cancel); footer.Children.Add(generate);
        if (dialog.ShowDialog() != true) return;

        var selected = targets.Where(t => t.Check.IsChecked == true).ToArray();
        var pending = new List<JsonObject>();
        var identities = session.Meta.Arr("charts").OfType<JsonObject>().Select(Identity).ToHashSet();
        int failures = 0;
        owner.Progress.IsIndeterminate = false;
        owner.SetProgress(0);
        for (int i = 0; i < selected.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var target = selected[i];
            owner.StatusText.Text = $"Gráfico {i + 1}/{selected.Length}: {session.ContentName(target.Path)} · {target.Sheet}";
            try
            {
                var chart = template.DeepClone().AsObject();
                chart["path"] = target.Path; chart["sheet"] = target.Sheet; chart["header_row"] = target.HeaderRow;
                chart["excluded"] = new JsonArray();
                string tableName = session.ContentName(target.Path);
                string sourceName = string.Equals(tableName, target.Sheet, StringComparison.OrdinalIgnoreCase)
                    ? tableName : tableName + " · " + target.Sheet;
                string baseTitle = chart.S("title") + " · " + sourceName;
                chart["title"] = baseTitle;
                for (int suffix = 2; identities.Contains(Identity(chart)); suffix++) chart["title"] = baseTitle + " (" + suffix + ")";
                var request = chart.DeepClone().AsObject(); request["path"] = session.Resolve(target.Path);
                var result = await WorkspaceOperations.Run(new() {
                    ["op"] = "chart", ["project"] = session.Data!.DeepClone(), ["chart"] = request,
                });
                cancellation.ThrowIfCancellationRequested();
                var saved = result["result"]!["chart"]!.DeepClone().AsObject();
                saved["path"] = target.Path;
                pending.Add(saved); identities.Add(Identity(saved));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                owner.Log($"Falha no gráfico de {session.ContentName(target.Path)} · {target.Sheet}: {ex.Message}");
            }
            owner.SetProgress((i + 1) * 98.0 / selected.Length);
        }
        cancellation.ThrowIfCancellationRequested();
        foreach (var chart in pending) session.StoreChart(chart);
        status.Text = $"Lote concluído: {pending.Count} gráfico(s) criado(s), {failures} falha(s). Abra os gráficos nos Conteúdos. Detalhes das falhas no Histórico.";
        owner.Log(status.Text);
    }
}
