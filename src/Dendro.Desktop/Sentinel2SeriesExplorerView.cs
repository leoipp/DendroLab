using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class Sentinel2SeriesExplorerView : ToolFormView
{
    private sealed record Observation(string Polygon,string Pixel,int Row,int Column,DateTimeOffset Date,double? Value);
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source=Ui.Combo([]),polygon=Ui.Combo([]),date=Ui.Combo([]);
    private readonly Canvas map=new(){Height=280,Background=Brushes.White,ClipToBounds=true};
    private readonly Canvas curve=new(){Height=200,Background=Brushes.White,ClipToBounds=true};
    private readonly TextBlock status=Ui.Text("Selecione uma série de pixels."),legend=Ui.Text("NDVI: vermelho −1 · amarelo 0 · verde +1. Cinza: sem dado válido."),curveTitle=Ui.Text("");
    private readonly List<TableSource> tables=[];
    private Observation[] observations=[];
    private string? selectedPixel;
    private bool initialized, loadingSources;
    internal Sentinel2SeriesExplorerView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner;this.session=session;
        var form=Ui.Form();SetForm(form);form.Children.Add(owner.ToolHeader("Explorar série temporal Sentinel-2"));
        Ui.Field(form,"Série de pixels",source);Ui.Field(form,"Polígono da série (registro do shapefile)",polygon);Ui.Field(form,"Aquisição UTC",date);
        form.Children.Add(map);form.Children.Add(legend);form.Children.Add(curveTitle);form.Children.Add(curve);form.Children.Add(status);
        date.ItemStringFormat="{0:dd/MM/yyyy HH:mm:ss} UTC";
        Ui.AddHelp(form,"Clique em um pixel para abrir sua curva. Sem seleção, a curva mostra a mediana do polígono em cada aquisição. Datas sem observações válidas interrompem a linha. O mapa mantém a posição dos pixels na grade da extração.");
        form.Children.Add(Ui.Button("Mostrar mediana do polígono",()=>{selectedPixel=null;DrawCurve();}));
        source.SelectionChanged+=async(_,_)=>{if(!loadingSources)await owner.Execute(LoadSeries);};
        polygon.SelectionChanged+=(_,_)=>{selectedPixel=null;RefreshDates();};date.SelectionChanged+=(_,_)=>DrawMap();
        map.SizeChanged+=(_,_)=>DrawMap();curve.SizeChanged+=(_,_)=>DrawCurve();
        Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await owner.Execute(Initialize);};
    }
    private async Task Initialize()
    {
        var paths=session.Files.Where(session.IsTabular).ToArray();
        var found=await Task.Run(()=>paths.SelectMany(p=>TableStore.Tables(session.Resolve(p)).Where(t=>t=="SERIE PIXELS NDVI").Select(t=>(Path:p,Table:new TableSource(session.Resolve(p),t)))).ToArray());
        loadingSources=true;
        try
        {
            tables.Clear();tables.AddRange(found.Select(f=>f.Table));
            source.ItemsSource=found.Select(f=>session.ContentName(f.Path)).ToArray();
            if(tables.Count>0)source.SelectedIndex=tables.Count-1;
        }
        finally{loadingSources=false;}
        if(source.SelectedIndex>=0)await LoadSeries();
        else status.Text="Nenhuma série encontrada. Execute Série de pixels Sentinel-2 primeiro.";
    }
    private async Task LoadSeries()
    {
        if(source.SelectedIndex<0)return;
        var table=tables[source.SelectedIndex];status.Text="Carregando série…";
        observations=await Task.Run(()=>{
            using var cursor=new TableCursor(table);
            int Col(string name)=>Array.IndexOf(cursor.Columns,name) is var i && i>=0?i:throw new InvalidOperationException("Coluna ausente: "+name);
            int p=Col("Poligono_ID"),px=Col("Pixel_ID"),r=Col("Linha_Grade"),c=Col("Coluna_Grade"),d=Col("Data_UTC"),v=Col("NDVI"),ok=Col("Valido");
            double Number(object? value)=>Convert.ToDouble(value,CultureInfo.InvariantCulture);
            return cursor.Rows().Select(row=>new Observation(Convert.ToString(row[p],CultureInfo.InvariantCulture)??"",row[px]?.ToString()??"",(int)Number(row[r]),(int)Number(row[c]),DateTimeOffset.Parse(row[d]?.ToString()??"",CultureInfo.InvariantCulture),row[v]!=null&&Number(row[ok])==1?Number(row[v]):null)).ToArray();
        });
        polygon.ItemsSource=observations.Select(o=>o.Polygon).Distinct().Order().ToArray();if(polygon.Items.Count>0)polygon.SelectedIndex=0;
        status.Text=$"{observations.Length:N0} observações · {observations.Count(o=>o.Value.HasValue):N0} válidas.";
    }
    private Observation[] Current()=>polygon.SelectedItem is string p?observations.Where(o=>o.Polygon==p).ToArray():[];
    private void RefreshDates()
    {
        var previous = date.SelectedItem is DateTimeOffset selected ? selected : (DateTimeOffset?)null;
        var dates = Current().Select(o=>o.Date).Distinct().Order().ToArray();
        date.ItemsSource = dates;
        if (previous.HasValue && dates.Contains(previous.Value)) date.SelectedItem = previous.Value;
        else if (dates.Length > 0) date.SelectedIndex = 0;
        // Replacing ItemsSource can retain an equal selected date without raising
        // SelectionChanged. Always redraw for the newly selected polygon.
        DrawMap();
        DrawCurve();
    }
    private static Brush ColorFor(double? value)
    {
        if(!value.HasValue)return Brushes.LightGray;
        double t=Math.Clamp((value.Value+1)/2,0,1);
        return new SolidColorBrush(Color.FromRgb((byte)(240*(1-t)),(byte)(65+150*t),45));
    }
    private void DrawMap()
    {
        map.Children.Clear();if(date.SelectedItem is not DateTimeOffset day)return;
        var all=Current();if(all.Length==0)return;
        int minR=all.Min(o=>o.Row),minC=all.Min(o=>o.Column),nR=all.Max(o=>o.Row)-minR+1,nC=all.Max(o=>o.Column)-minC+1;
        double size=Math.Min(Math.Max(1,map.ActualWidth-8)/nC,270d/nR);
        foreach(var o in all.Where(o=>o.Date==day))
        {
            var cell=new Rectangle{Width=size,Height=size,Fill=ColorFor(o.Value),Stroke=o.Pixel==selectedPixel?Brushes.Black:null,StrokeThickness=1,
                ToolTip=$"Pixel {o.Pixel} · "+(o.Value is double value?$"NDVI {value:F4}":"Sem dado válido")};
            Canvas.SetLeft(cell,4+(o.Column-minC)*size);Canvas.SetTop(cell,4+(o.Row-minR)*size);
            cell.MouseLeftButtonDown+=(_,_)=>{selectedPixel=o.Pixel;DrawMap();DrawCurve();};map.Children.Add(cell);
        }
    }
    private void DrawCurve()
    {
        curve.Children.Clear();var all=Current();if(all.Length==0)return;
        curveTitle.Text=selectedPixel==null?"Mediana do NDVI do polígono":"NDVI do pixel "+selectedPixel;
        var points=all.Where(o=>selectedPixel==null||o.Pixel==selectedPixel).GroupBy(o=>o.Date).OrderBy(g=>g.Key).Select(g=>{
            var values=g.Where(o=>o.Value.HasValue).Select(o=>o.Value!.Value).Order().ToArray();
            double? median=values.Length==0?null:values.Length%2==1?values[values.Length/2]:(values[values.Length/2-1]+values[values.Length/2])/2;
            return (Date:g.Key,Value:median);
        }).ToArray();if(points.Length==0)return;
        double w=Math.Max(100,curve.ActualWidth)-55,h=145,days=Math.Max(1,(points[^1].Date-points[0].Date).TotalDays);
        void Label(string text,double x,double y){var label=new TextBlock{Text=text,FontSize=10,Foreground=Brushes.SlateGray};Canvas.SetLeft(label,x);Canvas.SetTop(label,y);curve.Children.Add(label);}
        foreach(double tick in new[]{-1d,0d,1d}){double y=10+(1-tick)/2*h;curve.Children.Add(new Line{X1=32,Y1=y,X2=32+w,Y2=y,Stroke=Brushes.LightGray});Label(tick.ToString("0"),6,y-6);}
        Point? previous=null;
        foreach(var item in points){if(item.Value is not double value){previous=null;continue;}var p=new Point(32+(item.Date-points[0].Date).TotalDays/days*w,10+(1-value)/2*h);
            if(previous is Point last)curve.Children.Add(new Line{X1=last.X,Y1=last.Y,X2=p.X,Y2=p.Y,Stroke=Brushes.ForestGreen,StrokeThickness=2});
            var marker=new Ellipse{Width=6,Height=6,Fill=Brushes.ForestGreen,ToolTip=$"{item.Date:dd/MM/yyyy HH:mm} UTC · {value:F4}"};Canvas.SetLeft(marker,p.X-3);Canvas.SetTop(marker,p.Y-3);curve.Children.Add(marker);previous=p;}
        Label(points[0].Date.ToString("dd/MM/yyyy"),32,165);Label(points[^1].Date.ToString("dd/MM/yyyy"),Math.Max(32,w-35),180);
    }
}
