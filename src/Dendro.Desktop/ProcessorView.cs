using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class ProcessorView : DockPanel
{
    internal bool SourcesChanged =>
        choicesLoaded && !choices
            .Select(c => c.Path)
            .SequenceEqual(
                session.Files.Where(session.IsTabular),
                StringComparer.OrdinalIgnoreCase
            );
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly TextBox factor;
    private readonly Button inventory;
    private readonly ContextMenu inventoryMenu;
    private readonly HashSet<string> selectedInputs;
    private readonly ComboBox mode = Ui.Combo(["Inventário com revisão e aprovação", "Resumo direto por nível"]);
    private readonly CheckBox classDistribution = new() { Content = "Calcular distribuição por classes de DAP e volume" };
    private readonly TextBox classWidth = Ui.Input("20"), volumeClassInterval = Ui.Input("0.1");
    private readonly ComboBox inventoryType = Ui.Combo(["IFC", "IFPC", "MISTO"]), factorScope = Ui.Combo(["Talhão", "Lote"]);
    private readonly Dictionary<string, CheckBox> summaryLevels = new();
    private readonly List<(string Path, CheckBox Box)> choices = new();
    private readonly TextBlock validation = Ui.Text("");
    private readonly WrapPanel selectionSummary = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox inputSearch = Ui.Input();
    private bool choicesLoaded;
    private bool suppressSelectionChange;
    private Task? choicesPopulation;

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
        var summaries = new StackPanel();
        summaries.Children.Add(Ui.Text("Saídas do resumo direto"));
        Ui.AddHelp(summaries, "Por árvore: DAP, H e AS. Por parcela: DAP médio, diâmetro quadrático, H médio e AB (soma de AS), sem volume/fustes amostrados. Os níveis superiores mantêm os indicadores por hectare e totais estimados.");
        summaries.Children.Add(Ui.Button("Configurar colunas por nível…",()=>DirectColumnsView.Show(owner,session)));
        foreach (var (key, title) in new[] { ("arvore", "Por árvore"), ("parcela", "Por parcela"), ("talhao", "Por talhão"), ("lote", "Por lote"), ("projeto", "Por projeto") })
        {
            var check = new CheckBox { Content = title, IsChecked = session.Data.At("processador.saidas").B(key, key != "arvore") };
            summaryLevels[key] = check;
            System.Windows.Automation.AutomationProperties.SetAutomationId(check, "Processor.Summary." + key);
            summaries.Children.Add(check);
            check.Click += (_, _) => TryCapture();
        }
        summaries.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        form.Children.Add(summaries);
        var classSettings = new StackPanel();
        classDistribution.IsChecked = session.Data.At("processador").B("distribuicao_classes");
        System.Windows.Automation.AutomationProperties.SetAutomationId(classDistribution, "Processor.ClassDistribution");
        classWidth.Text = session.Data.At("processador").N("largura_classe_dap_mm", 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
        classSettings.Children.Add(classDistribution);
        Ui.AddHelp(classSettings, "Gera tabelas de distribuição para parcela, talhão, lote e projeto. Cada nível soma os indivíduos por classe e divide pelo total de indivíduos válidos do grupo. DAP e volume têm intervalos próprios. Cada frequência conta as árvores da classe e divide pelo total de árvores com valor válido na parcela.");
        Ui.Field(classSettings, "Intervalo de classe de DAP (mm)", classWidth);
        volumeClassInterval.Text = session.Data.At("processador").N("intervalo_classe_volume_m3", 0.1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Ui.Field(classSettings, "Intervalo de classe de volume (m³/árvore)", volumeClassInterval);
        form.Children.Add(classSettings);
        Ui.Section(form,"Inventários de entrada");
        inventory = new Button { HorizontalContentAlignment = HorizontalAlignment.Left };
        var menu = inventoryMenu = new ContextMenu { StaysOpen = true };
        var all = new CheckBox { Content = "Marcar todos", Margin = new Thickness(12, 7, 12, 7) };
        var allItem = new MenuItem { Header = all, StaysOpenOnClick = true };
        menu.Items.Add(allItem);
        menu.Items.Add(new Separator());
        inputSearch.MinWidth = 220;
        inputSearch.ToolTip = "Pesquisar inventários";
        System.Windows.Automation.AutomationProperties.SetName(inputSearch, "Pesquisar inventários");
        menu.Items.Add(new MenuItem { Header = inputSearch, StaysOpenOnClick = true });
        inputSearch.TextChanged += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Header is CheckBox box && !ReferenceEquals(box, all)))
                item.Visibility = ((CheckBox)item.Header).Content.ToString()!.Contains(inputSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var selected = selectedInputs = session
            .Meta.Arr("batch_inputs")
            .Select(n => n.Display())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        inventory.Click += async (_, _) =>
        {
            if (!choicesLoaded) await PopulateChoices(menu, selected);
            menu.PlacementTarget = inventory;
            menu.IsOpen = true;
        };
        all.Click += (_, _) =>
        {
            suppressSelectionChange = true;
            foreach (var choice in choices)
                choice.Box.IsChecked = all.IsChecked;
            suppressSelectionChange = false;
            SelectionChanged();
        };
        form.Children.Add(inventory);
        form.Children.Add(selectionSummary);
        UpdateSummary();
        Ui.Section(form,"Saídas e execução");
        factor = Ui.Input(session.Data!.Obj("volume")["fator"].Display());
        var factorSettings = new StackPanel { Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed };
        Ui.Field(factorSettings, "Fator do resumo direto (opcional, de 0 a 1)", factor);
        Ui.AddHelp(factorSettings, "Vazio = 1. Aplica equação × fator por árvore, antes das somas.");
        form.Children.Add(factorSettings);
        factor.LostKeyboardFocus += (_, _) => TryCapture(); classWidth.LostKeyboardFocus += (_, _) => TryCapture(); volumeClassInterval.LostKeyboardFocus += (_, _) => TryCapture();
        classDistribution.Click += (_, _) => TryCapture();
        inventoryType.SelectionChanged += (_, _) => TryCapture(); factorScope.SelectionChanged += (_, _) => TryCapture();
        mode.SelectionChanged += (_, _) => { summaries.Visibility = factorSettings.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            versionSettings.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; TryCapture(); };
        validation.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 41, 52));
        validation.Visibility = Visibility.Collapsed;
        form.Children.Add(validation);
        var execute = Ui.AsyncButton("Executar", () => owner.Execute(Process), Ui.ActionEmphasis.Primary);
        execute.MinWidth = 100;
        var footer = Ui.ActionFooter(execute);
        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(Ui.Scroll(form));
    }

    private Task PopulateChoices(ContextMenu menu, HashSet<string> selected)
    {
        if (choicesLoaded) return Task.CompletedTask;
        return choicesPopulation ??= PopulateChoicesCore(menu, selected);
    }

    private async Task PopulateChoicesCore(ContextMenu menu, HashSet<string> selected)
    {
        string[] paths = session.Files.Where(session.IsTabular).ToArray();
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            var box = new CheckBox
            {
                Content = session.ContentName(path),
                IsChecked = selected.Contains(path),
                Margin = new Thickness(12, 7, 12, 7),
            };
            choices.Add((path, box));
            menu.Items.Add(new MenuItem { Header = box, StaysOpenOnClick = true });
            box.Checked += (_, _) => { if (!suppressSelectionChange) SelectionChanged(); };
            box.Unchecked += (_, _) => { if (!suppressSelectionChange) SelectionChanged(); };
            if ((i + 1) % 32 == 0)
                await Dispatcher.Yield(DispatcherPriority.Background);
        }
        choicesLoaded = true;
        UpdateSummary();
    }

    private void SelectionChanged()
    {
        if (suppressSelectionChange) return;
        selectedInputs.Clear();
        foreach (var choice in choices.Where(c => c.Box.IsChecked == true)) selectedInputs.Add(choice.Path);
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
        string[] selected = choicesLoaded
            ? choices.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToArray()
            : session.Files.Where(session.IsTabular).Where(selectedInputs.Contains).ToArray();
        int count = selected.Length;
        selectionSummary.Children.Clear();
        if (count == 0)
            selectionSummary.Children.Add(Ui.Text("Selecione os inventários que deseja processar.", 12));
        foreach (string path in selected)
        {
            var row = new DockPanel();
            var remove = Ui.Button("×", () =>
            {
                if (choicesLoaded) choices.First(choice => choice.Path == path).Box.IsChecked = false;
                else
                {
                    selectedInputs.Remove(path);
                    session.Meta["batch_inputs"] = new JsonArray(selectedInputs.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                    session.Touch();
                    UpdateSummary();
                }
            }, Ui.ActionEmphasis.Tertiary, size: Ui.ActionSize.Small);
            remove.ToolTip = "Retirar " + session.ContentName(path) + " da seleção";
            System.Windows.Automation.AutomationProperties.SetName(remove, remove.ToolTip.ToString());
            remove.Margin = new Thickness(4, 0, 0, 0);
            remove.Padding = new Thickness(3, 0, 3, 0);
            remove.MinHeight = 24;
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = session.ContentName(path), MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = session.ContentName(path), VerticalAlignment = VerticalAlignment.Center });
            selectionSummary.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(5),
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("Ui.Surface"),
                Padding = new Thickness(8, 2, 3, 2), Margin = new Thickness(0, 0, 6, 6) });
        }
        inventory.Content = count == 0 ? "Selecionar inventários  ▾"
            : count == 1 ? "1 inventário selecionado  ▾" : $"{count} inventários selecionados  ▾";
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
            validation.Text = "";
            validation.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            validation.Text = ex.Message;
            validation.Visibility = Visibility.Visible;
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
        processing["distribuicao_classes"] = classDistribution.IsChecked == true;
        double classWidthValue = Ui.Number(classWidth.Text);
        if (classDistribution.IsChecked == true && (!double.IsFinite(classWidthValue) || classWidthValue < 1 || classWidthValue > 1000))
            throw new InvalidOperationException("O intervalo de classe de DAP deve estar entre 1 e 1000 mm.");
        if (double.IsFinite(classWidthValue)) processing["largura_classe_dap_mm"] = classWidthValue;
        double volumeInterval = Ui.Number(volumeClassInterval.Text);
        if (classDistribution.IsChecked == true && (!double.IsFinite(volumeInterval) || volumeInterval <= 0))
            throw new InvalidOperationException("O intervalo de classe de volume deve ser maior que zero.");
        if (double.IsFinite(volumeInterval)) processing["intervalo_classe_volume_m3"] = volumeInterval;
        foreach (var pair in summaryLevels) processing.Obj("saidas")[pair.Key] = pair.Value.IsChecked == true;
        DirectInventorySummary.Validate(processing);
        bool changed =
            !JsonNode.DeepEquals(processing, session.Data!["processador"])
            || session.Data!.Obj("volume").At("fator")?.Display()
                != (value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? null);
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
        // Keep execution reliable when invoked before the view receives its WPF
        // Loaded event (for example, automation and keyboard-triggered actions).
        await PopulateChoices(inventoryMenu, selectedInputs);
        Capture();
        bool versioned = mode.SelectedIndex == 0;
        if (versioned && session.FilePath.Length == 0) throw new InvalidOperationException("Salve o projeto antes de criar o histórico de processamento.");
        if (versioned && inventoryType.SelectedIndex < 0) throw new InvalidOperationException("Indique se o inventário é IFC ou IFPC.");
        var inputs = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToArray();
        if (inputs.Length == 0)
            throw new InvalidOperationException("Selecione um ou mais inventários.");
        string outputFolder = session.ProjectWorkFolder();
        if (versioned && session.IsDirty) await session.Save(session.FilePath);
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
                    Path.GetFileNameWithoutExtension(path) + suffix + ".dlb"
                );
                for (int n = 2; File.Exists(output); n++)
                    output = Path.Combine(
                        outputFolder,
                        Path.GetFileNameWithoutExtension(path) + suffix + $"_{n}.dlb"
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
                    bool directResult = resultSheets.Any(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal));
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
                    // Cada execução é uma versão independente. Nunca substituir o resultado
                    // anterior da mesma origem: o histórico deve continuar acessível no projeto.
                    string reference = await session.CommitBankFile(output);
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
                        ["source_sheet"] = session.ExcelOptions(path).S("sheet"),
                        ["direct_options"] = directResult ? config["processador"]?.DeepClone() : null,
                        ["path"] = reference,
                        ["sheets"] = resultSheets.DeepClone(),
                        ["created"] = DateTimeOffset.Now.ToString("O"),
                        ["totals"] = r["result"]!.DeepClone(),
                        ["removed_duplicates"] = r["result"]!.N("removed_duplicates"),
                    });
                    session.Touch();
                    owner.Log(
                        $"{session.ContentName(path)} → novo resultado {Path.GetFileNameWithoutExtension(reference)} · {r["result"]!.N("trees"):N0} árvores · volume {r["result"]!.N("volume"):N3}"
                    );
                    }
                    if (directResult)
                        owner.Log(
                            $"{session.ContentName(path)}: {r["result"]!.N("trees"):N0} linhas usadas; remoção de duplicadas {(config.At("processador").B("remover_duplicadas") ? "ativada" : "desativada")}; {r["result"]!.N("removed_duplicates"):N0} removida(s)."
                        );
                    else if (r["result"]!.N("removed_duplicates") > 0)
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
