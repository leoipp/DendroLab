using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Dendro.Desktop;

internal sealed class NoaaNdviView : DockPanel
{
    private const double EarthRadius = 6378137d;
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox product = Ui.Combo(["NDVI", "RGB"], "NDVI");
    private readonly ComboBox cadence = Ui.Combo(["Semanal", "Mensal", "Anual"], "Mensal");
    private readonly AppDatePicker date = new() { SelectedDate = DateTime.Today.AddDays(-14) };
    private readonly ComboBox areaLayer = new();
    private readonly TextBox minLongitude = Ui.Input("-50");
    private readonly TextBox minLatitude = Ui.Input("-25");
    private readonly TextBox maxLongitude = Ui.Input("-49");
    private readonly TextBox maxLatitude = Ui.Input("-24");
    private readonly TextBox imageWidth = Ui.Input("1600");

    internal NoaaNdviView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        DockPanel header = owner.ToolHeader("Imagens NOAA");
        header.Margin = new Thickness(24, 10, 12, 2);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        var form = Ui.Form();
        form.Margin = new Thickness(28, 12, 16, 20);
        Ui.AddHelp(form, "Baixa imagens georreferenciadas do NOAA e abre a camada no GIS. RGB usa a imagem diária em cor natural; NDVI usa as composições semanais, mensais ou anuais. A resolução é regional, adequada para contexto amplo, não para separar árvores ou talhões pequenos.");
        Ui.Field(form, "Produto", product);
        Ui.Field(form, "Periodicidade do NDVI", cadence);
        product.SelectionChanged += (_, _) => cadence.IsEnabled = product.SelectedItem?.ToString() != "RGB";
        Ui.Field(form, "Data de referência", date);

        var vectorPaths = session.Files.Where(path => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)).ToArray();
        areaLayer.Items.Add(new ComboBoxItem { Content = "Limites informados abaixo", Tag = "" });
        foreach (string path in vectorPaths)
            areaLayer.Items.Add(new ComboBoxItem { Content = session.ContentName(path), Tag = path });
        areaLayer.SelectedIndex = 0;
        var areaPanel = new DockPanel();
        areaPanel.LastChildFill = true;
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
        Ui.AddHelp(form, "Coordenadas WGS84 em graus. Se selecionar um shapefile, Carregar limite do vetor preenche a caixa envolvente; os quatro valores também podem ser editados manualmente.");

        Ui.Field(form, "Largura máxima da imagem (pixels)", imageWidth);
        var download = Ui.AsyncButton("Baixar imagem e abrir no GIS", async () => await Download());
        download.HorizontalAlignment = HorizontalAlignment.Left;
        download.Margin = new Thickness(0, 9, 0, 8);
        form.Children.Add(download);
        if (!session.IsOpen)
        {
            download.IsEnabled = false;
            Ui.AddHelp(form, "Abra ou crie um projeto para salvar a imagem como uma camada do GIS.");
        }
        Children.Add(Ui.Scroll(form));
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
        double minLon = ToLongitude(layer.Extent.Left), maxLon = ToLongitude(layer.Extent.Right);
        // Rect.Top is the smaller projected Y here because the map extent uses
        // WPF's downward-positive screen coordinates; normalize after projection.
        double minLat = Math.Min(ToLatitude(layer.Extent.Top), ToLatitude(layer.Extent.Bottom));
        double maxLat = Math.Max(ToLatitude(layer.Extent.Top), ToLatitude(layer.Extent.Bottom));
        minLongitude.Text = Format(minLon);
        minLatitude.Text = Format(minLat);
        maxLongitude.Text = Format(maxLon);
        maxLatitude.Text = Format(maxLat);
    }

    private async Task Download()
    {
        if (!session.IsOpen) throw new InvalidOperationException("Abra ou crie um projeto antes de baixar imagens.");
        if (date.SelectedDate is not DateTime selectedDate) throw new InvalidOperationException("Informe a data da composição.");
        double xmin = Parse(minLongitude), ymin = Parse(minLatitude), xmax = Parse(maxLongitude), ymax = Parse(maxLatitude);
        if (xmin < -180 || xmax > 180 || ymin < -90 || ymax > 90 || xmin >= xmax || ymin >= ymax)
            throw new InvalidOperationException("Informe limites WGS84 válidos, com os valores mínimos antes dos máximos.");
        if (!int.TryParse(imageWidth.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requestedWidth) || requestedWidth is < 256 or > 4096)
            throw new InvalidOperationException("A largura deve estar entre 256 e 4096 pixels.");

        string key = WindowsCredentialStore.ReadNoaaToken();
        string selectedProduct = product.SelectedItem?.ToString() ?? "NDVI";
        string selectedCadence = cadence.SelectedItem?.ToString() ?? "Mensal";
        owner.Log($"Consultando imagem {selectedProduct} da NOAA…");
        NoaaNdviImage image = await NoaaNdviClient.DownloadAsync(selectedProduct, selectedCadence, selectedDate, xmin, ymin, xmax, ymax,
            requestedWidth, key, owner.OperationCancellation);
        string path = OutputPath(selectedProduct, selectedCadence, selectedDate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] geotiff = SatelliteGeoTiffWriter.FromPng(image.Png, image.XMin, image.YMin, image.XMax, image.YMax);
        await File.WriteAllBytesAsync(path, geotiff, owner.OperationCancellation);
        await owner.RegisterNoaaNdviImage(path, selectedProduct);
    }

    private string OutputPath(string selectedProduct, string selectedCadence, DateTime selectedDate)
    {
        string folder = session.FilePath.Length > 0
            ? Path.Combine(Path.GetDirectoryName(session.FilePath)!, Path.GetFileNameWithoutExtension(session.FilePath) + "_camadas")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DENDRO", "imagens-noaa");
        string stem = selectedProduct == "RGB"
            ? $"RGB_NOAA_{selectedDate:yyyyMMdd}"
            : $"NDVI_NOAA_{selectedCadence}_{selectedDate:yyyyMMdd}";
        string path = Path.Combine(folder, stem + ".tif");
        for (int suffix = 2; File.Exists(path); suffix++) path = Path.Combine(folder, $"{stem}_{suffix}.tif");
        return path;
    }

    private static double Parse(TextBox box)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            return value;
        throw new InvalidOperationException($"Coordenada inválida: {box.Text}");
    }

    private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static double ToLongitude(double webMercatorX) => webMercatorX / EarthRadius * 180 / Math.PI;
    private static double ToLatitude(double webMercatorY) => (2 * Math.Atan(Math.Exp(webMercatorY / EarthRadius)) - Math.PI / 2) * 180 / Math.PI;

}
