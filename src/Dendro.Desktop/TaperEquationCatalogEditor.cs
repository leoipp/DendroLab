using System.Text.Json.Nodes;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class TaperEquationCatalogEditor : ScrollViewer
{
    private readonly JsonObject project;
    private readonly Action? changed;
    private readonly TabControl groups=new();
    private readonly DataGrid[] grids=new DataGrid[4];
    private readonly TextBox id=Ui.Input(), name=Ui.Input(), formula=Ui.Input(), coefficients=Ui.Input("2"), initial=Ui.Input();
    private readonly HelpIcon hint=Ui.Help("");
    private readonly TextBlock error=Ui.Text("");
    private JsonObject? editing;
    private bool loading, draft;
    private int draftParameters=1;
    private JsonArray Equations=>project.Obj("taper").Arr("equations");
    private int Parameters=>Math.Max(0,groups.SelectedIndex)+1;

    internal TaperEquationCatalogEditor(JsonObject project,Action? changed=null)
    {
        this.project=project;this.changed=changed;
        if(Equations.Count==0)project.Obj("taper")["equations"]=TaperAdjustment.DefaultDefinitions();
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto;Background=System.Windows.Media.Brushes.White;Foreground=System.Windows.Media.Brushes.Black;
        var panel=Ui.Form();Content=panel;
        panel.Children.Add(Ui.Text("Equações de taper",22));
        Ui.AddHelp(panel,"Catálogo de equações deste projeto. As guias indicam a quantidade de coeficientes; use DAP, H, h, z (= h/H) e b0…b3.");
        for(int i=0;i<4;i++)
        {
            var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,Height=180,SelectionMode=DataGridSelectionMode.Single,MinColumnWidth=80,Foreground=System.Windows.Media.Brushes.Black};
            var active=new FrameworkElementFactory(typeof(CheckBox));
            active.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            active.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            active.SetBinding(CheckBox.IsCheckedProperty,new Binding("Active"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
            grid.Columns.Add(new DataGridTemplateColumn{Header="Ativado",Width=80,CellTemplate=new DataTemplate{VisualTree=active}});
            grid.Columns.Add(new DataGridTextColumn{Header="ID",Binding=new Binding("Id"),Width=170});
            grid.Columns.Add(new DataGridTextColumn{Header="Modelo",Binding=new Binding("Name"),Width=170});
            grid.Columns.Add(new DataGridTextColumn{Header="Fórmula",Binding=new Binding("Formula"),Width=500});
            grid.Columns.Add(new DataGridTextColumn{Header="Coeficientes",Binding=new Binding("Count"),Width=100});
            grid.SelectionChanged+=(_,_)=>{if(!loading&&grid.SelectedItem is Row row)Load(row.Model);};
            grids[i]=grid;groups.Items.Add(new TabItem{Header=$"{i+1}P",Content=grid});
        }
        groups.SelectedIndex=0;panel.Children.Add(groups);
        var actions=new WrapPanel();actions.Children.Add(Ui.Button("Novo modelo",New));actions.Children.Add(Ui.Button("Salvar modelo",SaveCurrent));
        actions.Children.Add(Ui.Button("Excluir modelo",()=>{if(editing!=null){Equations.Remove(editing);changed?.Invoke();Refresh();New();}}));panel.Children.Add(actions);
        Ui.AddHelp(panel,hint);panel.Children.Add(error);
        var identity=new Grid();identity.ColumnDefinitions.Add(new ColumnDefinition());identity.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(2,GridUnitType.Star)});
        var idPanel=new StackPanel{Margin=new Thickness(0,0,12,0)};Ui.Field(idPanel,"ID do modelo",id);identity.Children.Add(idPanel);
        var namePanel=new StackPanel();Ui.Field(namePanel,"Nome",name);Grid.SetColumn(namePanel,1);identity.Children.Add(namePanel);panel.Children.Add(identity);
        Ui.Field(panel,"Fórmula",formula);Ui.Field(panel,"Número de coeficientes (1 a 4)",coefficients);Ui.Field(panel,"Valores iniciais (separados por vírgula)",initial);
        foreach(var field in new[]{id,name,formula,coefficients,initial})field.TextChanged+=(_,_)=>{if(!loading)draft=true;};
        groups.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,groups)&&!loading){try{if(draft)SaveCurrent();New();}catch(Exception ex){loading=true;groups.SelectedIndex=draftParameters-1;loading=false;error.Text=ex.Message;}}};
        Refresh();New();
    }

    internal JsonArray Read(){if(draft)SaveCurrent();return Equations.DeepClone().AsArray();}
    private sealed record Row(JsonObject Model,Action? Changed=null)
    {
        public bool Active{get=>Model.B("ativado",true);set{Model["ativado"]=value;Changed?.Invoke();}}
        public string Id=>Model.S("id"); public string Name=>Model.S("name");public string Formula=>Model.S("formula");public int Count=>Model.Arr("parameters").Count;
    }
    private void Refresh()
    {
        loading=true;try{for(int i=0;i<4;i++)grids[i].ItemsSource=Equations.OfType<JsonObject>().Where(x=>x.Arr("parameters").Count==i+1).Select(x=>new Row(x,changed)).ToArray();}finally{loading=false;}
    }
    private void New()
    {
        loading=true;editing=null;draftParameters=Parameters;id.Text="taper_"+Guid.NewGuid().ToString("N")[..8];name.Text="";coefficients.Text=Parameters.ToString();
        formula.Text="DAP*sqrt(max("+string.Join("+",Enumerable.Range(0,Parameters).Select(i=>i==0?"b0":$"b{i}*z^{i}"))+",0))";
        initial.Text=string.Join(",",Enumerable.Repeat("0.1",Parameters));hint.Text=$"{Parameters}P: coeficientes b0 a b{Parameters-1}; DAP e alturas em metros/centímetros conforme descrição da ferramenta.";
        foreach(var grid in grids)grid.SelectedItem=null;draft=false;error.Text="";loading=false;
    }
    private void Load(JsonObject model)
    {
        loading=true;editing=model;draftParameters=model.Arr("parameters").Count;id.Text=model.S("id");name.Text=model.S("name");formula.Text=model.S("formula");coefficients.Text=draftParameters.ToString();initial.Text=string.Join(",",model.Arr("initial").Select(x=>x?.ToString()));draft=false;loading=false;
    }
    private void SaveCurrent()
    {
        int n=(int)Ui.Number(coefficients.Text);if(n is <1 or >4)throw new InvalidDataException("Use de 1 a 4 coeficientes.");
        var pars=Enumerable.Range(0,n).Select(i=>"b"+i).ToArray();var starts=initial.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if(starts.Length!=n||starts.Any(v=>!double.IsFinite(v)))throw new InvalidDataException("Informe um valor inicial finito para cada coeficiente.");
        _=new Formula(formula.Text,pars.Concat(["DAP","H","h","z"]));
        var item=new JsonObject{["id"]=id.Text.Trim(),["name"]=name.Text.Trim(),["formula"]=formula.Text.Trim(),["parameters"]=new JsonArray(pars.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["initial"]=new JsonArray(starts.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["ativado"]=editing?.B("ativado",true)??true};
        if(item.S("id").Length==0||item.S("name").Length==0||item.S("formula").Length==0)throw new InvalidDataException("ID, nome e fórmula são obrigatórios.");
        if(Equations.OfType<JsonObject>().Any(x=>!ReferenceEquals(x,editing)&&(x.S("id").Equals(item.S("id"),StringComparison.OrdinalIgnoreCase)||x.S("name").Equals(item.S("name"),StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("O ID e o nome devem ser únicos.");
        int index=editing==null?-1:Equations.IndexOf(editing);if(index<0)Equations.Add(item);else Equations[index]=item;changed?.Invoke();Refresh();Load(item);
    }
}
