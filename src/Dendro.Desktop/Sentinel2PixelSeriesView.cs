using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class Sentinel2PixelSeriesView : ToolFormView
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source = Ui.Combo([]);
    private readonly TextBox resolution = Ui.Input("10");
    private readonly TextBlock status = Ui.Text("Selecione um catálogo temporal salvo no projeto.");
    private readonly List<(string Path, TableSource Table)> catalogs = [];
    private bool initialized;
    internal Sentinel2PixelSeriesView(MainWindow owner, ProjectSession session)
    {
        this.owner=owner; this.session=session;
        var form=Ui.Form(); SetForm(form);
        form.Children.Add(owner.ToolHeader("Série de pixels Sentinel-2"));
        Ui.Field(form,"Catálogo temporal",source);
        Ui.Field(form,"Tamanho aproximado do pixel no terreno (m)",resolution);
        Ui.AddHelp(form,"Extrai NDVI numérico, com precisão de 0,0001, em grade fixa EPSG:3857. O tamanho no terreno é aproximado pela latitude central. Usa o centro do pixel para recortar cada polígono; polígonos sobrepostos mantêm séries separadas. Nuvens e sombras ficam com NDVI vazio e motivo registrado. A consulta consome processamento da conta Copernicus configurada.");
        Ui.AddHelp(form,"Consulta uma janela de ±30 minutos do horário do catálogo para acomodar diferenças de horário entre produto e tile. Data_UTC identifica a aquisição do catálogo. Uma observação por horário de aquisição: produtos de tiles sobrepostos no mesmo horário são reunidos. Respeita o período de cada polígono registrado no catálogo e identifica os polígonos pelo número do registro no vetor original.");
        form.Children.Add(status);
        SetPrimaryAction(Ui.AsyncButton("Extrair e salvar série de pixels",()=>owner.Execute(Run)));
        Loaded+=async(_,_)=>{if(!initialized){initialized=true;await owner.Execute(Initialize);}};
    }
    private async Task Initialize()
    {
        var paths=session.Files.Where(session.IsTabular).ToArray();
        var found=await Task.Run(()=>paths.SelectMany(p=>TableStore.Tables(session.Resolve(p))
            .Where(t=>t=="CATALOGO SENTINEL-2").Select(t=>(Path:p,Table:new TableSource(session.Resolve(p),t)))).ToArray());
        catalogs.AddRange(found);
        source.ItemsSource=catalogs.Select(c=>session.ContentName(c.Path)).ToArray();
        if(catalogs.Count>0)source.SelectedIndex=catalogs.Count-1;
    }
    private async Task Run()
    {
        if(source.SelectedIndex<0)throw new InvalidOperationException("Crie um catálogo temporal primeiro.");
        double meters=Ui.Number(resolution.Text);
        if(!double.IsFinite(meters)||meters<10||meters>1000)throw new InvalidOperationException("Informe um tamanho entre 10 e 1000 m.");
        var selected=catalogs[source.SelectedIndex];
        var catalog=await Task.Run(()=>TableStore.Read(selected.Table));
        if(catalog.Count<2)throw new InvalidOperationException("Catálogo vazio.");
        int vectorColumn=catalog.Require("Vetor_Origem"), dateColumn=catalog.Require("Data_UTC"), startColumn=catalog.Require("Inicio_Ciclo");
        string vector=catalog.Text(1,vectorColumn);
        if(Enumerable.Range(1,catalog.Count-1).Any(r=>catalog.Text(r,vectorColumn)!=vector))throw new InvalidOperationException("O catálogo deve usar um único vetor.");
        var layer=await Task.Run(()=>ShapefileReader.Read(session.Resolve(vector),session.ContentName(vector)));
        if(layer.Features.Any(f=>f.Type%10!=5))throw new InvalidOperationException("O vetor deve conter polígonos.");
        double centerY=(layer.Extent.Top+layer.Extent.Bottom)/2;
        double latitude=2*Math.Atan(Math.Exp(centerY/6378137))-Math.PI/2;
        double step=meters/Math.Cos(latitude);
        double xmin=Math.Floor(layer.Extent.Left/step)*step, ymax=Math.Ceiling(layer.Extent.Bottom/step)*step;
        int width=(int)Math.Ceiling((layer.Extent.Right-xmin)/step), height=(int)Math.Ceiling((ymax-layer.Extent.Top)/step);
        if(width<1||height<1||width>2500||height>2500)throw new InvalidOperationException("A grade deve ter até 2500 pixels por dimensão. Use um vetor menor ou pixels maiores.");
        var shapes=layer.Features.Select(f=>{
            var geometry=new StreamGeometry{FillRule=FillRule.EvenOdd};
            using(var ctx=geometry.Open())foreach(var ring in f.Parts){if(ring.Length<3)continue;ctx.BeginFigure(ring[0],true,true);ctx.PolyLineTo(ring.Skip(1).ToArray(),true,false);}
            geometry.Freeze();return geometry;
        }).ToArray();
        var credentials=WindowsCredentialStore.ReadCopernicusCredentials()??throw new InvalidOperationException("Configure as credenciais Copernicus em Configurações → Imagens de satélite.");
        string temporary=Path.Combine(session.ProjectWorkFolder(),"Serie_NDVI_"+Guid.NewGuid().ToString("N")+".dlb");
        owner.StartTableOperation("Extraindo série temporal de NDVI…");
        var cancellation=owner.OperationCancellation;
        var acquisitions=Enumerable.Range(1,catalog.Count-1).GroupBy(r=>DateTimeOffset.Parse(catalog.Text(r,dateColumn),CultureInfo.InvariantCulture).ToUniversalTime()).OrderBy(g=>g.Key).ToArray();
        long rows=0, presentRows=0;
        try
        {
            using(var writer=new CompactBankWriter(temporary))
            using(var client=new CopernicusDataSpaceClient(credentials))
            {
                string table=writer.AddTable(["Vetor_Origem","Poligono_ID","Pixel_ID","Linha_Grade","Coluna_Grade","Longitude","Latitude","Data_UTC","Idade_dias","NDVI","Valido","Qualidade","SCL","Cenas_Catalogo","Grade_CRS","Passo_Grade_m","Origem_X","Origem_Y"],"SERIE PIXELS NDVI");
                var pixels=await Task.Run(()=>{
                    var list=new List<(int Polygon,int Row,int Column,double X,double Y)>();
                    for(int r=0;r<height;r++){
                        cancellation.ThrowIfCancellationRequested();
                        for(int c=0;c<width;c++){
                            double x=xmin+(c+0.5)*step,y=ymax-(r+0.5)*step;
                            for(int p=0;p<shapes.Length;p++)if(shapes[p].FillContains(new Point(x,y)))list.Add((p+1,r,c,x,y));
                        }
                    }
                    return list;
                },cancellation);
                if(pixels.Count==0)throw new InvalidOperationException("Nenhum centro de pixel dentro dos polígonos.");
                int completed=0;
                foreach(var acquisition in acquisitions)
                {
                    cancellation.ThrowIfCancellationRequested();
                    status.Text=$"Extraindo {acquisition.Key:dd/MM/yyyy HH:mm} UTC · {completed+1}/{acquisitions.Length} aquisições…";
                    byte[] png=await client.DownloadNumericNdviAsync(xmin,ymax-height*step,xmin+width*step,ymax,width,height,acquisition.Key,cancellation);
                    using var stream=new MemoryStream(png,false);
                    var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                    var frame=decoder.Frames[0];
                    if(frame.PixelWidth!=width||frame.PixelHeight!=height)throw new InvalidDataException("A resposta mudou as dimensões da grade.");
                    // Do not convert to premultiplied alpha: RG encodes numeric data.
                    var bitmap=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
                    byte[] bytes=new byte[checked(width*height*4)];bitmap.CopyPixels(bytes,width*4,0);
                    DateTime start=DateTime.ParseExact(catalog.Text(acquisition.First(),startColumn),"yyyy-MM-dd",CultureInfo.InvariantCulture);
                    var polygonRows = acquisition.Where(r => catalog.Column("Poligono_ID") >= 0)
                        .GroupBy(r => checked((int)catalog.Number(r,catalog.Require("Poligono_ID")))).ToDictionary(g => g.Key, g => g.First());
                    string scenes=string.Join(";",acquisition.Select(r=>catalog.Text(r,catalog.Require("Cena_ID"))));
                    await Task.Run(()=>{
                        foreach(var pixel in pixels)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (catalog.Column("Poligono_ID") >= 0 && !polygonRows.ContainsKey(pixel.Polygon)) continue;
                            int catalogRow = polygonRows.GetValueOrDefault(pixel.Polygon, acquisition.First());
                            DateTime pixelStart = DateTime.ParseExact(catalog.Text(catalogRow,startColumn),"yyyy-MM-dd",CultureInfo.InvariantCulture);
                            int i=(pixel.Row*width+pixel.Column)*4;
                            int scl=bytes[i]; bool present=bytes[i+3]>0;
                            if(present)presentRows++;
                            bool valid=present && scl is not (0 or 1 or 3 or 7 or 8 or 9 or 10 or 11);
                            double ndvi=(bytes[i+2]*256+bytes[i+1])/10000d-1;
                            string quality=!present?"Sem dados":valid?"Valido":scl==3?"Sombra":scl is 8 or 9 or 10?"Nuvem":scl==11?"Neve":"SCL excluida";
                            writer.Add(table,[vector,(double)pixel.Polygon,$"{pixel.Row}:{pixel.Column}",(double)pixel.Row,(double)pixel.Column,
                                pixel.X/6378137*180/Math.PI,(2*Math.Atan(Math.Exp(pixel.Y/6378137))-Math.PI/2)*180/Math.PI,
                                acquisition.Key.ToString("O"), (acquisition.Key.UtcDateTime.Date-pixelStart).TotalDays,valid?(object)ndvi:null,valid?1d:0d,quality,(double)scl,scenes,"EPSG:3857",step,xmin,ymax]);
                            rows++;
                        }
                    },cancellation);
                    owner.SetProgress(++completed*95d/acquisitions.Length);
                }
                cancellation.ThrowIfCancellationRequested();
                if(presentRows==0)throw new InvalidDataException("O provedor retornou somente pixels sem dados em todas as aquisições. A série não foi salva. Confira a área e a disponibilidade das cenas no Process API.");
                writer.Complete();
            }
            await session.AddTableResults([new(temporary,"SERIE PIXELS NDVI",rows)],selected.Path,"CATALOGO SENTINEL-2");
            owner.SetProgress(100);status.Text=$"Série salva: {rows:N0} observações em {acquisitions.Length:N0} aquisições.";owner.Log(status.Text);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
