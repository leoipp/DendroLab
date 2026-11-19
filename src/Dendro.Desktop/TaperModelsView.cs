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
    private readonly DataGrid catalog=new();
    private readonly TextBlock status=Ui.Text("");
    private Source[] sources=[];
    private bool loading, initialized;
    internal TaperModelsView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner;this.session=session;
        Background=System.Windows.Media.Brushes.White;
        var tabs=new TabControl{Background=System.Windows.Media.Brushes.White};Children.Add(tabs);
        var form=Ui.Form();
        form.Children.Add(Ui.Text("Modelos de Afilamento",22));
        form.Children.Add(Ui.Text("Ajuste o diâmetro ao longo do fuste a partir de dados de cubagem. DAP e d em cm; H e h em metros.",13));
        Ui.Section(form,"Dados de cubagem");
        Ui.Field(form,"Tabela de origem",source);
        System.Windows.Automation.AutomationProperties.SetAutomationId(source,"Taper.Source");
        Ui.Field(form,"Identificador da árvore",tree);Ui.Field(form,"DAP (cm)",dap);
        Ui.Field(form,"Altura total H (m)",total);Ui.Field(form,"Altura da seção h (m)",height);Ui.Field(form,"Diâmetro da seção d (cm)",diameter);
        form.Children.Add(stratified);Ui.Field(form,"Campo de estrato",stratum);stratum.IsEnabled=false;
        stratified.Click+=(_,_)=>stratum.IsEnabled=stratified.IsChecked==true;
        Ui.Section(form,"Modelos para ajustar");
        foreach(var model in TaperAdjustment.Models)
        {
            var check=new CheckBox{Content=model.Name,IsChecked=true,ToolTip=model.Formula};models[model.Id]=check;form.Children.Add(check);
        }
        Ui.AddHelp(form,"O ajuste usa ID preenchido, DAP > 0, H > 1,3 m, 0 ≤ h < H e d > 0. Medições inválidas ficam na tabela de saída e são contabilizadas. Bias = observado − estimado. Os coeficientes são ajustados na escala original do diâmetro, por mínimos quadrados não lineares.");
        form.Children.Add(status);
        var host=new DockPanel();
        var footer=Ui.ActionFooter(Ui.AsyncButton("Ajustar modelos",()=>owner.Execute(Run),Ui.ActionEmphasis.Primary));
        DockPanel.SetDock(footer,Dock.Bottom);host.Children.Add(footer);host.Children.Add(Ui.Scroll(form));
        tabs.Items.Add(new TabItem{Header="Ajuste",Content=host});
        var saved=new DockPanel();
        var actions=new WrapPanel{Margin=new Thickness(8)};
        actions.Children.Add(Ui.AsyncButton("Exportar XLSX…",()=>owner.Execute(ExportSaved),Ui.ActionEmphasis.Secondary));
        actions.Children.Add(Ui.Button("Abrir estatísticas",()=>_=owner.Execute(()=>OpenSaved("Estatisticas")),Ui.ActionEmphasis.Secondary));
        actions.Children.Add(Ui.Button("Abrir parâmetros",()=>_=owner.Execute(()=>OpenSaved("Parametros")),Ui.ActionEmphasis.Secondary));
        actions.Children.Add(Ui.Button("Abrir dados estimados",()=>_=owner.Execute(()=>OpenSaved("Dados")),Ui.ActionEmphasis.Secondary));
        DockPanel.SetDock(actions,Dock.Top);saved.Children.Add(actions);saved.Children.Add(catalog);
        tabs.Items.Add(new TabItem{Header="Ajustes salvos",Content=saved});
        var definitions=new StackPanel{Margin=new Thickness(16)};
        foreach(var model in TaperAdjustment.Models){Ui.Section(definitions,model.Name);definitions.Children.Add(Ui.Text(model.Formula));}
        definitions.Children.Add(Ui.Text("Ormerod usa o coeficiente b1. Os demais coeficientes seguem b0…b3. São as cinco definições da referência IFPC; o otimizador nativo pode produzir diferenças numéricas em relação ao SciPy.",12));
        tabs.Items.Add(new TabItem{Header="Equações",Content=Ui.Scroll(definitions)});
        source.SelectionChanged+=async(_,_)=>{if(!loading)await LoadColumns();};
        catalog.AutoGeneratingColumn+=(_,e)=>{if(e.PropertyName=="ID")e.Cancel=true;};
        Loaded+=async(_,_)=>{if(!initialized){initialized=true;await Initialize();}RefreshCatalog();};RefreshCatalog();
    }
    private async Task Initialize()
    {
        try
        {
            loading=true;
            sources=await Task.Run(()=>
            {
                var available=new List<Source>();
                foreach(string path in session.Files.Where(session.IsTabular))
                {
                    try
                    {
                        foreach(string table in TableStore.Tables(session.Resolve(path)))
                            available.Add(new(path,new(session.Resolve(path),table,session.IsSpreadsheet(path)?(int)session.ExcelOptions(path).N("header_row",1):1),session.ContentName(path)+" · "+table));
                    }
                    catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                }
                return available.ToArray();
            });
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
            var result=await Task.Run(()=>TaperAdjustment.Run(selected.Table,options,stage,cancellation,progress));
            cancellation.ThrowIfCancellationRequested();
            await session.AddTableResults(result.Tables,selected.Path,selected.Table.Table);
            string reference=session.Files.First(path=>!before.Contains(path));
            session.RenameContent(reference,"Afilamento · "+session.ContentName(selected.Path));
            foreach(var fit in result.Fits.OfType<JsonObject>())
            {
                fit["path"]=reference;fit["source_path"]=selected.Path;fit["source_table"]=selected.Table.Table;fit["created"]=DateTimeOffset.Now.ToString("O");
                fit["tree_column"]=options.Tree;fit["dap_column"]=options.Dap;fit["total_height_column"]=options.TotalHeight;fit["section_height_column"]=options.Height;fit["diameter_column"]=options.Diameter;fit["stratum_column"]=options.Stratum;
                session.Meta.Arr("taper_fits").Add(fit.DeepClone());
            }
            session.Touch();RefreshCatalog();
            status.Text=$"{result.Rows:N0} medições · {result.Invalid:N0} inválidas · {result.Fits.Count} ajustes. Dados, parâmetros e estatísticas salvos no projeto.";
            owner.Log(status.Text);await owner.OpenTable(reference,"Estatisticas");
        }
        finally{if(File.Exists(stage))File.Delete(stage);}
    }
    private void RefreshCatalog()
    {
        var table=new System.Data.DataTable();
        foreach(string name in new[]{"ID","Modelo","Grupo","N","Árvores","Status","R²","RMSE (cm)","Bias (%)","Coeficientes","Origem","Data"})table.Columns.Add(name);
        foreach(var fit in session.Meta.Arr("taper_fits").OfType<JsonObject>())
            table.Rows.Add(fit.S("id"),fit.S("name"),fit.S("group"),fit.N("n"),fit.N("trees"),fit.S("status"),fit["r2"]?.ToString(),fit["rmse"]?.ToString(),fit["bias_pct"]?.ToString(),fit["coefficients"]?.ToJsonString(),session.ContentName(fit.S("source_path")),fit.S("created"));
        catalog.ItemsSource=table.DefaultView;
    }
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
    private Task OpenSaved(string sheet)
    {
        if(catalog.SelectedItem is not DataRowView row)throw new InvalidOperationException("Selecione um ajuste salvo.");
        var fit=session.Meta.Arr("taper_fits").OfType<JsonObject>().First(f=>f.S("id")==row["ID"].ToString());
        return owner.OpenTable(fit.S("path"),sheet);
    }
}
