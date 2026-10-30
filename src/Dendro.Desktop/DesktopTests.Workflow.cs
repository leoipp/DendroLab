using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static async Task RunProcessingWorkflow(string root)
    {
        Directory.CreateDirectory(root);
        var window = new MainWindow { WorkspacePath = Path.Combine(root, "layout.xml"), CloseConfirmation = () => Task.FromResult(true) };
        await window.Session.New("Fluxo completo");
        var input = new DataTable(["Talhão", "Parcela", "Lote", "DAP", "H", "Data", "Compr", "Larg", "Dec_Compr", "Dec_Larg", "Cova", "Fuste", "Fila"]);
        double date = new DateTime(2026, 6, 1).ToOADate();
        input.Add("T01", "P01", "XXXXAL00000P", 20d, 10d, date, 100d, 100d, 0d, 0d, 1d, 1d, 1d);
        input.Add("T01", "P01", "XXXXAL00000P", 40d, 20d, date, 100d, 100d, 0d, 0d, 2d, 1d, 1d);
        input.Add("T01", "P01", "XXXXAL00000P", 20d, 10d, date + 30, 100d, 100d, 0d, 0d, 1d, 1d, 1d);
        input.Add("T01", "P01", "XXXXAL00000P", 999d, 999d, date, 100d, 100d, 0d, 0d, 1d, 1d, 1d);
        string SaveTable(string name, DataTable data)
        {
            string path = Path.Combine(root, name + ".dlb"); using var writer = new CompactBankWriter(path);
            string id = writer.AddTable(Enumerable.Range(0, data.Width).Select(c => data.Text(0, c)).ToArray(), "Dados");
            foreach (var row in data.Rows.Skip(1)) writer.Add(id, row.ToArray()); writer.Complete(); return path;
        }
        string source = SaveTable("input", input);
        var registry = new DataTable(["Talhão", "Data de Plantio", "Área efetiva (ha)"]); registry.Add("T01", new DateTime(2016, 6, 1).ToOADate(), 10d);
        string cadastro = SaveTable("cadastro", registry);
        window.Session.AddFile(source); window.Session.SetExcel(source, "Dados", 1);
        window.Session.Data!.Obj("cadastro")["caminho"] = cadastro;
        window.Session.Data!.Obj("cadastro")["worksheet"] = "Dados";
        window.Session.Data!.Obj("volume").Arr("equacoes").Add(new JsonObject { ["codigo"] = "AL1R", ["b0"] = 0, ["b1"] = 1, ["b2"] = 1 });
        window.Session.Data!.Obj("ajuste_altura")["variaveis_customizadas"] = new JsonArray();
        window.Session.Data!.Obj("ajuste_altura")["estatisticas_parcela"] = new JsonArray();
        window.Session.Data!.Obj("processador")["inventory_type"] = "IFPC";
        window.Session.Meta["batch_inputs"] = new JsonArray(source);
        string project = Path.Combine(root, "workflow.dendro"); await window.Session.Save(project);
        var processor = new ProcessorView(window, window.Session); window.SetTool(processor);
        LogicalControls<CheckBox>(processor).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Processor.RemoveDuplicates").IsChecked = true;
        await window.Execute(processor.Process);
        Check(window.Session.Meta.Arr("processing_results").Count > 0, "original publicado: " + window.HistoryText.Text);
        var original = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Single();
        Check(original.N("number") == 1 && original.S("inventory_type") == "IFPC", "processador publica V1 original com indicador IFPC");
        Check(original.N("removed_duplicates") == 1 && window.HistoryText.Text.Contains("registro(s) duplicado(s) removido(s)"), "remoção opcional registra quantidade no histórico e no log");
        var originalTrees = TableStore.Read(new(window.Session.Resolve(original.S("path")), ProcessingHistory.Trees));
        Check(Math.Abs(Enumerable.Range(1, originalTrees.Count - 1).Sum(r => originalTrees.Number(r, originalTrees.Require("VTCC"))) - 12) < 1e-9, "V1 não aplica fator");
        Check(TableStore.Read(new(window.Session.Resolve(original.S("path")), ProcessingHistory.Plots)).Count == 3, "parcelas separam datas de medição no mesmo ano");
        var adjusted = TableStore.Read(new(window.Session.Resolve(original.S("path")), "AJUSTADO"));
        Check(adjusted.Column("Data de Plantio") >= 0 && double.IsFinite(adjusted.Number(1, adjusted.Require("Data de Plantio"))), "AJUSTADO inclui cadastro e variáveis geradas sem debug");
        Check(!original.Arr("sheets").Any(s => s.Display() == "Graficos"), "processamento não gera tabela Gráficos no banco");
        Check(LogicalControls<TreeViewItem>(window.ContentsTree).Any(n => n.Tag is MainWindow.ProcessingVersionReference v && v.Id == original.S("id")), "histórico de versões aparece nos Conteúdos");

        await window.OpenProcessingReview(original.S("id"));
        var view = (ProcessingReviewView)window.ToolContent.Content;
        var grid = LogicalControls<DataGrid>(view).Single();
        foreach (var row in grid.Items) row.GetType().GetProperty("Factor")!.SetValue(row, "0,5");
        LogicalControls<Button>(view).Single(b => b.Content?.ToString() == "Criar nova versão corrigida").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        var revised = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.N("number") == 2);
        var corrected = TableStore.Read(new(window.Session.Resolve(revised.S("path")), ProcessingHistory.Trees));
        Check(Math.Abs(Enumerable.Range(1, corrected.Count - 1).Sum(r => corrected.Number(r, corrected.Require("VTCC"))) - 6) < 1e-9, "grade aplica fatores em V2 sem acumular nem sobrescrever V1");
        Check(window.Session.Meta.Arr("processing_results").Count == 2, "original e correção permanecem disponíveis");
        LogicalControls<Button>(view).Single(b => b.Content?.ToString() == "Aprovar versão selecionada").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        await window.Session.RefreshHistory(true);
        var approved = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("id") == revised.S("id"));
        Check(approved.S("status") == "aprovado" && approved.S("approved_by") == ProcessingCatalog.User, "aprovação registra usuário do Windows");
        bool blocked = false;
        try { window.Session.RequireApprovedExport(original.S("path")); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "exportação da versão não aprovada é bloqueada");
        string excel = Path.Combine(root, "aprovado.xlsx");
        ProcessingHistory.ExportApproved(project, [], approved.S("id"), [ProcessingHistory.Plots], excel);
        Check(SpreadsheetReader.Sheets(excel).SequenceEqual(new[] { ProcessingHistory.Plots, "PROCESSAMENTO" }), "Excel exporta somente abas escolhidas e rastreabilidade");
        var another = new ProjectSession(); await another.Open(project);
        Check(another.Meta.Arr("processing_results").OfType<JsonObject>().Count(r => r.S("id").Length > 0) == 2, "outro usuário encontra histórico publicado sem precisar salvar o projeto novamente");
        Check(another.Meta.S("database_path").Length > 0, "vínculo do banco está disponível para o segundo usuário");
        bool blockedAbsolute = false;
        try { another.RequireApprovedExport(another.Resolve(original.S("path"))); } catch (InvalidOperationException) { blockedAbsolute = true; }
        Check(blockedAbsolute, "caminho absoluto também respeita bloqueio de exportação da versão não aprovada");
        var audit = another.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("id") == revised.S("id")).Arr("audit");
        Check(audit.Count == 2, "histórico registra publicação e aprovação");
        var selectedTool = window.ToolContent.Content;
        window.Show();
        async Task CheckNative(string path, string sheet, string screenshot)
        {
            await window.OpenTable(path, sheet);
            var document = window.DockManager.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>()
                .Single(d => d.ContentId == "table:" + path + ":" + sheet);
            Check(document.Content is ProcessingReportView, sheet + " abre apresentação nativa");
            var view = (ProcessingReportView)document.Content;
            var tabs = LogicalControls<TabControl>(view).Single();
            Check(tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).SequenceEqual(new[] { "Informações", "Gráficos" }), sheet + " separa informações e gráficos");
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(view, Path.Combine(root, screenshot + "-info.png"));
            tabs.SelectedIndex = 1;
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(view, Path.Combine(root, screenshot + "-plot.png"));
            Check(LogicalControls<ScatterPlot>(view).Any(), sheet + " tem gráfico próprio");
        }
        await CheckNative(original.S("path"), "ESTATISTICAS INV", "inventory-native");
        await CheckNative(Path.Combine(AppContext.BaseDirectory, "..", "..", "tests", "Fixtures", "Reference", "rna_report.xlsx"), "H Ajuste", "height-native");
        string legacyAdjusted = Path.Combine(root, "legacy-adjusted.dlb");
        using (var writer = new CompactBankWriter(legacyAdjusted))
        {
            string id = writer.AddTable(input.Rows[0].Select(c => c?.ToString() ?? "").ToArray(), "AJUSTADO");
            foreach (var row in input.Rows.Skip(1).Take(3)) writer.Add(id, row.ToArray());
            writer.Complete();
        }
        CompactBankFile.ReplaceTable(new(window.Session.Resolve(original.S("path")), "AJUSTADO"), legacyAdjusted);
        await window.OpenTable(original.S("path"), "AJUSTADO");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var adjustedDocument = window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "table:" + original.S("path") + ":AJUSTADO");
        Check(LogicalControls<DataGrid>((TableBrowser)adjustedDocument.Content).Single().Columns.Any(c => c.Header?.ToString() == "Data de Plantio"),
            "AJUSTADO legado recupera colunas da entrada completa congelada");
        var historicalBrowser = (TableBrowser)adjustedDocument.Content;
        var historicalGrid = LogicalControls<DataGrid>(historicalBrowser).Single(); historicalGrid.SelectedItem = historicalGrid.Items[0];
        Check(!LogicalControls<Button>(historicalBrowser).Single(b => b.Content?.ToString() == "Excluir selecionadas…").IsEnabled, "exclusão não modifica versões preservadas do histórico");
        var currentOriginal = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("id") == original.S("id"));
        currentOriginal.Arr("sheets").Add("Graficos"); window.Session.Touch();
        Check(!LogicalControls<TreeViewItem>(window.ContentsTree).Any(n => n.Tag is MainWindow.ResultSheetReference r && r.Sheet == "Graficos"), "tabela Gráficos legada fica fora dos Conteúdos");
        Check(ReferenceEquals(window.ToolContent.Content, selectedTool), "abrir relatórios nativos preserva ferramenta selecionada");
        string secondSource = SaveTable("input-second", input);
        window.Session.AddFile(secondSource); window.Session.SetExcel(secondSource, "Dados", 1);
        window.Session.Meta.Arr("content_names").Add(new JsonObject { ["path"] = secondSource, ["kind"] = "file", ["name"] = "Inventário 2026" });
        var merge = new TableToolsView(window, window.Session, false); window.SetTool(merge);
        foreach (var row in LogicalControls<DataGrid>(merge).First().ItemsSource.Cast<TableToolsView.InputRow>())
            row.Selected = row.Path == source || row.Path == secondSource;
        LogicalControls<TextBox>(merge).First().Text = "Fontes_UI";
        LogicalControls<Button>(merge).Single(b => b.Content?.ToString() == "Juntar tabelas").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        string mergedPath = window.Session.Files.Single(p => Path.GetFileName(p) == "Fontes_UI.dlb");
        var mergedData = TableStore.Read(new(window.Session.Resolve(mergedPath)));
        Check(mergedData.Count == 9 && mergedData.Text(1, mergedData.Require("Fonte")) == window.Session.ContentName(source)
            && mergedData.Text(5, mergedData.Require("Fonte")) == "Inventário 2026", "junção grava automaticamente os nomes visíveis de cada tabela na coluna Fonte");
        await window.OpenTable(mergedPath);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var filterDocument = window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "table:" + mergedPath);
        var browser = (TableBrowser)filterDocument.Content;
        var columnFilter = LogicalControls<ComboBox>(browser).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "TableFilter.Column");
        var operationFilter = LogicalControls<ComboBox>(browser).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "TableFilter.Operator");
        var valueFilter = LogicalControls<TextBox>(browser).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "TableFilter.Value");
        Controls<Button>(browser).First(b => b.ToolTip?.ToString() == "Filtrar DAP").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(columnFilter.SelectedItem?.ToString() == "DAP" && LogicalControls<Expander>(browser).Single().IsExpanded, "seta do cabeçalho abre filtro da coluna escolhida");
        valueFilter.Text = "999";
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Aplicar filtro").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(browser.Total == 2, "filtro numérico seleciona linhas em toda a tabela");
        columnFilter.SelectedItem = "Fonte"; operationFilter.SelectedItem = "Contém"; valueFilter.Text = "2026";
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Aplicar filtro").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(browser.Total == 1 && LogicalControls<Button>(browser).Any(b => b.Content?.ToString() == "Exportar resultado filtrado…"), "filtros de colunas diferentes são combinados e identificam exportação filtrada");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(browser, Path.Combine(root, "table-filters.png"));
        window.ResetWorkspace(); await browser.Load();
        Check(browser.Total == 1, "prévia filtrada permanece disponível após restaurar layout");
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Limpar filtros").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(browser.Total == 8, "limpar filtros restaura tabela completa sem alterar os dados");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var dateColumn = LogicalControls<DataGrid>(browser).Single().Columns.OfType<DataGridTextColumn>().Single(c => c.Header?.ToString() == "Data");
        var dateBinding = (System.Windows.Data.Binding)dateColumn.Binding;
        object formattedDate = dateBinding.Converter.Convert(date, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture)!;
        Check(formattedDate.ToString() == "01/06/2026", "data serial é formatada como data sem alterar valor armazenado");
        Check(new TableCellFormatting(ColumnFormat.Time).Convert(.5d, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture)?.ToString() == "12:00:00"
            && TableCellFormatting.Guess("Hora de medição") == ColumnFormat.Time, "hora serial e coluna de hora recebem apresentação correta");
        var formatMenu = (ContextMenu)dateColumn.HeaderStyle.Setters.OfType<Setter>().Single(s => s.Property == FrameworkElement.ContextMenuProperty).Value;
        formatMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Data e hora").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(window.Session.Meta.Arr("table_column_formats").Any(f => f.S("column") == "Data" && f.S("format") == "DateTime"), "formato escolhido no cabeçalho fica nas preferências do projeto");
        var deletionGrid = LogicalControls<DataGrid>(browser).Single();
        deletionGrid.SelectedItem = deletionGrid.Items[0];
        long confirmedRows = 0;
        browser.DeleteConfirmation = count => { confirmedRows = count; return Task.FromResult(false); };
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Excluir selecionadas…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(confirmedRows == 1 && browser.Total == 8, "cancelar confirmação preserva linhas selecionadas");
        browser.DeleteConfirmation = count => { confirmedRows = count; return Task.FromResult(true); };
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Excluir selecionadas…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(browser.Total == 7 && TableStore.Page(new(window.Session.Resolve(mergedPath))).Total == 7, "exclusão de seleção sem filtro é gravada na própria tabela");
        columnFilter.SelectedItem = "DAP"; operationFilter.SelectedItem = "Igual a"; valueFilter.Text = "999";
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Aplicar filtro").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        LogicalControls<Button>(browser).Single(b => b.Content?.ToString() == "Excluir filtradas…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(confirmedRows == 2 && browser.Total == 5 && TableStore.Page(new(window.Session.Resolve(mergedPath))).Total == 5, "exclusão do filtro confirma quantidade total e limpa a prévia antiga");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(browser, Path.Combine(root, "table-edited-dates.png"));
        var problematic = input.Copy();
        problematic.Set(1, 0, "RDBOBA00432CON-232"); problematic.Set(2, 0, "RDBOBA00432CON-232");
        problematic.Set(3, 0, "0"); problematic.Set(4, 0, "RDBOBA00413P16-147");
        string problematicSource = SaveTable("input-pendencias", problematic);
        window.Session.AddFile(problematicSource); window.Session.SetExcel(problematicSource, "Dados", 1);
        window.Session.Data!.Obj("cadastro").Obj("fallback_idade")["ativo"] = true;
        window.Session.Meta["batch_inputs"] = new JsonArray(problematicSource);
        int publishedBefore = window.Session.Meta.Arr("processing_results").Count;
        var failingProcessor = new ProcessorView(window, window.Session); window.SetTool(failingProcessor);
        await window.Execute(failingProcessor.Process);
        var issueDocument = window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "planting-issues:" + problematicSource);
        var issueBrowser = (TableBrowser)issueDocument.Content;
        Check(issueBrowser.Total == 2 && window.Session.Meta.Arr("processing_results").Count == publishedBefore,
            "falha abre todas as pendências sem publicar processamento parcial");
        var issueGrid = LogicalControls<DataGrid>(issueBrowser).Single(); issueGrid.SelectedItem = issueGrid.Items[0];
        Check(!LogicalControls<Button>(issueBrowser).Single(b => b.Content?.ToString() == "Excluir selecionadas…").IsEnabled,
            "relatório de pendências orienta correção na origem e é somente leitura");
        Check(ReferenceEquals(window.ToolContent.Content, failingProcessor), "abrir pendências preserva a ferramenta do processamento");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(issueBrowser, Path.Combine(root, "planting-issues.png"));
        var invalidAreas = input.Copy(); invalidAreas.Rows.RemoveAt(4);
        invalidAreas.Set(1, 6, 0d); invalidAreas.Set(2, 8, "inválido"); invalidAreas.Set(3, 7, 0d);
        string areaSource = SaveTable("input-areas-invalidas", invalidAreas);
        window.Session.AddFile(areaSource); window.Session.SetExcel(areaSource, "Dados", 1);
        window.Session.Meta["batch_inputs"] = new JsonArray(areaSource);
        window.Session.Data!.Obj("ajuste_altura")["h_mode"] = "MODEL_GENERAL";
        window.Session.Data!.Obj("ajuste_altura").Obj("modelo_geral")["caminho"] = Path.Combine(root, "modelo-inexistente.zip");
        var areaProcessor = new ProcessorView(window, window.Session); window.SetTool(areaProcessor);
        await window.Execute(areaProcessor.Process);
        var areaDocument = window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "area-issues:" + areaSource);
        var areaBrowser = (TableBrowser)areaDocument.Content;
        Check(areaBrowser.Total == 3 && window.Session.Meta.Arr("processing_results").Count == publishedBefore,
            "áreas inválidas são agrupadas antes de carregar RNA e não geram processamento parcial");
        Check(window.HistoryText.Text.Contains("Compr='0'") && LogicalControls<DataGrid>(areaBrowser).Single().Columns.Any(c => c.Header?.ToString() == "Comprimento original"),
            "diagnóstico de área identifica valores e colunas de origem");
        await window.OpenTable(mergedPath);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var columnDocument = window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "table:" + mergedPath);
        var columnBrowser = (TableBrowser)columnDocument.Content;
        columnBrowser.ColumnRemovalSelection = _ => Task.FromResult<string[]?>(["Fuste", "Fila"]);
        var removable = LogicalControls<DataGrid>(columnBrowser).Single().Columns.Single(c => c.Header?.ToString() == "Fila");
        var columnMenu = (ContextMenu)removable.HeaderStyle.Setters.OfType<Setter>().Single(s => s.Property == FrameworkElement.ContextMenuProperty).Value;
        columnMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Remover colunas…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !window.IsBusy);
        var columnsRemoved = TableStore.Read(new(window.Session.Resolve(mergedPath)));
        Check(columnsRemoved.Count == 6 && columnsRemoved.Column("Fuste") < 0 && columnsRemoved.Column("Fila") < 0,
            "menu do cabeçalho remove várias colunas mantendo todas as linhas");

        var mixed = input.Copy(); mixed.Rows.RemoveRange(3, mixed.Count - 3);
        int sourceField = mixed.Ensure("Fonte"), typeField = mixed.Ensure("Tipo"), treeField = mixed.Ensure("ID livre"), plotField = mixed.Ensure("ID da parcela");
        for (int row = 1; row < mixed.Count; row++)
        {
            mixed.Set(row, sourceField, "Banco histórico"); mixed.Set(row, typeField, row == 1 ? "IFC" : "IFPC");
            mixed.Set(row, treeField, "A1"); mixed.Set(row, plotField, "P1"); mixed.Set(row, mixed.Require("Cova"), 1d);
        }
        mixed.Set(1, mixed.Require("Dec_Compr"), null); mixed.Set(2, mixed.Require("Dec_Larg"), " ");
        string mixedSource = SaveTable("input-misto", mixed);
        window.Session.AddFile(mixedSource); window.Session.SetExcel(mixedSource, "Dados", 1);
        window.Session.Meta["batch_inputs"] = new JsonArray(mixedSource);
        window.Session.Data!.Obj("ajuste_altura")["h_mode"] = "KEEP_FILE";
        window.Session.Data!.Obj("processador")["inventory_type"] = "MISTO";
        window.Session.Data!.Obj("identificacao")["arvore"] = new JsonArray("ID livre");
        window.Session.Data!.Obj("identificacao")["parcela"] = new JsonArray("ID da parcela");
        window.Session.Data!.Obj("identificacao")["coluna_tipo_inventario"] = "Tipo";
        window.Session.Data!.Obj("arquivo_entrada").Obj("declinacoes")["comprimento_vazio_zero"] = true;
        window.Session.Data!.Obj("arquivo_entrada").Obj("declinacoes")["largura_vazio_zero"] = true;
        var mixedProcessor = new ProcessorView(window, window.Session); window.SetTool(mixedProcessor);
        await window.Execute(mixedProcessor.Process);
        var mixedRun = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("source_path") == mixedSource);
        var mixedPlots = TableStore.Read(new(window.Session.Resolve(mixedRun.S("path")), ProcessingHistory.Plots));
        Check(mixedRun.S("inventory_type") == "MISTO" && mixedPlots.Count == 3
            && Enumerable.Range(1, 2).Select(r => mixedPlots.Text(r, mixedPlots.Require("Tipo_Inventario"))).Distinct().Count() == 2,
            "processamento com chaves próprias e declinações vazias separa IFC/IFPC da mesma base");
        await window.OpenTable(mixedRun.S("path"), "ESTATISTICAS INV");
        var mixedDocument = window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.ContentId == "table:" + mixedRun.S("path") + ":ESTATISTICAS INV");
        var mixedView = (ProcessingReportView)mixedDocument.Content;
        Check(LogicalControls<ComboBox>(mixedView).Any(c => c.Items.Contains("Banco histórico · IFC") && c.Items.Contains("Banco histórico · IFPC")),
            "estatísticas oferecem seleção da base e do tipo sem totalizar inventários diferentes");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(mixedView, Path.Combine(root, "mixed-statistics.png"));
        await window.Session.Save(project);
        var reopenedSettings = new ProjectSession(); await reopenedSettings.Open(project);
        Check(reopenedSettings.Data!.At("identificacao.arvore")!.AsArray()[0]!.GetValue<string>() == "ID livre"
            && reopenedSettings.Data.At("arquivo_entrada.declinacoes").B("comprimento_vazio_zero"), "configurações de identificação e declinação persistem no projeto");
        window.Session.Data!["identificacao"] = InventoryIdentity.Defaults();
        window.Session.Data!.Obj("processador")["modo"] = "resumo_direto";
        window.Session.Data!.Obj("processador")["inventory_type"] = "IFPC";
        window.Session.Meta["batch_inputs"] = new JsonArray(source);
        var directProcessor = new ProcessorView(window, window.Session); window.SetTool(directProcessor);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(LogicalControls<CheckBox>(directProcessor).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Processor.RemoveDuplicates").IsVisible,
            "resumo direto exibe opção de remover duplicadas");
        await window.Execute(directProcessor.Process);
        var directRun = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("source_path") == source && r.Arr("sheets").Any(s => s.Display() == "DIRETO - PARCELAS"));
        Check(directRun.N("removed_duplicates") == 1 && directRun["totals"].N("trees") == 3,
            "resumo direto remove repetição antes das somas e registra contagem");
        var directPlots = TableStore.Read(new(window.Session.Resolve(directRun.S("path")),"DIRETO - PARCELAS"));
        Check(directPlots.Column("Data_Medicao") >= 0 && directPlots.Number(1,directPlots.Require("area_corr")) == 100,
            "resumo direto registra data e área para auditoria temporal");
        window.Session.Meta.Arr("processing_results").Remove(directRun);
        window.Session.Meta.Arr("processing_results").Insert(0,directRun);
        var auditTool=new InventoryAuditView(window,window.Session);window.SetTool(auditTool);
        await auditTool.Initialize();
        Check(LogicalControls<ComboBox>(auditTool).Single(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c)=="Audit.Result").SelectedItem?.ToString()?.Contains("DIRETO - PARCELAS")==true,
            "auditoria seleciona processamento recente pela data, mesmo com histórico fora de ordem");
        var originSheet=LogicalControls<ComboBox>(auditTool).Single(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c)=="Audit.OriginSheet");
        Check(originSheet.SelectedItem?.ToString()=="Dados" && ((FrameworkElement)originSheet.Parent).Visibility==Visibility.Collapsed,
            "origem lógica é usada diretamente e não exige escolher tabela interna Dados");
        LogicalControls<TextBox>(auditTool).Single(t=>System.Windows.Automation.AutomationProperties.GetAutomationId(t)=="Audit.Volume").Text="10";
        await window.Execute(auditTool.Run);
        var savedAudit=window.Session.Meta.Arr("analyses").OfType<JsonObject>().Single(a=>a["definition"].S("kind")=="inventory_audit");
        string auditPath=window.Session.Resolve(savedAudit["definition"].S("evidence_path"));
        Check(savedAudit["definition"].S("source_path")==source,"origem da auditoria vem do processamento selecionado e não do último registro do histórico");
        Check(TableStore.Tables(auditPath).Contains("AUDITORIA - EVOLUCAO") && TableStore.Read(new(auditPath,"AUDITORIA - ARVORES")).Count==4,
            "auditoria publica banco próprio de parcelas, evolução e árvores únicas sem alterar origem");
        Check(Dendro.Core.Configuration.ProjectSnapshots.HasPlot(savedAudit),"auditoria preserva gráfico de volume e idade na análise salva");
        await window.OpenTable(savedAudit["definition"].S("evidence_path"),"AUDITORIA - PARCELAS");
        await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var auditBrowser=(TableBrowser)window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d=>d.ContentId=="table:"+savedAudit["definition"].S("evidence_path")+":AUDITORIA - PARCELAS").Content;
        var auditGrid=LogicalControls<DataGrid>(auditBrowser).Single();
        var column=auditGrid.Columns.Single(c=>c.Header?.ToString()=="Volume_m3_ha");
        var sortMenu=(ContextMenu)column.HeaderStyle.Setters.OfType<Setter>().Single(s=>s.Property==FrameworkElement.ContextMenuProperty).Value;
        sortMenu.Items.OfType<MenuItem>().Single(m=>m.Header?.ToString()=="Ordenar menor → maior").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(()=>!window.IsBusy);
        await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        auditGrid=LogicalControls<DataGrid>(auditBrowser).Single();
        Check(auditGrid.Columns.Single(c=>c.Header?.ToString()=="Volume_m3_ha").SortDirection==System.ComponentModel.ListSortDirection.Ascending,
            "cabeçalho ordena resultado inteiro e mostra direção da ordenação");
        var directColumns=new DirectColumnsView(window.Session.Data!.DeepClone().AsObject());
        var columnChoices=LogicalControls<CheckBox>(directColumns).ToArray();
        var optional=columnChoices.Single(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c)=="DirectColumn.parcela.Dq");optional.IsChecked=false;
        var mandatory=columnChoices.Single(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c)=="DirectColumn.parcela.Parcela_ID");
        Check(!mandatory.IsEnabled&&mandatory.IsChecked==true&&!directColumns.Read()["parcela"].B("Dq",true),
            "colunas diretas permitem desabilitar opcionais mantendo identificadores obrigatórios");
        var asset=new BiologicalAssetView(window,window.Session);window.SetTool(asset);await asset.Initialize();
        LogicalControls<ComboBox>(asset).Single(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c)=="BiologicalAsset.Base").SelectedItem=window.Session.ContentName(source);
        await Until(()=>!window.IsBusy);
        await window.Execute(asset.Run);
        string assetPath=window.Session.Files.Single(p=>Path.GetFileName(p)=="Ativo_biologico.dlb");
        var assetTable=TableStore.Read(new(window.Session.Resolve(assetPath),"Ativo biológico"));
        Check(assetTable.Count==input.Count && assetTable.Text(1,assetTable.Require("AB_Vinculo"))=="Vinculado" && assetTable.Number(1,assetTable.Require("INV_Volume_m3_ha"))==200,
            "ativo biológico preserva linhas da base e traz volume da medição mais recente da chave");
        File.WriteAllText(Path.Combine(root, "workflow-ui.log"), $"{checks} verificações do fluxo completo passaram.");
        window.Close();
    }
}
