using System.Data;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class TaperModelsView : DockPanel
{
    private sealed record Source(string Path, TableSource Table, string Label);
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source=Ui.Combo([]), tree=Ui.Combo([]), dap=Ui.Combo([]), total=Ui.Combo([]), height=Ui.Combo([]), diameter=Ui.Combo([]), stratum=Ui.Combo([]);
    private readonly CheckBox stratified=new(){Content="Ajustar separadamente por estrato"};
    private readonly Dictionary<string,CheckBox> models=new();
    private readonly TaperModel[] taperModels;
    private readonly DataGrid catalog=new();
    private readonly Button exportSavedButton, removeSavedButton;
    private readonly TextBlock status=Ui.Text("");
    private readonly TextBlock catalogStatus=Ui.Text("");
    private Source[] sources=[];
    private bool loading, initialized;
    internal TaperModelsView(MainWindow owner,ProjectSession session, bool catalogOnly = false)
    {
        this.owner=owner;this.session=session;
        taperModels=TaperAdjustment.ReadModels(session.Data!);
        Background=System.Windows.Media.Brushes.White;
        LastChildFill=true;
        FrameworkElement header;
        if(catalogOnly)
        {
            var catalogHeader=new StackPanel{Margin=new Thickness(8,6,8,4)};
            catalogHeader.Children.Add(Ui.Text("Ajustes salvos de taper",22));
            Ui.AddHelp(catalogHeader,"Catálogo do projeto para os ajustes de afilamento. Salve um modelo pelo botão direito sobre o ajuste nos resultados. Selecione uma linha para exportar os dados do ajuste ou removê-lo do catálogo.");
            header=catalogHeader;
        }
        else
        {
            var toolHeader=owner.ToolHeader("Ajuste de taper");
            toolHeader.Margin=new Thickness(10,8,10,5);
            header=toolHeader;
        }
        DockPanel.SetDock(header, Dock.Top); Children.Add(header);
        var form=Ui.Form();
        form.Children.Add(Ui.Text("Ajuste o diâmetro ao longo do fuste a partir de dados de cubagem. DAP e d em cm; H e h em metros.",13));
        Ui.Section(form,"Dados de cubagem");
        Ui.Field(form,"Tabela de origem",source);
        System.Windows.Automation.AutomationProperties.SetAutomationId(source,"Taper.Source");
        Ui.Field(form,"Identificador da árvore",tree);Ui.Field(form,"DAP (cm)",dap);
        Ui.Field(form,"Altura total H (m)",total);Ui.Field(form,"Altura da seção h (m)",height);Ui.Field(form,"Diâmetro da seção d (cm)",diameter);
        form.Children.Add(stratified);Ui.Field(form,"Campo de estrato",stratum);stratum.IsEnabled=false;
        stratified.Click+=(_,_)=>stratum.IsEnabled=stratified.IsChecked==true;
        Ui.Section(form,"Modelos para ajustar");
        foreach(var model in taperModels)
        {
            var check=new CheckBox{Content=model.Name,IsChecked=true,ToolTip=model.Formula};models[model.Id]=check;form.Children.Add(check);
        }
        Ui.AddHelp(form,"O ajuste usa ID preenchido, DAP > 0, H > 1,3 m, 0 ≤ h < H e d > 0. Medições inválidas ficam na tabela de saída e são contabilizadas. Bias = observado − estimado. Os coeficientes são ajustados na escala original do diâmetro, por mínimos quadrados não lineares.");
        form.Children.Add(status);
        var host=new DockPanel();
        var footer=Ui.ActionFooter(Ui.AsyncButton("Ajustar modelos",()=>owner.Execute(Run),Ui.ActionEmphasis.Primary));
        DockPanel.SetDock(footer,Dock.Bottom);host.Children.Add(footer);host.Children.Add(Ui.Scroll(form));
        var saved=new DockPanel();
        var actions=new WrapPanel{Margin=new Thickness(8,1,8,4)};
        exportSavedButton=Ui.IconButton("Exportar ajuste selecionado para XLSX",CommandIconKind.Export,()=>_=owner.Execute(ExportSaved));
        removeSavedButton=Ui.IconButton("Remover ajuste selecionado",CommandIconKind.Remove,()=>_=owner.Execute(RemoveSaved));
        actions.Children.Add(exportSavedButton);actions.Children.Add(removeSavedButton);
        DockPanel.SetDock(actions,Dock.Top);saved.Children.Add(actions);
        catalogStatus.Margin=new Thickness(8,4,8,8);DockPanel.SetDock(catalogStatus,Dock.Bottom);saved.Children.Add(catalogStatus);
        catalog.AutoGenerateColumns=false;catalog.IsReadOnly=true;catalog.CanUserAddRows=false;catalog.SelectionMode=DataGridSelectionMode.Single;catalog.SelectionUnit=DataGridSelectionUnit.FullRow;catalog.MinColumnWidth=75;catalog.RowHeight=32;catalog.EnableColumnVirtualization=false;
        foreach(var column in new[]{("ID","ID",110d),("Modelo","Modelo",175d),("Grupo","Grupo",130d),("N","N",65d),("Árvores","Árvores",80d),("Status","Status",100d),("R²","R²",85d),("RMSE (cm)","RMSE (cm)",100d),("Bias (%)","Bias (%)",95d),("Coeficientes","Coeficientes",220d),("Origem","Origem",170d),("Data","Data",150d)})
            catalog.Columns.Add(new DataGridTextColumn{Header=column.Item1,Binding=new System.Windows.Data.Binding(column.Item2),Width=column.Item3});
        catalog.SelectionChanged+=(_,_)=>UpdateCatalogActions();saved.Children.Add(catalog);
        RefreshCatalog();
        Children.Add(catalogOnly ? saved : host);
        source.SelectionChanged+=async(_,_)=>{if(!loading)await LoadColumns();};
        Loaded+=async(_,_)=>{session.Changed+=SessionChanged;if(!initialized){initialized=true;await Initialize();}RefreshCatalog();};
        Unloaded+=(_,_)=>session.Changed-=SessionChanged;
        RefreshCatalog();
    }
    internal async Task Initialize()
    {
        try
        {
            loading=true;
            string[] paths=session.Files.Where(session.IsTabular).ToArray();
            // Match the Processador: one selectable source per tabular item in Contents,
            // with the worksheet/table and header already configured for that item.
            var tables=session.TableSources(paths);
            sources=paths.Select((path,index)=>new Source(path,tables[index],session.ContentName(path))).ToArray();
            source.ItemsSource=sources.Select(s=>s.Label).ToArray();source.SelectedIndex=sources.Length>0?0:-1;
            loading=false;await LoadColumns();
            if(sources.Length==0)status.Text="Adicione uma tabela de cubagem ao projeto para começar.";
        }
        catch(Exception ex){loading=false;status.Text=ex.Message;}
    }
    private async Task LoadColumns()
    {
        if(source.SelectedIndex<0)return;
        var selected=sources[source.SelectedIndex];
        try
        {
            var columns=await Task.Run(()=>{using var cursor=new TableCursor(selected.Table);return cursor.Columns;});
            if(source.SelectedIndex<0 || !ReferenceEquals(sources[source.SelectedIndex],selected))return;
            void Set(ComboBox combo,params string[] names){combo.ItemsSource=columns;combo.SelectedItem=names.FirstOrDefault(n=>columns.Contains(n))??names.Select(n=>columns.FirstOrDefault(c=>c.Equals(n,StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n=>n!=null);}
            Set(tree,"Arvore","Árvore","arvore","TreeID","ID");Set(dap,"DAP","dap");Set(total,"H","HT","H_total","Altura_total");Set(height,"h","h_secao","Altura_secao");Set(diameter,"d","d_secao","Diametro");Set(stratum,"Estrato","Talhao","Talhão");
            status.Text="";
        }
        catch(Exception ex){status.Text=ex.Message;}
    }
    private async Task Run()
    {
        if(session.FilePath.Length==0)throw new InvalidOperationException("Salve o projeto antes de ajustar modelos de afilamento.");
        if(source.SelectedIndex<0)throw new InvalidOperationException("Selecione a tabela de cubagem.");
        string Required(ComboBox input)=>input.SelectedItem as string??throw new InvalidOperationException("Mapeie todas as colunas obrigatórias.");
        var options=new TaperOptions(Required(tree),Required(dap),Required(total),Required(height),Required(diameter),stratified.IsChecked==true?Required(stratum):"",models.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray());
        var selected=sources[source.SelectedIndex];
        string stage=Path.Combine(session.ProjectWorkFolder(),"Afilamento_"+Guid.NewGuid().ToString("N")+".dlb");
        var before=session.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cancellation=owner.OperationCancellation;var progress=owner.StartTableOperation("Ajustando modelos de afilamento…");
        try
        {
            var result=await Task.Run(()=>TaperAdjustment.Run(selected.Table,options,stage,taperModels,cancellation,progress));
            cancellation.ThrowIfCancellationRequested();
            await session.AddTableResults(result.Tables,selected.Path,selected.Table.Table);
            string reference=session.Files.First(path=>!before.Contains(path));
            session.RenameContent(reference,"Afilamento · "+session.ContentName(selected.Path));
            var fits=result.Fits.OfType<JsonObject>().Select(fit=>fit.DeepClone().AsObject()).ToArray();
            foreach(var fit in fits)
            {
                fit["path"]=reference;fit["source_path"]=selected.Path;fit["source_table"]=selected.Table.Table;fit["created"]=DateTimeOffset.Now.ToString("O");
                fit["tree_column"]=options.Tree;fit["dap_column"]=options.Dap;fit["total_height_column"]=options.TotalHeight;fit["section_height_column"]=options.Height;fit["diameter_column"]=options.Diameter;fit["stratum_column"]=options.Stratum;
            }
            status.Text=$"{result.Rows:N0} medições · {result.Invalid:N0} inválidas · {result.Fits.Count} ajustes. Dados, parâmetros e estatísticas salvos no projeto.";
            owner.Log(status.Text);
            owner.Document("taper-results:"+reference,"Afilamento · resultados",new TaperFitResultsView(owner,session,fits));
        }
        finally{if(File.Exists(stage))File.Delete(stage);}
    }
    private void RefreshCatalog()
    {
        var table=new System.Data.DataTable();
        foreach(string name in new[]{"ID","Modelo","Grupo","N","Árvores","Status","R²","RMSE (cm)","Bias (%)","Coeficientes","Origem","Data"})table.Columns.Add(name);
        foreach(var fit in session.Meta.Arr("taper_fits").OfType<JsonObject>())
            table.Rows.Add(fit.S("id"),fit.S("name"),fit.S("group"),fit.N("n"),fit.N("trees"),fit.S("status"),fit["r2"]?.ToString(),fit["rmse"]?.ToString(),fit["bias_pct"]?.ToString(),fit["coefficients"]?.ToJsonString(),session.ContentName(fit.S("source_path")),DateLabel(fit.S("created")));
        catalog.ItemsSource=table.DefaultView;
        int count=session.Meta.Arr("taper_fits").Count;
        catalogStatus.Text=count==0
            ? "Nenhum ajuste salvo. Nos resultados, clique com o botão direito em um ajuste OK para salvá-lo no projeto."
            : $"{count:N0} ajuste(s) salvo(s) no projeto. Selecione uma linha para exportar os dados ou remover o ajuste.";
        UpdateCatalogActions();
    }
    private void SessionChanged()
    {
        if(Dispatcher.CheckAccess())RefreshCatalog();
        else Dispatcher.BeginInvoke(RefreshCatalog);
    }
    private void UpdateCatalogActions()
    {
        if(exportSavedButton==null)return;
        bool selected=catalog.SelectedItem is DataRowView row&&session.Meta.Arr("taper_fits").OfType<JsonObject>().Any(f=>f.S("id")==row["ID"].ToString());
        exportSavedButton.IsEnabled=selected;removeSavedButton.IsEnabled=selected;
    }
    private static string DateLabel(string value)=>DateTimeOffset.TryParse(value,out var date)?date.ToLocalTime().ToString("g"):value;
    private async Task ExportSaved()
    {
        if(catalog.SelectedItem is not DataRowView row)throw new InvalidOperationException("Selecione um ajuste salvo.");
        var fit=session.Meta.Arr("taper_fits").OfType<JsonObject>().First(f=>f.S("id")==row["ID"].ToString());
        string? target=Ui.SaveFile("Planilha Excel|*.xlsx","Afilamento.xlsx");
        if(target==null)return;
        string bank=session.Resolve(fit.S("path"));var cancellation=owner.OperationCancellation;
        owner.StartTableOperation("Exportando resultados de afilamento…");
        await Task.Run(()=>
        {
            var sheets=new[]{"Dados","Parametros","Estatisticas"}.Select(name=>new Dendro.Core.Spreadsheets.WorksheetData(name,TableStore.Read(new(bank,name)))).ToArray();
            cancellation.ThrowIfCancellationRequested();
            Dendro.Core.Configuration.ProjectStore.AtomicWrite(target,temp=>Dendro.Core.Spreadsheets.WorkbookWriter.Write(temp,sheets),true);
        },cancellation);
        owner.Log("Resultados de afilamento exportados em XLSX.");
    }
    private Task RemoveSaved()
    {
        if(catalog.SelectedItem is not DataRowView row)return Task.CompletedTask;
        string id=row["ID"].ToString()??"";
        var fit=session.Meta.Arr("taper_fits").OfType<JsonObject>().FirstOrDefault(item=>item.S("id")==id);
        if(fit==null)return Task.CompletedTask;
        if(!Ui.Confirm("Remover ajuste salvo de taper",$"Remover {fit.S("name")} · {fit.S("group")} do catálogo?","Remover",true))return Task.CompletedTask;
        session.Meta.Arr("taper_fits").Remove(fit);session.Touch();RefreshCatalog();owner.Log("Ajuste removido do catálogo de taper.");
        return Task.CompletedTask;
    }
}
