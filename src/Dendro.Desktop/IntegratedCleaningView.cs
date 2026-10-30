using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class IntegratedCleaningView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source=Ui.Combo([]),sheet=Ui.Combo([]),x=Ui.Combo([]),y=Ui.Combo([]);
    private readonly StackPanel graphs=new(),tables=new();
    private readonly List<(JsonObject Config,CheckBox Check)> choices=[];
    private string[] paths=[];private bool loading;
    internal IntegratedCleaningDocument? LastDocument {get;private set;}

    internal IntegratedCleaningView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner;this.session=session;VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        var form=Ui.Form();Content=form;form.Children.Add(owner.ToolHeader("Limpeza integrada"));
        Ui.Field(form,"Tabela de origem",source);Ui.Field(tables,"Tabela / aba",sheet);form.Children.Add(tables);
        Ui.Field(form,"Gráficos para vincular",new ScrollViewer{Content=graphs,MaxHeight=260,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        Ui.AddHelp(form,"Selecione dois ou mais gráficos da mesma tabela. A sessão usa cópias dos gráficos e todas as linhas atuais da tabela; exclusões e ajustes anteriores não são importados. Os gráficos originais e a tabela de origem são preservados.");
        Ui.Field(form,"Eixo X para adicionar um gráfico",x);Ui.Field(form,"Eixo Y",y);
        form.Children.Add(Ui.Button("Adicionar gráfico à limpeza",AddChart));
        form.Children.Add(Ui.AsyncButton("Iniciar limpeza integrada",()=>owner.Execute(Start)));
        Ui.AddHelp(form,"Clique ou desenhe um contorno para selecionar pontos. A seleção acompanha os demais gráficos; Remover seleção retira as mesmas linhas de todos eles. Desfazer recupera a última remoção. Exporte ou salve a tabela limpa antes de fechar a sessão temporária.");
        source.SelectionChanged+=async(_,_)=>{if(!loading&&source.SelectedIndex>=0)await owner.Execute(()=>LoadTables());};
        sheet.SelectionChanged+=async(_,_)=>{if(!loading&&sheet.SelectedIndex>=0)await owner.Execute(LoadCharts);};
    }

    internal async Task Initialize(string? initialPath=null,string? initialSheet=null)
    {
        loading=true;paths=session.Files.Where(session.IsTabular).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        source.ItemsSource=paths.Select(p=>session.ContentName(p)).ToArray();source.SelectedIndex=Array.IndexOf(paths,initialPath);loading=false;
        if(source.SelectedIndex>=0)await LoadTables(initialSheet);
    }

    private async Task LoadTables(string? preferred=null)
    {
        string path=paths[source.SelectedIndex];var names=await Task.Run(()=>TableStore.Tables(session.Resolve(path)));
        loading=true;sheet.ItemsSource=names;sheet.SelectedItem=names.Contains(preferred??"")?preferred:names.FirstOrDefault();tables.Visibility=names.Length>1?Visibility.Visible:Visibility.Collapsed;loading=false;
        if(sheet.SelectedIndex>=0)await LoadCharts();
    }

    private TableSource Source()
    {
        if(source.SelectedIndex<0||sheet.SelectedItem is not string table)throw new InvalidOperationException("Selecione uma tabela de origem.");
        string path=paths[source.SelectedIndex];return new(session.Resolve(path),table,session.IsSpreadsheet(path)?(int)session.ExcelOptions(path).N("header_row",1):1);
    }

    private async Task LoadCharts()
    {
        var selected=Source();var columns=await Task.Run(()=>{using var cursor=new TableCursor(selected);return cursor.Columns;});
        x.ItemsSource=y.ItemsSource=columns;x.SelectedIndex=columns.Length>0?0:-1;y.SelectedIndex=columns.Length>1?1:x.SelectedIndex;
        graphs.Children.Clear();choices.Clear();string reference=paths[source.SelectedIndex];
        foreach(var chart in session.Meta.Arr("charts").OfType<JsonObject>().Where(c=>
            string.Equals(session.Resolve(c.S("path")),selected.Path,StringComparison.OrdinalIgnoreCase)
            && (c.S("sheet")==selected.Table || c.S("sheet").Length==0&&sheet.SelectedIndex==0)
            && (int)c.N("header_row",1)==selected.HeaderRow))AddChoice(chart);
        if(choices.Count==0)graphs.Children.Add(Ui.Text("Adicione gráficos pelos eixos abaixo."));
    }

    private void AddChoice(JsonObject config)
    {
        var copy=config.DeepClone().AsObject();
        var check=new CheckBox{Content=copy.S("title")+" · "+copy.S("y")+" × "+copy.S("x"),IsChecked=true};
        choices.Add((copy,check));graphs.Children.Add(check);
    }

    private void AddChart()
    {
        if(x.SelectedItem is not string xc||y.SelectedItem is not string yc)return;
        if(choices.Count==0)graphs.Children.Clear();
        AddChoice(new JsonObject{["x"]=xc,["y"]=yc,["title"]=yc+" × "+xc,["x_label"]=xc,["y_label"]=yc,["point_size"]=3,["point_alpha"]=0.6,["grid"]=true});
    }

    internal async Task Start()
    {
        var selected=Source();var configs=choices.Where(c=>c.Check.IsChecked==true).Select(c=>c.Config.DeepClone().AsObject()).ToArray();
        if(configs.Length<2)throw new InvalidOperationException("Selecione ou adicione pelo menos dois gráficos.");
        string path=Path.Combine(Path.GetTempPath(),"DendroLab-cleaning-"+Guid.NewGuid().ToString("N")+".dlb");
        var cancellation=owner.OperationCancellation;var progress=owner.StartTableOperation("Preparando cópia e gráficos da limpeza integrada…");
        var cleaning=await Task.Run(()=>IntegratedCleaning.Create(selected,configs,path,cancellation,progress),cancellation);
        string reference=paths[source.SelectedIndex];
        try
        {
            LastDocument=new IntegratedCleaningDocument(owner,session,cleaning,reference,session.ContentName(reference));
            owner.Document("cleaning:"+Guid.NewGuid().ToString("N"),"Limpeza · "+session.ContentName(reference),LastDocument);
        }
        catch {cleaning.Dispose();throw;}
    }
}
