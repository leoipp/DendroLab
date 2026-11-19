using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Tabular;
using Dendro.Core.Sampling;

namespace Dendro.Desktop;

internal sealed class Sentinel2TemporalCatalogView : ToolFormView
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox plantingField = Ui.Combo([]), harvestField = Ui.Combo([]);
    private readonly CheckBox plantingFromAttribute = new() { Content = "Ler plantio dos atributos" }, harvestFromAttribute = new() { Content = "Ler colheita dos atributos" };
    private readonly ComboBox area = Ui.Combo([]);
    private readonly AppDatePicker planting = new() { SelectedDate = DateTime.Today.AddYears(-1) };
    private readonly AppDatePicker harvest = new() { SelectedDate = DateTime.Today };
    private readonly TextBox cloud = Ui.Input("100");
    private readonly TextBlock status = Ui.Text("Selecione o vetor e o período para consultar as aquisições.");
    private readonly DataGrid preview = new() { IsReadOnly = true, AutoGenerateColumns = true, MaxHeight = 260, MinHeight = 100 };
    private readonly string[] paths;

    internal Sentinel2TemporalCatalogView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner; this.session = session;
        paths = session.Files.Where(p => Path.GetExtension(p).Equals(".shp", StringComparison.OrdinalIgnoreCase)).ToArray();
        area.ItemsSource = paths.Select(p => session.ContentName(p)).ToArray();
        if (paths.Length > 0) area.SelectedIndex = 0;
        var form = Ui.Form(); SetForm(form);
        form.Children.Add(owner.ToolHeader("Catálogo temporal Sentinel-2"));
        Ui.Field(form, "Vetor da área de interesse", area);
        Ui.AddHelp(form, "Consulta as cenas L2A que intersectam a caixa envolvente do vetor. O catálogo registra candidatos para extração posterior; não verifica ainda a cobertura de pixels válidos dentro dos polígonos.");
        form.Children.Add(plantingFromAttribute);
        Ui.Field(form, "Coluna de plantio", plantingField);
        Ui.Field(form, "Início do ciclo / plantio", planting);
        form.Children.Add(harvestFromAttribute);
        Ui.Field(form, "Coluna de colheita", harvestField);
        Ui.Field(form, "Fim do ciclo / colheita ou data de análise", harvest);
        Ui.Field(form, "Cobertura máxima de nuvens na cena (%)", cloud);
        Ui.AddHelp(form, "100% preserva todas as cenas candidatas, inclusive aquelas com alguma área útil sob nuvens parciais. A máscara de nuvens por pixel será aplicada na etapa de extração. Cada polígono é consultado separadamente com seu próprio período. Plantio e colheita podem vir de colunas independentes ou de datas manuais. Datas ausentes ou inválidas precisam ser corrigidas antes da consulta.");
        form.Children.Add(preview); form.Children.Add(status);
        var run = Ui.AsyncButton("Consultar e salvar catálogo", () => owner.Execute(Run));
        run.IsEnabled = session.IsOpen && paths.Length > 0;
        SetPrimaryAction(run);
        void UpdateDates()
        {
            plantingField.IsEnabled = plantingFromAttribute.IsChecked == true;
            harvestField.IsEnabled = harvestFromAttribute.IsChecked == true;
            planting.IsEnabled = !plantingField.IsEnabled;
            harvest.IsEnabled = !harvestField.IsEnabled;
        }
        plantingFromAttribute.Click += (_,_) => UpdateDates(); harvestFromAttribute.Click += (_,_) => UpdateDates(); UpdateDates();
        area.SelectionChanged += async (_,_) => await owner.Execute(LoadFields);
        Loaded += async (_,_) => await owner.Execute(LoadFields);
        if (paths.Length == 0) Ui.AddHelp(form, "Adicione um shapefile de polígonos ao projeto para consultar sua área.");
    }

    private async Task LoadFields()
    {
        if (area.SelectedIndex < 0) return;
        string path = session.Resolve(paths[area.SelectedIndex]);
        var fields = await Task.Run(() => ForestSampler.ReadAttributeFields(path));
        plantingField.ItemsSource = fields; harvestField.ItemsSource = fields;
        if (fields.Length > 0) { plantingField.SelectedIndex = 0; harvestField.SelectedIndex = 0; }
    }
    private static DateTime AttributeDate(object? raw, int polygon, string field)
    {
        if (raw is DateTime date) return date.Date;
        string text = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (DateTime.TryParseExact(text, new[]{"yyyyMMdd","yyyy-MM-dd","dd/MM/yyyy","yyyy-MM-dd HH:mm:ss"}, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date.Date;
        throw new InvalidOperationException($"Polígono {polygon}: data inválida ou ausente em '{field}' ({text}).");
    }

    private async Task Run()
    {
        if (area.SelectedIndex < 0) throw new InvalidOperationException("Selecione o vetor.");
        var from = planting.SelectedDate?.Date ?? throw new InvalidOperationException("Informe o início do ciclo.");
        var to = harvest.SelectedDate?.Date ?? throw new InvalidOperationException("Informe o fim do período.");
        double maxCloud = Ui.Number(cloud.Text);
        if (!double.IsFinite(maxCloud) || maxCloud < 0 || maxCloud > 100) throw new InvalidOperationException("Informe nuvens entre 0 e 100%.");
        string source = paths[area.SelectedIndex];
        string sourceName = session.ContentName(source);
        string folder = session.ProjectWorkFolder();
        var layer = await Task.Run(() => ShapefileReader.Read(session.Resolve(source), sourceName));
        if (layer.Features.Length == 0 || layer.Features.Any(f => f.Type % 10 != 5))
            throw new InvalidOperationException("Selecione um shapefile contendo polígonos.");
        var attributes = await Task.Run(() => ForestSampler.ReadAttributeRecords(session.Resolve(source)));
        if (attributes.Length != layer.Features.Length) throw new InvalidOperationException("A quantidade de geometrias e registros de atributos difere; revise o shapefile.");
        bool usePlanting = plantingFromAttribute.IsChecked == true, useHarvest = harvestFromAttribute.IsChecked == true;
        string plantColumn = plantingField.SelectedItem as string ?? "", harvestColumn = harvestField.SelectedItem as string ?? "";
        var periods = layer.Features.Select((feature,index) => {
            var start = usePlanting ? AttributeDate(attributes[index].GetValueOrDefault(plantColumn),index+1,plantColumn) : from;
            var end = useHarvest ? AttributeDate(attributes[index].GetValueOrDefault(harvestColumn),index+1,harvestColumn) : to;
            if (start > end) throw new InvalidOperationException($"Polígono {index+1}: plantio posterior à colheita/fim do período.");
            var points=feature.Parts.SelectMany(p=>p).ToArray();
            double Lon(double x)=>x/6378137*180/Math.PI;
            double Lat(double y)=>(2*Math.Atan(Math.Exp(y/6378137))-Math.PI/2)*180/Math.PI;
            return (Id:index+1,Start:start,End:end,Xmin:Lon(points.Min(p=>p.X)),Ymin:Lat(points.Min(p=>p.Y)),Xmax:Lon(points.Max(p=>p.X)),Ymax:Lat(points.Max(p=>p.Y)));
        }).ToArray();
        owner.StartTableOperation("Consultando catálogo temporal por polígono…");
        var cancellation = owner.OperationCancellation;
        using var client = new CopernicusDataSpaceClient();
        var found = new List<(int Polygon, DateTime Start, DateTime End, double Xmin, double Ymin, double Xmax, double Ymax, Sentinel2Scene Scene)>();
        foreach (var period in periods)
        {
            status.Text=$"Consultando polígono {period.Id}/{periods.Length}: {period.Start:dd/MM/yyyy} a {period.End:dd/MM/yyyy}…";
            var scenes=await client.SearchTemporalCatalogAsync(period.Xmin,period.Ymin,period.Xmax,period.Ymax,period.Start,period.End,maxCloud,cancellation);
            found.AddRange(scenes.Select(scene=>(period.Id,period.Start,period.End,period.Xmin,period.Ymin,period.Xmax,period.Ymax,scene)));
        }
        preview.ItemsSource = found.Select(s => new { Poligono=s.Polygon, Cena = s.Scene.Id, Data_UTC = s.Scene.Acquired.UtcDateTime, Plantio=s.Start, Fim=s.End, Nuvens_pct = s.Scene.CloudCover }).ToArray();
        if (found.Count == 0) { status.Text = "Nenhuma cena encontrada; catálogo não gravado."; return; }
        string temporary = Path.Combine(folder, "Catalogo_Sentinel2_" + Guid.NewGuid().ToString("N") + ".dlb");
        try
        {
            await Task.Run(() =>
            {
                using var writer = new CompactBankWriter(temporary);
                string id = writer.AddTable(["Area_Origem", "Vetor_Origem", "Poligono_ID", "Cena_ID", "Data_UTC", "Idade_dias", "Nuvens_Cena_pct", "Satelite", "Inicio_Ciclo", "Fim_Periodo", "Longitude_Min", "Latitude_Min", "Longitude_Max", "Latitude_Max", "Colecao", "Consulta_UTC"], "CATALOGO SENTINEL-2");
                string queried = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                foreach (var item in found)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var scene=item.Scene;
                    writer.Add(id, [sourceName, source, (double)item.Polygon, scene.Id, scene.Acquired.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        (scene.Acquired.UtcDateTime.Date - item.Start).TotalDays, scene.CloudCover, scene.Platform,
                        item.Start.ToString("yyyy-MM-dd"), item.End.ToString("yyyy-MM-dd"), item.Xmin, item.Ymin, item.Xmax, item.Ymax, "sentinel-2-l2a", queried]);
                }
                writer.Complete();
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            await session.AddTableResult(new(temporary, "CATALOGO SENTINEL-2", found.Count), source);
            status.Text = $"Catálogo salvo no projeto: {found.Count:N0} cenas em {found.Select(s => s.Scene.Acquired.UtcDateTime.Date).Distinct().Count():N0} datas. As cenas ainda não foram baixadas.";
            owner.SetProgress(100); owner.Log(status.Text);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
