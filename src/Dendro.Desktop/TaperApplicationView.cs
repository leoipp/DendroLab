using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class TaperApplicationView : DockPanel
{
    private sealed record FitChoice(JsonObject Fit,TaperModel Model,double[] Coefficients)
    {
        public string Label=>$"{Fit.S("name")} · {Fit.S("group")} · {string.Join("; ",Coefficients.Select((v,i)=>$"b{i}={v:G4}"))}";
    }
    private sealed record Section(int Number,double Start,double End,double DStart,double DEnd,double Volume,bool Usable,bool Adjusted=false);
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox fit=Ui.Combo([]);
    private readonly TextBox dap=Ui.Input("30"),height=Ui.Input("20"),stump=Ui.Input("0,3"),sectionLength=Ui.Input("2,2"),minimumLength=Ui.Input("1,0"),minimumDiameter=Ui.Input("0");
    private readonly CheckBox discardShort=new(){Content="Descartar a seção final se for menor que o comprimento mínimo",IsChecked=true};
    private readonly TextBlock summary=Ui.Text("Informe as dimensões da árvore e selecione um ajuste salvo de taper.",13);
    private FitChoice[] choices=[];

    internal TaperApplicationView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner;this.session=session;Background=Brushes.White;
        var header=owner.ToolHeader("Aplicar taper");header.Margin=new Thickness(10,8,10,5);DockPanel.SetDock(header,Dock.Top);Children.Add(header);
        var form=Ui.Form();Ui.Section(form,"Árvore teórica e ajuste");
        Ui.Field(form,"Ajuste salvo",fit);Ui.AddHelp(form,"Selecione um ajuste de taper com status OK. Os coeficientes salvos serão aplicados à árvore teórica informada abaixo.");
        var dimensions=new Grid();dimensions.ColumnDefinitions.Add(new ColumnDefinition());dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        dimensions.RowDefinitions.Add(new RowDefinition());dimensions.RowDefinitions.Add(new RowDefinition());dimensions.RowDefinitions.Add(new RowDefinition());
        AddField(dimensions,0,0,"DAP (cm)",dap);AddField(dimensions,1,0,"Altura total (m)",height);AddField(dimensions,0,1,"Altura do toco (m)",stump);AddField(dimensions,1,1,"Comprimento da seção (m)",sectionLength);AddField(dimensions,0,2,"Comprimento mínimo da tora (m)",minimumLength);AddField(dimensions,1,2,"Diâmetro mínimo no topo da seção (cm)",minimumDiameter);form.Children.Add(dimensions);
        form.Children.Add(discardShort);
        Ui.AddHelp(form,"O comprimento da seção é o máximo. Se o diâmetro no topo ficar abaixo do mínimo, a tora é encurtada em passos de 0,1 m até o maior comprimento que atende ao diâmetro, respeitando o comprimento mínimo. O trecho residual fica como não aproveitável.");
        summary.Margin=new Thickness(0,3,0,8);form.Children.Add(summary);
        var calculate=Ui.AsyncButton("Calcular volume",()=>owner.Execute(()=>{Calculate();return Task.CompletedTask;}),Ui.ActionEmphasis.Primary);form.Children.Add(calculate);
        DockPanel.SetDock(form,Dock.Top);Children.Add(Ui.Scroll(form));
        Loaded+=(_,_)=>LoadFits();
    }

    private static void AddField(Grid grid,int column,int row,string label,Control control)
    {
        var panel=new StackPanel{Margin=new Thickness(0,0,12,0)};Ui.Field(panel,label,control);Grid.SetColumn(panel,column);Grid.SetRow(panel,row);grid.Children.Add(panel);
    }
    private void LoadFits()
    {
        var definitions=TaperAdjustment.ReadModels(session.Data!);
        choices=session.Meta.Arr("taper_fits").OfType<JsonObject>()
            .Where(f=>f.S("status")=="OK"&&f["coefficients"] is JsonArray)
            .Select(f=>
            {
                var coefficients=f.Arr("coefficients").Select(v=>v?.GetValue<double>()??double.NaN).ToArray();
                var registered=definitions.FirstOrDefault(m=>m.Id==f.S("model"));
                string formula=f.S("formula",registered?.Formula??"");
                var parameters=registered?.Parameters??Regex.Matches(formula,@"\bb\d+\b").Select(m=>m.Value).Distinct().OrderBy(value=>int.Parse(value[1..],CultureInfo.InvariantCulture)).ToArray();
                var model=registered==null
                    ? new TaperModel(f.S("model"),f.S("name"),formula,parameters,Enumerable.Repeat(1d,parameters.Length).ToArray())
                    : registered with { Name=f.S("name",registered.Name), Formula=formula };
                return new FitChoice(f,model,coefficients);
            })
            .Where(item=>item.Model.Parameters.Length==item.Coefficients.Length&&item.Coefficients.All(double.IsFinite))
            .ToArray();
        fit.ItemsSource=choices.Select(item=>item.Label).ToArray();fit.SelectedIndex=choices.Length>0?0:-1;
        if(choices.Length==0)summary.Text="Nenhum ajuste de taper válido foi salvo. Use Ajuste de taper e salve um ajuste com status OK antes de aplicar.";
    }
    private static double Number(TextBox input,string label)
    {
        string text=input.Text.Trim().Replace(',','.');
        if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value))throw new InvalidDataException($"Informe um valor numérico válido para {label}.");
        return value;
    }
    private void Calculate()
    {
        if(fit.SelectedIndex<0)throw new InvalidOperationException("Selecione um ajuste salvo de taper.");
        double dbh=Number(dap,"DAP"),total=Number(height,"altura total"),stumpHeight=Number(stump,"altura do toco"),step=Number(sectionLength,"comprimento da seção"),min=Number(minimumLength,"comprimento mínimo"),minDiameter=Number(minimumDiameter,"diâmetro mínimo");
        if(dbh<=0||total<=1.3||stumpHeight<0||stumpHeight>=total||step<=0||min<=0||minDiameter<0)throw new InvalidDataException("Use DAP e comprimentos positivos, diâmetro mínimo igual ou maior que zero, altura total acima de 1,3 m e altura do toco menor que a altura total.");
        if(!OneDecimal(total)||!OneDecimal(stumpHeight)||!OneDecimal(step)||!OneDecimal(min))throw new InvalidDataException("Altura total, altura do toco e comprimentos devem usar no máximo uma casa decimal (incrementos de 0,1 m).");
        var selected=choices[fit.SelectedIndex];var rows=new List<Section>();double start=stumpHeight;int number=1;
        while(start<total-1e-9)
        {
            double nominalEnd=Math.Round(Math.Min(total,start+step),1),end=nominalEnd;
            double d1=Diameter(start),nominalD2=nominalEnd>=total-1e-9?0:Diameter(nominalEnd);
            bool adjusted=false;
            if(minDiameter>0&&d1>=minDiameter-1e-9&&nominalD2<minDiameter-1e-9)
            {
                double shortest=discardShort.IsChecked==true?min:.1;
                for(double candidate=Math.Round(nominalEnd-.1,1);candidate>=start+shortest-1e-9;candidate=Math.Round(candidate-.1,1))
                {
                    if(Diameter(candidate)>=minDiameter-1e-9)
                    {
                        end=candidate;adjusted=end<nominalEnd-1e-9;break;
                    }
                }
            }
            double length=Math.Round(end-start,1);
            double d2=end>=total-1e-9?0:Diameter(end);
            bool isUsable=d2>=minDiameter-1e-9&&!(discardShort.IsChecked==true&&length<min-1e-9);
            double volume=0;
            for(double subStart=start;subStart<end-1e-9;)
            {
                double subEnd=Math.Min(end,subStart+.1);
                double subD1=Diameter(subStart),subD2=subEnd>=total-1e-9?0:Diameter(subEnd);
                volume+=Math.PI/40000*((subD1*subD1+subD2*subD2)/2)*(subEnd-subStart);
                subStart=subEnd;
            }
            if(!double.IsFinite(volume)||volume<0)throw new InvalidDataException($"O ajuste salvo gerou diâmetro inválido na seção {number}, entre {start:G3} e {end:G3} m.");
            rows.Add(new(number++,start,end,d1,d2,volume,isUsable,adjusted));start=end;
        }
        double Diameter(double at)
        {
            if(at>=total-1e-9)return 0;
            double value=TaperAdjustment.Predict(selected.Model,selected.Coefficients,dbh,total,at);
            if(!double.IsFinite(value)||value<0)throw new InvalidDataException($"O ajuste salvo não pode estimar o diâmetro a {at:G3} m. Confira a equação e seus coeficientes.");
            return value;
        }
        static bool OneDecimal(double value)=>Math.Abs(value*10-Math.Round(value*10))<1e-8;
        var sectionArray=rows.ToArray();
        double totalVol=sectionArray.Sum(s=>s.Volume),usableVol=sectionArray.Where(s=>s.Usable).Sum(s=>s.Volume);
        int accepted=sectionArray.Count(s=>s.Usable),discarded=sectionArray.Length-accepted,adjustedCount=sectionArray.Count(s=>s.Adjusted);
        summary.Text=$"Volume total: {totalVol:N4} m³  ·  Volume aproveitável: {usableVol:N4} m³  ·  Resultado aberto em uma aba central.";
        string resultId="taper-application:"+Guid.NewGuid().ToString("N");
        owner.Document(resultId,$"Aplicar taper · {selected.Fit.S("name")}",BuildResultView(dbh,total,stumpHeight,sectionArray,totalVol,usableVol,accepted,discarded,adjustedCount));
    }

    private FrameworkElement BuildResultView(double dbh,double total,double stumpHeight,Section[] sections,double totalVol,double usableVol,int accepted,int discarded,int adjustedCount)
    {
        var root=new DockPanel{Background=Brushes.White};
        var heading=new StackPanel{Margin=new Thickness(16,12,16,10)};
        heading.Children.Add(Ui.Text("Resultado de aplicar taper",22));
        heading.Children.Add(Ui.Text($"Volume total acima do toco: {totalVol:N4} m³    ·    Volume aproveitável: {usableVol:N4} m³",13));
        heading.Children.Add(Ui.Text($"Seções aproveitadas: {accepted}    ·    Ajustadas pelo diâmetro mínimo: {adjustedCount}    ·    Descartadas: {discarded}",12));
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var content=new Grid{Margin=new Thickness(12,0,12,12)};
        content.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1.45,GridUnitType.Star),MinHeight=300});
        content.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star),MinHeight=185});
        var drawing=new TreeDiagram{MinHeight=300,Margin=new Thickness(0,4,0,10)};drawing.SetTree(dbh,total,stumpHeight,sections);content.Children.Add(drawing);
        var detail=new DataGrid{AutoGenerateColumns=true,IsReadOnly=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single,MinHeight=185,Foreground=Brushes.Black,ColumnWidth=new DataGridLength(1,DataGridLengthUnitType.Star),HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var data=new DataTable();foreach(string col in new[]{"Seção","h inicial (m)","h final (m)","Comprimento (m)","d inicial (cm)","d final (cm)","Volume (m³)","Aproveitável","Ajustada por d mín."})data.Columns.Add(col);
        var culture=CultureInfo.GetCultureInfo("pt-BR");
        foreach(var s in sections)data.Rows.Add(s.Number,s.Start.ToString("0.0",culture),s.End.ToString("0.0",culture),(s.End-s.Start).ToString("0.0",culture),s.DStart.ToString("0.00",culture),s.DEnd.ToString("0.00",culture),s.Volume.ToString("0.0000",culture),s.Usable?"Sim":"Não",s.Adjusted?"Sim":"—");detail.ItemsSource=data.DefaultView;
        detail.SelectionChanged+=(_,_)=>{if(detail.SelectedIndex>=0&&detail.SelectedIndex<sections.Length)drawing.SelectedSection=detail.SelectedIndex;};
        Grid.SetRow(detail,1);content.Children.Add(detail);root.Children.Add(content);return root;
    }

    private sealed class TreeDiagram : FrameworkElement
    {
        private double dap,height,stump;private Section[] sections=[];private int selected=-1;
        private static readonly Brush Line=new SolidColorBrush(Color.FromRgb(65,85,91));
        private static readonly BitmapSource Canopy=LoadCanopy();
        internal int SelectedSection{get=>selected;set{selected=value;InvalidateVisual();}}
        internal void SetTree(double dap,double height,double stump,Section[] sections){this.dap=dap;this.height=height;this.stump=stump;this.sections=sections;selected=-1;InvalidateVisual();}
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);double w=ActualWidth,h=ActualHeight;if(w<30||h<50)return;
            var bounds=new Rect(0,0,w,h);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(250,252,250),Color.FromRgb(239,244,239),90),new Pen(new SolidColorBrush(Color.FromRgb(219,227,234)),1),bounds);
            if(height<=0){DrawCentered(dc,"A árvore e suas seções aparecerão aqui após o cálculo.",w/2,h/2);return;}
            double top=28,bottom=h-43,stemH=bottom-top,center=w*.52,baseWidth=Math.Clamp(stemH*.04*dap/30,8,w*.08);
            double Y(double level)=>bottom-level/height*stemH;
            double Width(double diameter)=>Math.Max(1,baseWidth*diameter/Math.Max(dap,1));
            StreamGeometry ProfileGeometry(Section[] ordered)
            {
                var geometry=new StreamGeometry();using(var c=geometry.Open())
                {
                    var first=ordered[0];c.BeginFigure(new Point(center-Width(first.DStart)/2,Y(first.Start)),true,true);
                    foreach(var section in ordered)c.LineTo(new Point(center-Width(section.DEnd)/2,Y(section.End)),true,false);
                    var last=ordered[^1];c.LineTo(new Point(center+Width(last.DEnd)/2,Y(last.End)),true,false);
                    for(int i=ordered.Length-1;i>=0;i--)c.LineTo(new Point(center+Width(ordered[i].DStart)/2,Y(ordered[i].Start)),true,false);
                }
                geometry.Freeze();return geometry;
            }
            StreamGeometry SectionGeometry(Section s)
            {
                double r1=Width(s.DStart)/2,r2=Width(s.DEnd)/2,y1=Y(s.Start),y2=Y(s.End);
                var geometry=new StreamGeometry();using(var c=geometry.Open())
                {
                    c.BeginFigure(new Point(center-r1,y1),true,true);c.LineTo(new Point(center+r1,y1),true,false);
                    c.LineTo(new Point(center+r2,y2),true,false);c.LineTo(new Point(center-r2,y2),true,false);
                }
                geometry.Freeze();return geometry;
            }
            // A quiet height grid sits behind the illustration, keeping measurements readable.
            var gridPen=new Pen(new SolidColorBrush(Color.FromRgb(203,214,206)),.8){DashStyle=DashStyles.Dash};
            for(int i=0;i<=5;i++)
            {
                double level=height*i/5,y=Y(level);
                dc.DrawLine(gridPen,new Point(58,y),new Point(w-18,y));
                DrawAt(dc,$"{level:0.0} m",8,y,10,Color.FromRgb(87,105,93));
            }

            // Reference-like, fine eucalyptus foliage. Its partial alpha keeps the
            // measured, sectioned bole visible below the photographic leaf detail.
            double crownHeight=stemH*.54,crownWidth=Math.Min(crownHeight*.516,w*.68),crownTop=top;
            dc.PushOpacity(.62);
            dc.DrawImage(Canopy,new Rect(center-crownWidth/2,crownTop,crownWidth,crownHeight));
            dc.Pop();

            // Draw the bole once as a continuous tapered profile. Section boundaries
            // are then shown as fine marks inside the wood, without log-like overhangs.
            var ordered=sections.OrderBy(s=>s.Start).ToArray();
            if(ordered.Length>0)
            {
                var bole=ProfileGeometry(ordered);
                var bark=new LinearGradientBrush(Color.FromRgb(226,218,196),Color.FromRgb(132,128,112),new Point(0,0),new Point(1,0));
                dc.DrawGeometry(bark,new Pen(new SolidColorBrush(Color.FromRgb(111,106,92)),.8),bole);

                for(int i=0;i<ordered.Length;i++)
                {
                    var s=ordered[i];var section=SectionGeometry(s);double y=Y(s.End);
                    if(!s.Usable)
                    {
                        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(95,111,124,117)),null,section);
                    }
                    if(i==selected)
                    {
                        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(105,255,174,37)),new Pen(new SolidColorBrush(Color.FromArgb(90,222,111,20)),5),section);
                        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(165,255,190,61)),new Pen(new SolidColorBrush(Color.FromRgb(196,91,12)),2.5),section);
                    }
                    if(i<ordered.Length-1)
                    {
                        double radius=Width(s.DEnd)/2;
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(190,88,82,68)),.85),new Point(center-radius,y),new Point(center+radius,y));
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(170,255,255,244)),.7),new Point(center-radius,y-1),new Point(center+radius,y-1));
                    }
                }
            }
            // A clean, gently flared butt with a flat cut at the entered stump height.
            double stumpY=Y(stump),groundDiameter=Math.Max(dap,ordered.FirstOrDefault()?.DStart??dap)*1.10;
            if(stump>0)
            {
                double rTop=Width(ordered.FirstOrDefault()?.DStart??dap)/2,rBase=Width(groundDiameter)/2;
                var butt=new StreamGeometry();
                using(var c=butt.Open())
                {
                    c.BeginFigure(new Point(center-rTop,stumpY),true,true);
                    c.BezierTo(new Point(center-rTop*1.04,stumpY+(bottom-stumpY)*.35),new Point(center-rBase*.92,bottom-4),new Point(center-rBase*.72,bottom),true,false);
                    c.LineTo(new Point(center+rBase*.72,bottom),true,false);
                    c.BezierTo(new Point(center+rBase*.92,bottom-4),new Point(center+rTop*1.04,stumpY+(bottom-stumpY)*.35),new Point(center+rTop,stumpY),true,false);
                }
                butt.Freeze();
                dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(226,218,196),Color.FromRgb(132,128,112),new Point(0,0),new Point(1,0)),new Pen(new SolidColorBrush(Color.FromRgb(111,106,92)),.8),butt);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(91,83,67)),.8),new Point(center-rTop,stumpY),new Point(center+rTop,stumpY));
            }
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(92,113,91)),1.2),new Point(58,bottom),new Point(w-18,bottom));
            var info=new Rect(66,10,180,27);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235,255,255,255)),new Pen(new SolidColorBrush(Color.FromRgb(215,224,215)),.8),info,6,6);
            DrawAt(dc,$"DAP {dap:0.0} cm  ·  Toco {stump:0.0} m",info.X+9,info.Y+6,10,Color.FromRgb(71,80,69));
            if(selected>=0&&selected<ordered.Length)
            {
                var s=ordered[selected];
                var badge=new Rect(w-190,10,174,27);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(238,255,255,255)),new Pen(new SolidColorBrush(Color.FromRgb(215,224,215)),.8),badge,7,7);
                DrawAt(dc,$"Seção {s.Number}  ·  {s.Volume:N4} m³",badge.X+10,badge.Y+6,11,Color.FromRgb(51,67,53));
            }
        }
        private static void DrawAt(DrawingContext dc,string text,double x,double y,double size,Color color)
        {
            var ft=new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,new SolidColorBrush(color),VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
            dc.DrawText(ft,new Point(x,y-ft.Height/2));
        }
        private static BitmapSource LoadCanopy()
        {
            var bitmap=new BitmapImage(new Uri("pack://application:,,,/DendroLab;component/Assets/tree_canopy_taper.png"));
            bitmap.Freeze();return bitmap;
        }
        private static void DrawCentered(DrawingContext dc,string text,double x,double y)
        {
            var ft=new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,Brushes.Black,VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
            dc.DrawText(ft,new Point(x-ft.Width/2,y-ft.Height/2));
        }
    }
}
