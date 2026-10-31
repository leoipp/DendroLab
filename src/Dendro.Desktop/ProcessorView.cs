using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class ProcessorView : ScrollViewer
{
    internal bool SourcesChanged =>
        !choices
            .Select(c => c.Path)
            .SequenceEqual(
                session.Files.Where(session.IsTabular),
                StringComparer.OrdinalIgnoreCase
            );
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly TextBox prefix,
        factor;
    private readonly Button inventory;
    private readonly ComboBox mode = Ui.Combo(["Inventário com revisão e aprovação", "Resumo direto por nível"]);
    private readonly ComboBox inventoryType = Ui.Combo(["IFC", "IFPC", "MISTO"]), factorScope = Ui.Combo(["Talhão", "Lote"]);
    private readonly Dictionary<string, CheckBox> summaryLevels = new();
    private readonly List<(string Path, CheckBox Box)> choices = new();

    public ProcessorView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        var form = Ui.Form();
        form.Children.Add(owner.ToolHeader("Processador"));
        Ui.Section(form,"Configuração do processamento");
        mode.SelectedIndex = session.Data!["processador"].S("modo") == "resumo_direto" ? 1 : 0;
        System.Windows.Automation.AutomationProperties.SetAutomationId(mode, "Processor.Mode");
        Ui.Field(form, "Tipo de processamento", mode);
        Ui.AddHelp(form, "Gera uma versão original sem fator, com resultados por árvore e parcela e os relatórios do inventário. Em Revisão de inventário você analisa, aplica fatores, aprova e exporta.");
        inventoryType.SelectedIndex = Array.IndexOf(new[] { "IFC", "IFPC", "MISTO" }, session.Data.At("processador").S("inventory_type"));
        factorScope.SelectedIndex = session.Data.At("processador").S("factor_scope", "talhao") == "lote" ? 1 : 0;
        var versionSettings = new StackPanel();
        Ui.Field(versionSettings, "Tipo de inventário", inventoryType);
        Ui.AddHelp(versionSettings, "As chaves de árvore e parcela são escolhidas em Configurações > Identificação. MISTO permite processar IFC e IFPC juntos, preservando a separação por tipo e base. A coluna de tipo tem prioridade sobre o nome da Fonte e sobre esta escolha.");
        Ui.Field(versionSettings, "Aplicação dos fatores nas revisões", factorScope);
        versionSettings.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        form.Children.Add(versionSettings);
        Ui.AddHelp(form, "Usa as chaves de árvore, base e tipo de inventário definidas em Configurações > Identificação. A opção de remover árvores duplicadas também fica em Projeto > Configurações > Processador; a remoção ocorre antes do ajuste de altura e das somas, nos dois modos, sem alterar o arquivo de entrada.");
        var summaries = new StackPanel();
        summaries.Children.Add(Ui.Text("Saídas do resumo direto"));
        summaries.Children.Add(Ui.Button("Configurar colunas por nível…",()=>DirectColumnsView.Show(owner,session)));
        foreach (var (key, title) in new[] { ("arvore", "Por árvore"), ("parcela", "Por parcela"), ("talhao", "Por talhão"), ("lote", "Por lote"), ("projeto", "Por projeto") })
        {
            var check = new CheckBox { Content = title, IsChecked = session.Data.At("processador.saidas").B(key, key != "arvore") };
            summaryLevels[key] = check;
            System.Windows.Automation.AutomationProperties.SetAutomationId(check, "Processor.Summary." + key);
            summaries.Children.Add(check);
            check.Click += (_, _) => TryCapture();
        }
        Ui.AddHelp(summaries, "Por árvore: DAP, H e AS. Por parcela: DAP médio, diâmetro quadrático, H médio e AB (soma de AS), sem volume/fustes amostrados. Os níveis superiores mantêm os indicadores por hectare e totais estimados.");
        summaries.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        form.Children.Add(summaries);
        Ui.Section(form,"Inventários de entrada");
        inventory = new Button { HorizontalContentAlignment = HorizontalAlignment.Left };
        var menu = new ContextMenu { StaysOpen = true };
        inventory.Click += (_, _) =>
        {
            menu.PlacementTarget = inventory;
            menu.IsOpen = true;
        };
        var all = new CheckBox { Content = "Marcar todos", Margin = new Thickness(12, 7, 12, 7) };
        var allItem = new MenuItem { Header = all, StaysOpenOnClick = true };
        menu.Items.Add(allItem);
        menu.Items.Add(new Separator());
        var selected = session
            .Meta.Arr("batch_inputs")
            .Select(n => n.Display())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in session.Files.Where(session.IsTabular))
        {
            var box = new CheckBox
            {
                Content = session.ContentName(path),
                IsChecked = selected.Contains(path),
                Margin = new Thickness(12, 7, 12, 7),
            };
            choices.Add((path, box));
            menu.Items.Add(new MenuItem { Header = box, StaysOpenOnClick = true });
            box.Checked += (_, _) => SelectionChanged();
            box.Unchecked += (_, _) => SelectionChanged();
        }
        all.Click += (_, _) =>
        {
            foreach (var choice in choices)
                choice.Box.IsChecked = all.IsChecked;
            SelectionChanged();
        };
        form.Children.Add(inventory);
        UpdateSummary();
        Ui.AddHelp(form, "Resultados ficam no mesmo arquivo DLB do projeto. Excel e CSV ficam disponíveis na exportação das tabelas.");
        Ui.Section(form,"Saídas e execução");
        prefix = Ui.Input(session.Meta.S("output_prefix"));
        Ui.Field(form, "Prefixo dos resultados", prefix);
        factor = Ui.Input(session.Data!.Obj("volume")["fator"].Display());
        var factorSettings = new StackPanel { Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed };
        Ui.Field(factorSettings, "Fator do resumo direto (opcional, de 0 a 1)", factor);
        Ui.AddHelp(factorSettings, "Vazio = 1. Aplica equação × fator por árvore, antes das somas.");
        form.Children.Add(factorSettings);
        prefix.LostKeyboardFocus += (_, _) => TryCapture();
        factor.LostKeyboardFocus += (_, _) => TryCapture();
        inventoryType.SelectionChanged += (_, _) => TryCapture(); factorScope.SelectionChanged += (_, _) => TryCapture();
        mode.SelectionChanged += (_, _) => { summaries.Visibility = factorSettings.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            versionSettings.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; TryCapture(); };
        form.Children.Add(Ui.AsyncButton("Executar selecionados", () => owner.Execute(Process)));
        Content = form;
    }

    private void SelectionChanged()
    {
        UpdateSummary();
        session.Meta["batch_inputs"] = new JsonArray(
            choices
                .Where(c => c.Box.IsChecked == true)
                .Select(c => (JsonNode?)JsonValue.Create(c.Path))
                .ToArray()
        );
        session.Touch();
    }

    private void UpdateSummary()
    {
        int count = choices.Count(c => c.Box.IsChecked == true);
        inventory.Content =
            count == 1 ? session.ContentName(choices.First(c => c.Box.IsChecked == true).Path)
            : count > 0 ? $"{count} inventários selecionados  ▾"
            : "Selecionar inventários  ▾";
    }

    internal void RefreshContentNames()
    {
        foreach (var choice in choices)
            choice.Box.Content = session.ContentName(choice.Path);
        UpdateSummary();
    }

    private void TryCapture()
    {
        try
        {
            Capture();
        }
        catch (Exception ex)
        {
            factor.ToolTip = ex.Message;
        }
    }

    public void Capture()
    {
        double? value = Ui.Factor(factor.Text);
        var processing = session.Data!["processador"]?.DeepClone().AsObject() ?? DirectInventorySummary.Defaults();
        processing.Remove("execucao");
        ProjectStore.MergeDefaults(processing, DirectInventorySummary.Defaults());
        processing["modo"] = mode.SelectedIndex == 1 ? "resumo_direto" : "completo";
        processing["inventory_type"] = inventoryType.SelectedItem?.ToString() ?? "";
        processing["factor_scope"] = factorScope.SelectedIndex == 1 ? "lote" : "talhao";
        foreach (var pair in summaryLevels) processing.Obj("saidas")[pair.Key] = pair.Value.IsChecked == true;
        DirectInventorySummary.Validate(processing);
        bool changed =
            !JsonNode.DeepEquals(processing, session.Data!["processador"])
            || session.Meta.S("output_prefix") != prefix.Text
            || session.Data!.Obj("volume").At("fator")?.Display()
                != (value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? null);
        session.Meta["output_prefix"] = prefix.Text;
        session.Data!["processador"] = processing;
        if (value.HasValue)
            session.Data!.Obj("volume")["fator"] = value.Value;
        else
            session.Data!.Obj("volume").Remove("fator");
        if (changed)
            session.Touch();
    }

    internal async Task Process()
    {
        Capture();
        bool versioned = mode.SelectedIndex == 0;
        if (versioned && session.FilePath.Length == 0) throw new InvalidOperationException("Salve o projeto antes de criar o histórico de processamento.");
        if (versioned && inventoryType.SelectedIndex < 0) throw new InvalidOperationException("Indique se o inventário é IFC ou IFPC.");
        var inputs = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToArray();
        if (inputs.Length == 0)
            throw new InvalidOperationException("Selecione um ou mais inventários.");
        string outputFolder = session.ProjectWorkFolder();
        if (versioned && session.IsDirty) await session.Save(session.FilePath);
        if (prefix.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("O prefixo contém caracteres inválidos.");
        string registry = session.Resolve(session.RegistryPath);
        if (!Dendro.Core.Tabular.CompactBankFile.Exists(registry))
            throw new InvalidOperationException(
                "Configure o cadastro florestal em Projeto > Configurações."
            );
        string snapshot = Path.Combine(Path.GetTempPath(), $"DENDRO-{Guid.NewGuid():N}.dlb");
        var config = session.ProcessingSnapshot();
        try
        {
            await WorkspaceOperations.Run(
                new()
                {
                    ["op"] = "save",
                    ["project"] = config.DeepClone(),
                    ["path"] = snapshot,
                }
            );
            int completed = 0,
                failed = 0;
            foreach (var path in inputs)
            {
                owner.OperationCancellation.ThrowIfCancellationRequested();
                owner.StartTableOperation("Processando " + session.ContentName(path) + "…");
                JsonObject? run = versioned ? ProcessingCatalog.NewRecord(path, inventoryType.SelectedItem!.ToString()!, factorScope.SelectedIndex == 1 ? "lote" : "talhao") : null;
                string runConfig = snapshot;
                var suffix = config.At("saida.sufixo_arquivo")?.Display() ?? "_DENDRO";
                string output = Path.Combine(
                    outputFolder,
                    prefix.Text + Path.GetFileNameWithoutExtension(path) + suffix + ".dlb"
                );
                for (int n = 2; File.Exists(output); n++)
                    output = Path.Combine(
                        outputFolder,
                        prefix.Text + Path.GetFileNameWithoutExtension(path) + suffix + $"_{n}.dlb"
                    );
                try
                {
                    if (run != null)
                    {
                    runConfig = Path.Combine(Path.GetTempPath(), "DENDRO-" + run.S("id") + ".dlb");
                        var execution = config.DeepClone().AsObject(); execution.Obj("processador")["execucao"] = run.DeepClone(); execution.Obj("volume")["fator"] = 1;
                        await WorkspaceOperations.Run(new() { ["op"] = "save", ["project"] = execution, ["path"] = runConfig });
                    }
                    var r = await WorkspaceOperations.Run(
                        new()
                        {
                            ["op"] = "process",
                            ["input"] = session.Resolve(path),
                            ["config"] = runConfig,
                            ["registry"] = registry,
                            ["output"] = output,
                            ["sheet"] = session.ExcelOptions(path).S("sheet"),
                        },
                        (percent, message) =>
                            owner.Dispatcher.BeginInvoke(() =>
                            {
                                owner.Progress.IsIndeterminate = false;
                                owner.SetProgress((completed * 100.0 + Math.Min(percent, 98)) / inputs.Length);
                                owner.StatusText.Text = message;
                            }), owner.OperationCancellation
                    );
                    var resultSheets = (
                        await WorkspaceOperations.Run(
                            new() { ["op"] = "sheets", ["path"] = output }
                        )
                    )["result"]!.AsArray();
                    if (run != null)
                    {
                        run["sheets"] = new JsonArray(resultSheets.Where(s => !s.Display().StartsWith(ProcessingHistory.InternalPrefix, StringComparison.Ordinal)).Select(s => s!.DeepClone()).ToArray());
                        run["totals"] = r["result"]!.DeepClone();
                        run["inventory_types"] = r["result"]!["inventory_types"]?.DeepClone() ?? new JsonArray();
                        if (run.Arr("inventory_types").Count > 1) run["inventory_type"] = "MISTO";
                        else if (run.Arr("inventory_types").Count == 1) run["inventory_type"] = run.Arr("inventory_types")[0]!.DeepClone();
                        run["removed_duplicates"] = r["result"]!.N("removed_duplicates");
                        run["duplicate_policy"] = config["processador"].B("remover_duplicadas") ? "keep_first" : "reject";
                        run["export_sheets"] = ProcessingHistory.DefaultExportSheets(config, run["sheets"]!.AsArray().Select(s => s.Display()));
                        owner.StatusText.Text = "Publicando resultados no arquivo DLB do projeto…";
                        await session.PublishProcessing(output, run, owner.OperationCancellation, (copied, total) =>
                            owner.Dispatcher.BeginInvoke(() =>
                            {
                                owner.SetProgress((completed * 100.0 + 98 + (double)copied / Math.Max(1, total)) / inputs.Length);
                                owner.StatusText.Text = $"Publicando banco: {copied / 1048576.0:N1} de {total / 1048576.0:N1} MB…";
                            }));
                        owner.Log($"{session.ContentName(path)} · {run.S("inventory_type")} · V1 original criada. Revise e aprove antes de exportar.");
                    }
                    else
                    {
                    bool directResult = resultSheets.Any(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal));
                    var previous = session.Meta.Arr("processing_results").OfType<JsonObject>().Where(p =>
                        p.S("source_path") == path && p.Arr("sheets").Any(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal)) == directResult).ToArray();
                    string? previousPath = previous.LastOrDefault()?.S("path");
                    string reference = await session.CommitBankFile(output, previousPath);
                    foreach (var old in previous) session.Meta.Arr("processing_results").Remove(old);
                    session.AddFile(reference);
                    session.SetExcel(
                        reference,
                        resultSheets.Any(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal))
                            ? resultSheets.First(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal)).Display()
                            : resultSheets.Any(s => s.Display() == "RESULTADO - ARVORES")
                            ? "RESULTADO - ARVORES"
                            : resultSheets.Any(s => s.Display() == "AJUSTADO") ? "AJUSTADO"
                            : resultSheets.First().Display(),
                        1
                    );
                    session.Meta.Arr("processing_results").Add(new JsonObject
                    {
                        ["source_path"] = path,
                        ["path"] = reference,
                        ["sheets"] = resultSheets.DeepClone(),
                        ["created"] = DateTimeOffset.Now.ToString("O"),
                        ["totals"] = r["result"]!.DeepClone(),
                        ["removed_duplicates"] = r["result"]!.N("removed_duplicates"),
                    });
                    session.Touch();
                    owner.Log(
                        $"{session.ContentName(path)} → tabelas no arquivo do projeto · {r["result"]!.N("trees"):N0} árvores · volume {r["result"]!.N("volume"):N3}"
                    );
                    }
                    if (r["result"]!.N("removed_duplicates") > 0)
                        owner.Log($"{session.ContentName(path)}: {r["result"]!.N("removed_duplicates"):N0} registro(s) duplicado(s) removido(s); mantida a primeira ocorrência de cada chave configurada.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    owner.Log(Path.GetFileName(path) + ": " + ex.Message);
                    bool areaIssues = ex.Data[ParcelAreaValidation.DiagnosticsKey] != null;
                    if ((ex.Data[ParcelAreaValidation.DiagnosticsKey] ?? ex.Data[RegistryAgeFallback.DiagnosticsKey]) is Dendro.Core.Spreadsheets.DataTable issues)
                    {
                        string title = areaIssues ? "Pendências de área" : "Pendências de plantio";
                        string diagnostics = Path.Combine(outputFolder, "Pendencias_" + Guid.NewGuid().ToString("N")[..8] + ".dlb");
                        bool openedDiagnostics = false;
                        try
                        {
                            await Task.Run(() =>
                            {
                                int width = issues.Width;
                                using var writer = new CompactBankWriter(diagnostics);
                                string id = writer.AddTable(Enumerable.Range(0, width).Select(c => issues.Text(0, c)).ToArray(), title);
                                for (int row = 1; row < issues.Count; row++)
                                {
                                    owner.OperationCancellation.ThrowIfCancellationRequested();
                                    writer.Add(id, Enumerable.Range(0, width).Select(c => issues.Value(row, c)).ToArray());
                                }
                                writer.Complete();
                            });
                            var browser = new TableBrowser(owner, new(diagnostics, title), readOnly: true,
                                releaseSource: () => { if (File.Exists(diagnostics)) File.Delete(diagnostics); });
                            await browser.Load();
                            owner.Document((areaIssues ? "area-issues:" : "planting-issues:") + path, title + " · " + session.ContentName(path), browser);
                            openedDiagnostics = true;
                            owner.Log(areaIssues
                                ? $"{title}: {issues.Count - 1:N0} grupo(s) de valores inválidos. Confira dimensões, declinações e mapeamento das colunas; nenhum processamento foi publicado."
                                : $"{title}: {issues.Count - 1:N0} ocorrência(s) agrupada(s) por chave/código. Corrija o cadastro ou os dados de origem; nenhum processamento foi publicado.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception diagnosticError) { owner.Log("Não foi possível abrir a tabela de pendências: " + diagnosticError.Message); }
                        finally { if (!openedDiagnostics && File.Exists(diagnostics)) File.Delete(diagnostics); }
                    }
                }
                finally { if (runConfig != snapshot && File.Exists(runConfig)) File.Delete(runConfig); if (File.Exists(output)) File.Delete(output); }
                completed++;
            }
            owner.Log($"Lote concluído: {completed - failed} sucesso(s), {failed} falha(s).");
        }
        finally
        {
            File.Delete(snapshot);
        }
    }
}
