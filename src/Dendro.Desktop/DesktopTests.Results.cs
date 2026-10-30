using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static async Task RunResultPresentation(string root)
    {
        Directory.CreateDirectory(root);
        var window = new MainWindow { WorkspacePath = Path.Combine(root, "layout.xml"), CloseConfirmation = () => Task.FromResult(true) };
        await window.Session.New("Apresentação de resultados");
        FrameworkElement[] helpScreens = [new ProjectView(window.Session), new ProcessorView(window, window.Session),
            new ProcessingReviewView(window, window.Session), new GrowthValidationView(window, window.Session),
            new GrowthComparisonView(window, window.Session), new IsolationForestView(window, window.Session),
            new FrontierView(window, window.Session), new StatisticalTestsView(window, window.Session), new IntegratedCleaningView(window, window.Session),
            new RegressionCatalogView(window.Session.Data!.DeepClone().AsObject()), new TableToolsView(window, window.Session, true),
            new TableToolsView(window, window.Session, false), new TableToolsView(window, window.Session, false, calculateColumns: true),
            new ChartView(window, window.Session, true), new ChartView(window, window.Session, false)];
        foreach (var screen in helpScreens)
        {
            var help = LogicalControls<HelpIcon>(screen).ToArray();
            Check(help.Any(h => h.Text.Length > 0 && h.Width == 20 && h.CornerRadius.TopLeft == 10 && h.ToolTip is ToolTip),
                screen.GetType().Name + " oferece explicações em ícones redondos com tooltip");
            Check(!LogicalControls<TextBlock>(screen).Any(t => t.FontSize == 11 && t.Text.Length > 72),
                screen.GetType().Name + " não mostra parágrafos explicativos em letras miúdas");
        }
        var toolCards = ((ListBox)window.ToolContent.Content).Items.OfType<ListBoxItem>().ToArray();
        Check(toolCards.Select(ShellVisuals.GetIcon).Distinct().Count() == toolCards.Length, "cada ferramenta tem seu próprio ícone");
        CaptureIconAtlas(root);
        var selectedTool = Ui.Text("Ferramenta escolhida pelo usuário"); window.SetTool(selectedTool);
        var definition = new JsonObject { ["kind"] = "increments", ["path"] = "",
            ["curves"] = JsonSerializer.SerializeToNode(new[] { new GrowthCurve("ICA IMA", "b0*t", [2d]) }),
            ["min_age"] = 1, ["max_age"] = 10, ["annual"] = false };
        var report = await AnalysisDocuments.Compute(window.Session, definition);
        AnalysisDocuments.Show(window, window.Session, report, definition);
        var entry = window.Session.Meta.Arr("analyses").OfType<JsonObject>().Single();
        Check(Dendro.Core.Configuration.ProjectSnapshots.HasPlot(entry), "ICA/IMA salva seu próprio gráfico");
        Check(window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Any(d => d.ContentId == "analysis-plot:" + entry.S("id")), "ICA/IMA abre documento próprio de gráfico");
        await AnalysisDocuments.OpenSaved(window, window.Session, entry);
        await AnalysisDocuments.OpenPlot(window, window.Session, entry);
        Check(ReferenceEquals(window.ToolContent.Content, selectedTool), "abrir informações e gráfico ICA/IMA preserva a ferramenta selecionada");
        string project = Path.Combine(root, "resultados.dendro");
        await window.Session.Save(project); await window.Session.Open(project);
        var restored = window.Session.Meta.Arr("analyses").OfType<JsonObject>().Single();
        await AnalysisDocuments.OpenPlot(window, window.Session, restored);
        Check(ReferenceEquals(window.ToolContent.Content, selectedTool), "gráfico reabre depois de salvar sem abrir ferramenta");

        string source = Path.Combine(root, "scatter.dlb");
        using (var writer = new CompactBankWriter(source))
        {
            string id = writer.AddTable(["X", "Y"]); writer.Add(id, [1d, 2d]); writer.Add(id, [2d, 3d]); writer.Complete();
        }
        window.Session.AddFile(source);
        var sourceNode = LogicalControls<TreeViewItem>(window.ContentsTree).Single(n => n.Tag as string == source);
        sourceNode.IsExpanded = false;
        foreach (var rootNode in window.ContentsTree.Items.OfType<TreeViewItem>()) rootNode.IsExpanded = false;
        await window.OpenChart(source, false, new JsonObject { ["path"] = source, ["x"] = "X", ["y"] = "Y", ["title"] = "Visualizar" }, false);
        Check(ReferenceEquals(window.ToolContent.Content, selectedTool), "abrir gráfico comum não abre sua ferramenta");
        Check(window.ContentsTree.Items.OfType<TreeViewItem>().All(n => !n.IsExpanded), "abrir gráfico preserva os grupos recolhidos nos Conteúdos");
        Check(!LogicalControls<TreeViewItem>(window.ContentsTree).Single(n => n.Tag as string == source).IsExpanded,
            "atualizar Conteúdos preserva tabela recolhida mesmo quando ganha um gráfico filho");
        var databaseNode = window.ContentsTree.Items.OfType<TreeViewItem>().Single(n => n.Header?.ToString() == "Bancos de dados");
        databaseNode.IsExpanded = true;
        window.Session.Touch();
        Check(window.ContentsTree.Items.OfType<TreeViewItem>().Single(n => n.Header?.ToString() == "Bancos de dados").IsExpanded
            && !window.ContentsTree.Items.OfType<TreeViewItem>().Single(n => n.Header?.ToString() == "Planilhas").IsExpanded,
            "atualização preserva ramos abertos e fechados individualmente");

        string staging = Path.Combine(window.Session.ProjectWorkFolder(), "direct.dlb");
        using (var writer = new CompactBankWriter(staging))
        {
            string id = writer.AddTable(["X", "Y"], "DIRETO - ARVORES"); writer.Add(id, [1d, 2d]); writer.Add(id, [2d, 3d]);
            string plots = writer.AddTable(["AB"], "DIRETO - PARCELAS"); writer.Add(plots, [3d]); writer.Complete();
        }
        await window.Session.AddTableResult(new(staging, "DIRETO - ARVORES", 2));
        string output = window.Session.Files.Single(p => Path.GetFileName(p) == "direct.dlb");
        window.Session.Meta.Arr("processing_results").Add(new JsonObject { ["source_path"] = source, ["path"] = output,
            ["sheets"] = new JsonArray("DIRETO - ARVORES", "DIRETO - PARCELAS") }); window.Session.Touch();
        await window.OpenAnomalyTool(output, "DIRETO - ARVORES");
        var anomalyTool = (IsolationForestView)window.ToolContent.Content;
        var featureList = LogicalControls<ListBox>(anomalyTool).Single();
        featureList.SelectedItems.Add("X"); featureList.SelectedItems.Add("Y");
        LogicalControls<Button>(anomalyTool).Single(b => b.Content?.ToString() == "Treinar modelo e gerar gráfico")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(window.Session.Meta.Arr("anomaly_models").Count == 1, "Isolation Forest treina e salva modelo pela interface");
        var anomaly = window.Session.Meta.Arr("analyses").OfType<JsonObject>().Last();
        Check(anomaly.Obj("definition").S("kind") == "isolation_forest" && Dendro.Core.Configuration.ProjectSnapshots.HasPlot(anomaly),
            "Isolation Forest gera relatório e gráfico próprio");
        var directNode = LogicalControls<TreeViewItem>(window.ContentsTree).Single(n => n.Tag is MainWindow.ResultSheetReference r && r.Path == output && r.Sheet == "DIRETO - ARVORES");
        var anomalyNode = directNode.Items.OfType<TreeViewItem>().Single(n => n.Tag is AnalysisReference a && a.Entry.S("id") == anomaly.S("id"));
        Check(anomalyNode.Items.OfType<TreeViewItem>().Any(n => n.Tag is AnalysisPlotReference)
            && anomalyNode.Items.OfType<TreeViewItem>().Any(n => n.Tag is AnalysisTableReference), "gráfico e tabela do Isolation Forest aparecem no ramo DIRETO correto");
        window.SetTool(selectedTool);
        await AnalysisDocuments.OpenPlot(window, window.Session, anomaly);
        Check(ReferenceEquals(window.ToolContent.Content, selectedTool), "abrir gráfico de anomalias preserva ferramenta selecionada");
        await AnalysisDocuments.OpenResultTable(window, window.Session, anomaly);
        Check(ReferenceEquals(window.ToolContent.Content, selectedTool)
            && window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Any(d => d.ContentId == "analysis-table:" + anomaly.S("id")),
            "tabela de escores abre pelos Conteúdos sem abrir ferramenta");
        int beforeNormals=window.Session.Files.Count();
        await AnalysisDocuments.CreateNormalChart(window,window.Session,anomaly);
        string normalsPath=window.Session.Files.Last();var normals=TableStore.Read(new(window.Session.Resolve(normalsPath),"Normais"));
        Check(window.Session.Files.Count()==beforeNormals+1 && Enumerable.Range(1,normals.Count-1).All(r=>normals.Text(r,normals.Require("IF_Classificacao"))=="Normal"),
            "gráfico derivado do Isolation Forest contém apenas normais e preserva análise original");

        Dendro.Core.Configuration.ProjectModels.SetVolume(window.Session.Data!, -1, "ID01", 1, 2, 3, "Modelo", "", null, "VTCC");
        Dendro.Core.Configuration.ProjectModels.SetVolume(window.Session.Data!, -1, "ID01", 4, 5, 6, "Modelo", "", null, "VCCC");
        var volumeView = new ModelsView(window, window.Session, false);
        var rows = (System.Data.DataView)LogicalControls<DataGrid>(volumeView).Single().ItemsSource;
        Check(rows[0]["ID"].ToString() == "ID01" && rows[0]["Tipo de volume"].ToString() == "VTCC" && rows[1]["Tipo de volume"].ToString() == "VCCC",
            "cadastro mostra tipo VTCC/VCCC sem deslocar ID e coeficientes");

        await window.Execute(() => Task.CompletedTask);
        Check(window.Progress.Value == 100 && window.ProgressDisplay.Visibility == Visibility.Visible, "progresso concluído permanece visível em 100%");
        await Task.Delay(TimeSpan.FromSeconds(10.3));
        Check(window.ProgressDisplay.Visibility == Visibility.Collapsed, "progresso some depois de dez segundos");
        var gate = new TaskCompletionSource();
        Task operation = window.Execute(() => gate.Task);
        Check(window.ProgressDisplay.Visibility == Visibility.Visible && window.Progress.Value == 0, "nova operação reapresenta e reinicia progresso");
        gate.SetResult(); await operation;
        window.Show();
        window.SetRibbonPinned(false);window.UpdateLayout();
        Check(!window.RibbonExpanded && window.Ribbon.ActualHeight <= 40,"desafixar menu libera altura e mantém abas acessíveis");
        window.SetRibbonExpanded(true);window.UpdateLayout();
        Check(window.RibbonExpanded,"menu desafixado abre temporariamente");
        window.SetRibbonExpanded(false);
        string compactLayout=Path.Combine(root,"compact-layout.xml");window.SaveWorkspace(compactLayout);
        window.SetRibbonPinned(true);window.LoadWorkspace(compactLayout);
        Check(!window.RibbonPinned && !window.RibbonExpanded,"layout restaura preferência do menu desafixado");
        window.SetRibbonPinned(true);
        await TestInlineReportSettings(window, root);
        var filterChart=new ChartView(window,window.Session,false);
        var filterConfig=new JsonObject { ["path"]=source,["x"]="X",["y"]="Y",["title"]="Filtro de regressão QA" };
        await filterChart.Load(source,filterConfig);await filterChart.Generate();
        var filterFit=new ChartView(window,window.Session,true);window.SetTool(filterFit);
        await filterFit.Load(source,filterChart.CurrentConfig);await filterFit.Generate();
        var filterWorkspace=(ChartWorkspace)window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d=>d.ContentId==ChartView.Identity(filterFit.CurrentConfig)).Content;
        var filterBody=filterWorkspace.GraphBody;
        await filterFit.RegressionDataReady;
        Check(LogicalControls<TableBrowser>(filterWorkspace.DataContent!).Any(),"regressão apresenta dados em painel encaixável com filtros e paginação");
        var filterPlot=filterBody.Children.OfType<ScatterPlot>().Single();
        var dataBrowser=LogicalControls<TableBrowser>(filterWorkspace.DataContent!).Single();
        var linkedGrid=LogicalControls<DataGrid>(dataBrowser).Single();
        linkedGrid.SelectedItem=linkedGrid.Items[0];
        Check(filterPlot.SelectedRows.SequenceEqual(new[]{1}),"selecionar linha na tabela destaca o ponto correspondente no gráfico");
        filterPlot.SelectRows(new[]{2});
        Check(linkedGrid.SelectedItems.Count==1 && Convert.ToDouble(((System.Data.DataRowView)linkedGrid.SelectedItem)[1])==2,
            "selecionar ponto no gráfico destaca linha correspondente na tabela");
        var filtered=await filterFit.PreviewPointFilter(filterFit.CurrentConfig,filterPlot,[new("X",FilterOperator.Greater,"1")]);
        filterPlot.SelectRows(filtered.Rows);
        Check(filterPlot.SelectedRows.SequenceEqual(new[]{2}) && filterFit.CurrentConfig["excluded"]?.AsArray().Count is null or 0,
            "filtro destaca linhas no gráfico e não remove antes da confirmação");
        Check(LogicalControls<Button>(filterBody).Single(b=>b.Content?.ToString()=="Remover selecionados").IsEnabled,
            "seleção por filtro habilita confirmação da remoção");
        filterPlot.ConfirmRemoval();await Until(()=>!window.IsBusy && filterFit.CurrentConfig["excluded"]?.AsArray().Count==1);
        Check(filterFit.CurrentConfig["excluded"]!.AsArray()[0]!.GetValue<int>()==2,"confirmar remove somente a seleção filtrada da regressão");
        await filterFit.UndoRemoval();
        Check(filterFit.CurrentConfig["excluded"]!.AsArray().Count==0 && TableStore.Page(new(source)).Total==2,
            "desfazer restaura a regressão e tabela de origem continua intacta");
        var legendConfig=filterChart.CurrentConfig;legendConfig["stratum"]="Y";legendConfig["title"]="Legenda QA";legendConfig["x_tick_step"]=1d;
        await filterChart.Load(source,legendConfig);await filterChart.Generate();
        Check(filterChart.CurrentConfig["group_colors"]!.AsObject().Count==2 && filterChart.CurrentConfig.N("x_tick_step")==1,
            "gráfico estratificado preserva cores da legenda e intervalo manual das marcações");
        string comparisonSource=Path.Combine(root,"model-layout.dlb");
        using(var writer=new CompactBankWriter(comparisonSource))
        { string id=writer.AddTable(["X","Y"]);for(int i=1;i<=12;i++)writer.Add(id,[(double)i,3d+2*i]);writer.Complete(); }
        window.Session.AddFile(comparisonSource);
        var comparisonChart=new ChartView(window,window.Session,false);
        await comparisonChart.Load(comparisonSource,new JsonObject{["path"]=comparisonSource,["x"]="X",["y"]="Y",["title"]="Modelos ao lado"});
        await comparisonChart.Generate();
        var comparison=new ChartView(window,window.Session,true);window.SetTool(comparison);
        await comparison.Load(comparisonSource,comparisonChart.CurrentConfig);
        await comparison.Generate("fit",true);await comparison.RegressionDataReady;
        var comparisonDoc=window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d=>d.ContentId==ChartView.Identity(comparison.CurrentConfig));
        var workspace=(ChartWorkspace)comparisonDoc.Content;window.UpdateLayout();
        await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(window,Path.Combine(root,"modelos-inicial.png"));
        var modelPane=window.RegressionModelsPane!;var dataPane=window.RegressionDataPane!;
        Check(ReferenceEquals(modelPane.Root.Manager,window.DockManager) && ReferenceEquals(dataPane.Root.Manager,window.DockManager)
            && workspace.GraphBody.RowDefinitions.Count==2,"modelos e dados usam encaixe principal do aplicativo");
        Check(!LogicalControls<Button>(workspace).Any(b=>b.Content?.ToString()=="Mostrar modelos"),"gráfico não contém botão Mostrar modelos");
        var comparisonTable=LogicalControls<DataGrid>(workspace.ModelsContent!).Single();
        comparisonTable.SelectedIndex=1;
        Check(comparison.CurrentConfig.N("selected_fit",-1)==1,"selecionar modelo no painel lateral atualiza curvas");
        ((LayoutAnchorablePane)modelPane.Parent).Children.Remove(modelPane);
        ((LayoutAnchorablePane)window.ContentsPane.Parent).Children.Add(modelPane);modelPane.IsSelected=true;
        ((LayoutAnchorablePane)dataPane.Parent).Children.Remove(dataPane);
        ((LayoutAnchorablePane)window.ToolsPane.Parent).Children.Add(dataPane);dataPane.IsSelected=true;
        Check(modelPane.Parent==window.ContentsPane.Parent && dataPane.Parent==window.ToolsPane.Parent,
            "modelos agrupam com Conteúdos e dados agrupam com Ferramentas em abas");
        modelPane.ToggleAutoHide();window.UpdateLayout();
        Check(modelPane.IsAutoHidden,"modelos podem ser desafixados no encaixe principal");
        modelPane.ToggleAutoHide();modelPane.Hide();modelPane.Show();modelPane.IsSelected=true;
        Check(!modelPane.IsHidden,"painel pode fechar e reabrir sem botão sobre o gráfico");
        dataPane.Float();await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(dataPane.IsFloating,"dados podem ser destacados para janela flutuante");
        dataPane.Dock();await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(!dataPane.IsFloating,"dados retornam ao encaixe após flutuar");
        modelPane.ToggleAutoHide();
        window.Document("other-chart-test","Outro documento",Ui.Text("Outra tabela"));
        Check(modelPane.IsHidden && dataPane.IsHidden,"outro documento recolhe painéis da regressão");
        comparisonDoc.IsActive=true;window.UpdateLayout();
        Check(ReferenceEquals(window.RegressionModelsPane!.Content,workspace.ModelsContent) && ReferenceEquals(window.RegressionDataPane!.Content,workspace.DataContent),
            "voltar ao gráfico recupera os respectivos modelos e dados");
        Check(modelPane.IsAutoHidden && !window.ContentsPane.IsHidden && !window.ToolsPane.IsHidden,
            "trocar documentos preserva alfinete e os demais painéis agrupados");
        modelPane.ToggleAutoHide();
        string regressionLayout=Path.Combine(root,"regression-layout.xml");window.SaveWorkspace(regressionLayout);
        window.LoadWorkspace(regressionLayout);
        await comparison.Generate("fit",true);await comparison.RegressionDataReady;
        Check(window.RegressionModelsPane!.Parent==window.ContentsPane.Parent && window.RegressionDataPane!.Parent==window.ToolsPane.Parent,
            "layout preserva agrupamento de modelos e dados ao reabrir");
        window.ResetWorkspace();window.UpdateLayout();
        Check(window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Count(p=>p.ContentId=="regression-models")==1
            && window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Count(p=>p.ContentId=="regression-data")==1,
            "restaurar layout mantém um único par de painéis ligado ao gráfico");
        await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.SetRibbonPinned(false);window.UpdateLayout();
        Capture(window,Path.Combine(root,"modelos-lateral.png"));
        window.RegressionModelsPane!.IsActive=true;
        await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var activePanel=Controls<AvalonDock.Controls.LayoutAnchorableControl>(window).Single(c=>ReferenceEquals(c.Model,window.RegressionModelsPane));
        var activeHeader=(Border)activePanel.Template.FindName("Header",activePanel);
        Check(activeHeader.Background is System.Windows.Media.SolidColorBrush activeBrush && activeBrush.Color==System.Windows.Media.Color.FromRgb(33,102,157),
            "cabeçalho do painel ativo fica azul e acompanha os controles brancos do tema");
        Capture(window,Path.Combine(root,"painel-ativo.png"));
        window.ToolsPane.IsActive=true;
        await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(activeHeader.Background is System.Windows.Media.SolidColorBrush inactiveBrush && inactiveBrush.Color!=System.Windows.Media.Color.FromRgb(33,102,157),
            "painel anterior perde destaque ao selecionar outro painel");
        var reverseChart=new JsonObject{["path"]=comparisonSource,["sheet"]="Dados",["x"]="Y",["y"]="X",["title"]="Eixos invertidos"};
        window.Session.StoreChart(reverseChart);
        var cleaner=new IntegratedCleaningView(window,window.Session);window.SetTool(cleaner);
        await cleaner.Initialize(comparisonSource);await cleaner.Start();
        var cleaningDocument=cleaner.LastDocument!;var linked=cleaningDocument.Plots;
        Check(linked.Count>=2 && cleaningDocument.Cleaning.RowCount==12,"Limpeza integrada vincula gráficos salvos da mesma tabela");
        linked[0].SelectRows([2]);
        Check(linked.All(p=>p.SelectedRows.SequenceEqual(new[]{2})),"seleção de uma observação aparece em todos os gráficos vinculados");
        linked[0].ConfirmRemoval();
        Check(cleaningDocument.Cleaning.RemovedCount==1 && linked.All(p=>p.Data["points"]!.AsArray().Single(n=>n.N("row")==2).B("excluded")),
            "remover ponto em um gráfico remove a mesma linha de todos os demais");
        cleaningDocument.Undo();Check(cleaningDocument.Cleaning.RemovedCount==0,"desfazer recupera a linha em toda a limpeza integrada");
        linked[1].SelectRows([1,3]);linked[1].ConfirmRemoval();
        string cleanExport=Path.Combine(root,"cleaning-export.dlb");await cleaningDocument.ExportTo(cleanExport);
        Check(TableStore.Page(new(cleanExport)).Total==10 && TableStore.Page(new(comparisonSource)).Total==12,
            "exportar limpeza preserva dez linhas e mantém doze linhas na tabela original");
        await cleaningDocument.SaveToProject();
        string cleanReference=window.Session.Files.Single(p=>Path.GetFileName(p)=="model-layout_limpa.dlb");
        Check(TableStore.Page(new(window.Session.Resolve(cleanReference))).Total==10,"tabela limpa salva no banco do projeto aparece como nova tabela");
        window.UpdateLayout();await Application.Current.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(window,Path.Combine(root,"limpeza-integrada.png"));
        File.WriteAllText(Path.Combine(root, "results-ui.log"), $"{checks} verificações passaram.");
        window.Close();
    }

    private static void CaptureIconAtlas(string root)
    {
        var content = new StackPanel { Margin = new Thickness(28) };
        content.Children.Add(Ui.Text("DendroLab · sistema de ícones", 24));
        content.Children.Add(Ui.Text("Grade vetorial de 24 unidades · conferência em 16, 24 e 32 px", 12));
        var cards = new WrapPanel { Width = 1100, Margin = new Thickness(0, 14, 0, 0) }; content.Children.Add(cards);
        foreach (var kind in Enum.GetValues<CommandIconKind>())
        {
            var item = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var sizes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 40 };
            foreach (double size in new[] { 16d, 24d, 32d }) sizes.Children.Add(new CommandIcon { Kind = kind, Width = size, Height = size, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(sizes);
            item.Children.Add(new TextBlock { Text = kind.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            cards.Children.Add(new Border { Width = 170, Height = 112, Margin = new Thickness(5), Padding = new Thickness(8), CornerRadius = new CornerRadius(12),
                Background = System.Windows.Media.Brushes.White, BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)), BorderThickness = new Thickness(1), Child = item });
        }
        var canvas = new Border { Width = 1160, Height = 910, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(246, 248, 252)), Child = content };
        canvas.Measure(new Size(1160, 910)); canvas.Arrange(new Rect(0, 0, 1160, 910)); canvas.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1160, 910, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(canvas);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, "icons-preview.png")); encoder.Save(stream);
    }
}
