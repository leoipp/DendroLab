using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Dendro.Desktop;

internal sealed class Sentinel2NdviView : ToolFormView
{
    private const double EarthRadius = 6378137d;
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly AppDatePicker fromDate = new() { SelectedDate = DateTime.Today.AddDays(-30) };
    private readonly AppDatePicker toDate = new() { SelectedDate = DateTime.Today.AddDays(-1) };
    private readonly ComboBox product = new();
    private readonly ComboBox maxCloud = Ui.Combo(["10", "20", "30", "50", "80"], "30");
    private readonly ComboBox areaLayer = new();
    private readonly TextBox minLongitude = Ui.Input("-50");
    private readonly TextBox minLatitude = Ui.Input("-25");
    private readonly TextBox maxLongitude = Ui.Input("-49");
    private readonly TextBox maxLatitude = Ui.Input("-24");
    private readonly TextBox resolution = Ui.Input("10");
    private readonly ListBox scenes = new() { MinHeight = 145, MaxHeight = 230, SelectionMode = SelectionMode.Single };
    private readonly TextBlock status = Ui.Text("Busque cenas na área e no intervalo escolhidos.", 11);

    internal Sentinel2NdviView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        DockPanel header = owner.ToolHeader("Imagens Sentinel‑2");
        header.Margin = new Thickness(24, 10, 12, 2);
        DockPanel.SetDock(header, Dock.Top);


        var form = Ui.Form();
        form.Children.Add(header);
        form.Margin = new Thickness(28, 12, 16, 20);
        Ui.AddHelp(form, "Pesquise cenas Sentinel-2 L2A no catálogo Copernicus e escolha uma imagem RGB ou um índice espectral. Todas as saídas mascaram nuvens, sombras e neve. A busca do catálogo é separada da geração da imagem, que consome processamento da sua conta Sentinel Hub.");
        Ui.Field(form, "Data inicial", fromDate);
        Ui.Field(form, "Data final", toDate);
        product.Items.Add(new ComboBoxItem { Content = "RGB natural", Tag = Sentinel2ImageProduct.Rgb });
        product.Items.Add(new ComboBoxItem { Content = "Falso colorido de vegetação (NIR)", Tag = Sentinel2ImageProduct.FalseColorVegetation });
        product.Items.Add(new ComboBoxItem { Content = "NDVI — vigor da vegetação", Tag = Sentinel2ImageProduct.Ndvi });
        product.Items.Add(new ComboBoxItem { Content = "EVI — vegetação densa", Tag = Sentinel2ImageProduct.Evi });
        product.Items.Add(new ComboBoxItem { Content = "NDMI — umidade da vegetação", Tag = Sentinel2ImageProduct.Ndmi });
        product.Items.Add(new ComboBoxItem { Content = "NDWI — água superficial", Tag = Sentinel2ImageProduct.Ndwi });
        product.Items.Add(new ComboBoxItem { Content = "SAVI — vegetação com influência do solo", Tag = Sentinel2ImageProduct.Savi });
        product.SelectedIndex = 2;
        Ui.Field(form, "Produto", product);
        Ui.Field(form, "Cobertura máxima de nuvens na cena", maxCloud);

        var vectorPaths = session.Files.Where(path => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)).ToArray();
        areaLayer.Items.Add(new ComboBoxItem { Content = "Limites informados abaixo", Tag = "" });
        foreach (string path in vectorPaths)
            areaLayer.Items.Add(new ComboBoxItem { Content = session.ContentName(path), Tag = path });
        areaLayer.SelectedIndex = 0;
        var areaPanel = new DockPanel { LastChildFill = true };
        var loadExtent = Ui.Button("Carregar limite do vetor", LoadSelectedExtent);
        loadExtent.Margin = new Thickness(0, 2, 0, 2);
        DockPanel.SetDock(loadExtent, Dock.Right);
        areaPanel.Children.Add(loadExtent);
        areaPanel.Children.Add(areaLayer);
        Ui.Field(form, "Área de interesse (opcional)", areaPanel);

        var bounds = new UniformGrid { Columns = 2, Margin = new Thickness(0, 1, 0, 8) };
        AddBound(bounds, "Longitude mín.", minLongitude);
        AddBound(bounds, "Longitude máx.", maxLongitude);
        AddBound(bounds, "Latitude mín.", minLatitude);
        AddBound(bounds, "Latitude máx.", maxLatitude);
        form.Children.Add(bounds);
        Ui.AddHelp(form, "Coordenadas WGS84. Selecione um vetor do projeto e carregue sua caixa envolvente, ou informe os quatro limites manualmente.");

        var search = Ui.AsyncButton("Buscar cenas Sentinel‑2", SearchScenes);
        search.HorizontalAlignment = HorizontalAlignment.Left;
        form.Children.Add(search);
        scenes.DisplayMemberPath = nameof(Sentinel2Scene.Label);
        scenes.Margin = new Thickness(0, 5, 0, 3);
        form.Children.Add(scenes);
        Ui.Field(form, "Resolução solicitada (m/pixel)", resolution);
        var download = Ui.AsyncButton("Gerar imagem e abrir no GIS", DownloadImage);
        download.HorizontalAlignment = HorizontalAlignment.Left;
        download.Margin = new Thickness(0, 8, 0, 4);
        SetPrimaryAction(download);
        status.Margin = new Thickness(0, 4, 0, 8);
        form.Children.Add(status);
        if (!session.IsOpen)
        {
            search.IsEnabled = false;
            download.IsEnabled = false;
            Ui.AddHelp(form, "Abra ou crie um projeto para salvar as imagens no GIS.");
        }
        SetForm(form);
    }

    private static void AddBound(Panel panel, string label, TextBox field)
    {
        field.Margin = new Thickness(2, 0, 6, 0);
        Ui.Field(panel, label, field);
    }

    private void LoadSelectedExtent()
    {
        if (areaLayer.SelectedItem is not ComboBoxItem { Tag: string path } || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Selecione um shapefile do projeto.");
        var layer = ShapefileReader.Read(session.Resolve(path), session.ContentName(path));
        minLongitude.Text = Format(ToLongitude(layer.Extent.Left));
        // Rect.Top is the smaller projected Y here because the map extent uses
        // WPF's downward-positive screen coordinates; convert it to the southern latitude.
        minLatitude.Text = Format(Math.Min(ToLatitude(layer.Extent.Top), ToLatitude(layer.Extent.Bottom)));
        maxLongitude.Text = Format(ToLongitude(layer.Extent.Right));
        maxLatitude.Text = Format(Math.Max(ToLatitude(layer.Extent.Top), ToLatitude(layer.Extent.Bottom)));
    }

    private async Task SearchScenes()
    {
        var extent = ReadExtent();
        DateTime from = fromDate.SelectedDate?.Date ?? throw new InvalidOperationException("Informe a data inicial.");
        DateTime to = toDate.SelectedDate?.Date ?? throw new InvalidOperationException("Informe a data final.");
        double cloud = ReadCloud();
        status.Text = "Consultando o catálogo Copernicus…";
        scenes.ItemsSource = null;
        using var client = new CopernicusDataSpaceClient();
        var found = await client.SearchSentinel2Async(extent.XMin, extent.YMin, extent.XMax, extent.YMax, from, to, cloud, owner.OperationCancellation);
        scenes.ItemsSource = found;
        if (found.Length > 0)
        {
            scenes.SelectedIndex = 0;
            status.Text = $"{found.Length} cena(s) encontrada(s). A geração usa a menor cobertura de nuvens dentro do período.";
        }
        else status.Text = "Nenhuma cena encontrada. Amplie o intervalo ou permita mais nuvens.";
    }

    private async Task DownloadImage()
    {
        if (!session.IsOpen) throw new InvalidOperationException("Abra ou crie um projeto antes de gerar o NDVI.");
        if (scenes.SelectedItem is not Sentinel2Scene scene)
            throw new InvalidOperationException("Busque e selecione uma cena Sentinel-2 antes de gerar o NDVI.");
        if (product.SelectedItem is not ComboBoxItem { Tag: Sentinel2ImageProduct selectedProduct })
            throw new InvalidOperationException("Selecione o produto Sentinel-2 que deseja gerar.");
        var extent = ReadExtent();
        if (!double.TryParse(resolution.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double resolutionMeters)
            || !double.IsFinite(resolutionMeters) || resolutionMeters < 10 || resolutionMeters > 1000)
            throw new InvalidOperationException("Informe uma resolução entre 10 e 1000 metros por pixel.");
        double centerLatitude = (extent.YMin + extent.YMax) / 2;
        double widthMeters = (extent.XMax - extent.XMin) * 111320 * Math.Max(0.01, Math.Cos(centerLatitude * Math.PI / 180));
        double heightMeters = (extent.YMax - extent.YMin) * 111320;
        int width = Math.Max(1, (int)Math.Ceiling(widthMeters / resolutionMeters));
        int height = Math.Max(1, (int)Math.Ceiling(heightMeters / resolutionMeters));
        if (width > 2500 || height > 2500)
            throw new InvalidOperationException($"A área exige {width:N0} × {height:N0} pixels nesta resolução. O Process API limita cada dimensão a 2.500 pixels; reduza a resolução solicitada ou use um shapefile menor.");
        double cloud = ReadCloud();
        var credentials = WindowsCredentialStore.ReadCopernicusCredentials()
            ?? throw new InvalidOperationException("Configure o Client ID e o Client Secret em Configurações → Imagens de satélite.");
        using var client = new CopernicusDataSpaceClient(credentials);
        DateTime day = scene.Acquired.UtcDateTime.Date;
        string productName = ProductFileName(selectedProduct);
        status.Text = $"Gerando {productName} para {scene.Acquired:dd/MM/yyyy} UTC…";
        owner.Log($"Solicitando {productName} Sentinel-2 ao Copernicus Data Space…");
        byte[] image = await client.DownloadImagePngAsync(extent.XMin, extent.YMin, extent.XMax, extent.YMax,
            day, day, cloud, width, height, selectedProduct, owner.OperationCancellation);
        string path = OutputPath(scene.Acquired, productName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] geotiff = SatelliteGeoTiffWriter.FromPng(image, extent.XMin, extent.YMin, extent.XMax, extent.YMax);
        await File.WriteAllBytesAsync(path, geotiff, owner.OperationCancellation);
        await owner.RegisterSentinel2NdviImage(path, productName);
        status.Text = $"{productName} salvo e aberto no GIS.";
    }

    private (double XMin, double YMin, double XMax, double YMax) ReadExtent()
    {
        double xmin = Parse(minLongitude), ymin = Parse(minLatitude), xmax = Parse(maxLongitude), ymax = Parse(maxLatitude);
        if (xmin < -180 || xmax > 180 || ymin < -90 || ymax > 90 || xmin >= xmax || ymin >= ymax)
            throw new InvalidOperationException("Informe limites WGS84 válidos, com os valores mínimos antes dos máximos.");
        return (xmin, ymin, xmax, ymax);
    }

    private double ReadCloud() => double.TryParse(maxCloud.SelectedItem?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value : 30;

    private string OutputPath(DateTimeOffset acquired, string productName)
    {
        string folder = session.FilePath.Length > 0
            ? Path.Combine(Path.GetDirectoryName(session.FilePath)!, Path.GetFileNameWithoutExtension(session.FilePath) + "_camadas")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DENDRO", "imagens-sentinel-2");
        string stem = $"{productName}_Sentinel2_{acquired:yyyyMMdd_HHmm}";
        string path = Path.Combine(folder, stem + ".tif");
        for (int suffix = 2; File.Exists(path); suffix++) path = Path.Combine(folder, $"{stem}_{suffix}.tif");
        return path;
    }

    private static string ProductFileName(Sentinel2ImageProduct product) => product switch
    {
        Sentinel2ImageProduct.Rgb => "RGB",
        Sentinel2ImageProduct.FalseColorVegetation => "FalsoColorido",
        Sentinel2ImageProduct.Ndvi => "NDVI",
        Sentinel2ImageProduct.Evi => "EVI",
        Sentinel2ImageProduct.Ndmi => "NDMI",
        Sentinel2ImageProduct.Ndwi => "NDWI",
        Sentinel2ImageProduct.Savi => "SAVI",
        _ => throw new ArgumentOutOfRangeException(nameof(product)),
    };

    private static double Parse(TextBox box)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)) return value;
        throw new InvalidOperationException($"Coordenada inválida: {box.Text}");
    }

    private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static double ToLongitude(double x) => x / EarthRadius * 180 / Math.PI;
    private static double ToLatitude(double y) => (2 * Math.Atan(Math.Exp(y / EarthRadius)) - Math.PI / 2) * 180 / Math.PI;
}
