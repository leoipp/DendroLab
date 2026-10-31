using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace Dendro.Desktop;

internal sealed record GisFeature(int Type, Point[][] Parts);
internal sealed record GisRasterLayer(string Path, string Name, BitmapSource Image, Rect Extent);

internal sealed class GisLayer(string path, string name, GisFeature[] features, Rect extent, string? warning)
{
    internal string Path { get; } = path;
    internal string Name { get; } = name;
    internal GisFeature[] Features { get; } = features;
    internal Rect Extent { get; } = extent;
    internal string? Warning { get; } = warning;
    internal bool Visible { get; set; } = true;
}

internal sealed class GisView : DockPanel, IDisposable
{
    private readonly ProjectSession session;
    private readonly GisMapCanvas map = new();
    private readonly TextBlock status = Ui.Text("Carregando camadas GIS…", 12);
    private readonly List<GisLayer> layers = new();
    private readonly List<GisRasterLayer> rasters = new();
    private bool disposed;

    internal GisView(ProjectSession session)
    {
        this.session = session;
        Background = Brushes.White;
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 7, 10, 4) };
        status.Margin = new Thickness(4, 2, 4, 2);
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.MaxWidth = 1050;
        status.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(status);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var mapHost = new Grid();
        mapHost.Children.Add(map);
        var credit = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { FontSize = 11 },
        };
        var attribution = (TextBlock)credit.Child;
        var copyright = new Hyperlink(new Run("© OpenStreetMap contributors")) { NavigateUri = new Uri("https://www.openstreetmap.org/copyright") };
        copyright.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        attribution.Inlines.Add(copyright);
        mapHost.Children.Add(credit);
        Children.Add(mapHost);
    }

    internal async Task Initialize(string? selectedVector = null)
    {
        var paths = session.Files.ToArray();
        var vectorPaths = paths.Where(path => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rasterPaths = paths.Where(IsGeoTiff).ToArray();
        var loadedVectors = await Task.Run(() => vectorPaths.Select(path =>
        {
            try { return (Layer: ShapefileReader.Read(session.Resolve(path), session.ContentName(path)), Error: (string?)null); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            { return (Layer: (GisLayer?)null, Error: $"{session.ContentName(path)}: {ex.Message}"); }
        }).ToArray());
        var loadedRasters = await Task.Run(() => rasterPaths.Select(path =>
        {
            try { return (Layer: GeoTiffReader.Read(session.Resolve(path), session.ContentName(path)), Error: (string?)null); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException)
            { return (Layer: (GisRasterLayer?)null, Error: $"{session.ContentName(path)}: {ex.Message}"); }
        }).ToArray());
        layers.Clear();
        rasters.Clear();
        layers.AddRange(loadedVectors.Where(result => result.Layer != null).Select(result => result.Layer!));
        rasters.AddRange(loadedRasters.Where(result => result.Layer != null).Select(result => result.Layer!));
        map.SetLayers(layers, rasters);
        var failures = loadedVectors.Where(result => result.Error != null).Select(result => result.Error!)
            .Concat(loadedRasters.Where(result => result.Error != null).Select(result => result.Error!)).ToArray();
        var warnings = layers.Where(layer => layer.Warning != null).Select(layer => layer.Name + ": " + layer.Warning).ToArray();
        status.Text = failures.Length > 0 ? string.Join("\n", failures)
            : warnings.Length > 0 ? string.Join("\n", warnings)
            : layers.Count + rasters.Count == 0 ? "Adicione shapefiles ou rasteres GeoTIFF pelo menu de contexto de Mapas base."
            : $"{layers.Count:N0} vetor(es) · {rasters.Count:N0} raster(es). Arraste para mover; use a roda para zoom.";
        map.Fit(layers.Where(layer => selectedVector == null || string.Equals(layer.Path, selectedVector, StringComparison.OrdinalIgnoreCase)).Select(layer => layer.Extent)
            .Concat(rasters.Where(layer => selectedVector == null || string.Equals(layer.Path, selectedVector, StringComparison.OrdinalIgnoreCase)).Select(layer => layer.Extent)).ToArray());
    }

    private static bool IsGeoTiff(string path) => Path.GetExtension(path) is string extension
        && (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase));

    internal bool HasLayer(string path) => layers.Any(layer => string.Equals(layer.Path, path, StringComparison.OrdinalIgnoreCase))
        || rasters.Any(layer => string.Equals(layer.Path, path, StringComparison.OrdinalIgnoreCase));

    internal void FocusLayer(string path)
    {
        var layer = layers.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (layer != null) { map.Fit([layer.Extent]); return; }
        var raster = rasters.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (raster != null) map.Fit([raster.Extent]);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        map.Dispose();
    }
}

internal sealed class GisMapCanvas : FrameworkElement, IDisposable
{
    private const double EarthRadius = 6378137d;
    private const double WorldWidth = 2d * Math.PI * EarthRadius;
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim Requests = new(4, 4);
    private static readonly string TileCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DENDRO", "map-tiles");
    private readonly Dictionary<(int Z, int X, int Y), BitmapSource> tiles = new();
    private readonly HashSet<(int Z, int X, int Y)> pending = new();
    private readonly Dictionary<(int Z, int X, int Y), DateTimeOffset> retryAfter = new();
    private readonly Dictionary<GisLayer, Geometry?[]> geometryCache = new();
    private readonly CancellationTokenSource cancellation = new();
    private IReadOnlyList<GisLayer> layers = Array.Empty<GisLayer>();
    private IReadOnlyList<GisRasterLayer> rasters = Array.Empty<GisRasterLayer>();
    private Point center;
    private double zoom = 4;
    private Point dragOrigin;
    private Point dragCenter;
    private bool dragging;
    private bool hasCenter;
    private bool disposed;
    private bool showBasemap = true;
    internal bool ShowBasemap { get => showBasemap; set { showBasemap = value; InvalidateVisual(); } }

    internal GisMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.SizeAll;
        MouseWheel += (_, e) =>
        {
            double next = Math.Clamp(zoom + (e.Delta > 0 ? 0.5 : -0.5), 1, 19);
            if (Math.Abs(next - zoom) < 0.001) return;
            zoom = next;
            InvalidateVisual();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            dragOrigin = e.GetPosition(this);
            dragCenter = center;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
        };
        MouseMove += (_, e) =>
        {
            if (!dragging) return;
            Point p = e.GetPosition(this);
            double scale = PixelScale(zoom);
            center = new Point(dragCenter.X - (p.X - dragOrigin.X) / scale, dragCenter.Y + (p.Y - dragOrigin.Y) / scale);
            InvalidateVisual();
        };
        MouseLeftButtonUp += (_, _) => { dragging = false; ReleaseMouseCapture(); };
        MouseLeave += (_, _) => { if (!dragging) Cursor = Cursors.Arrow; };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DendroLab/1.0");
        return client;
    }

    internal void SetLayers(IReadOnlyList<GisLayer> value, IReadOnlyList<GisRasterLayer> rasterLayers)
    { layers = value; rasters = rasterLayers; geometryCache.Clear(); InvalidateVisual(); }

    internal void Fit(IReadOnlyList<Rect> visible)
    {
        if (visible.Count == 0)
        {
            center = ToWorld(new Point(-54, -10)); zoom = 4; hasCenter = true; InvalidateVisual(); return;
        }
        Rect bounds = visible[0];
        for (int i = 1; i < visible.Count; i++) bounds.Union(visible[i]);
        center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        hasCenter = true;
        double width = Math.Max(ActualWidth, 500) * 0.84, height = Math.Max(ActualHeight, 400) * 0.84;
        zoom = 4;
        for (int candidate = 19; candidate >= 1; candidate--)
        {
            double scale = PixelScale(candidate);
            if (Math.Max(bounds.Width, 5) * scale <= width && Math.Max(bounds.Height, 5) * scale <= height)
            { zoom = candidate; break; }
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 241, 246)), null, viewport);
        if (!hasCenter || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (ShowBasemap) DrawTiles(dc);
        DrawRasters(dc);
        DrawLayers(dc);
    }

    private void DrawRasters(DrawingContext dc)
    {
        double scale = PixelScale(zoom);
        foreach (var raster in rasters)
        {
            Point upperLeft = Screen(new Point(raster.Extent.Left, raster.Extent.Top), scale);
            Point lowerRight = Screen(new Point(raster.Extent.Right, raster.Extent.Bottom), scale);
            if (lowerRight.X <= 0 || upperLeft.X >= ActualWidth || lowerRight.Y <= 0 || upperLeft.Y >= ActualHeight)
                continue;
            dc.DrawImage(raster.Image, new Rect(upperLeft, lowerRight));
        }
    }

    private void DrawTiles(DrawingContext dc)
    {
        int z = Math.Clamp((int)Math.Floor(zoom), 0, 19), count = 1 << z;
        double centerX = (center.X / WorldWidth + 0.5) * 256 * count;
        double centerY = (0.5 - center.Y / WorldWidth) * 256 * count;
        double ratio = Math.Pow(2, zoom - z), tileSize = 256 * ratio;
        int x0 = (int)Math.Floor((centerX - ActualWidth / (2 * ratio)) / 256);
        int x1 = (int)Math.Floor((centerX + ActualWidth / (2 * ratio)) / 256);
        int y0 = Math.Max(0, (int)Math.Floor((centerY - ActualHeight / (2 * ratio)) / 256));
        int y1 = Math.Min(count - 1, (int)Math.Floor((centerY + ActualHeight / (2 * ratio)) / 256));
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (x < 0 || x >= count) continue;
            var key = (z, x, y);
            var rect = new Rect((x * 256 - centerX) * ratio + ActualWidth / 2, (y * 256 - centerY) * ratio + ActualHeight / 2, tileSize, tileSize);
            if (tiles.TryGetValue(key, out var bitmap)) dc.DrawImage(bitmap, rect);
            else if ((!retryAfter.TryGetValue(key, out var retry) || retry <= DateTimeOffset.UtcNow) && pending.Add(key)) _ = LoadTile(key, cancellation.Token);
        }
    }

    private async Task LoadTile((int Z, int X, int Y) key, CancellationToken token)
    {
        try
        {
            string file = Path.Combine(TileCache, key.Z.ToString(CultureInfo.InvariantCulture), key.X.ToString(CultureInfo.InvariantCulture), key.Y.ToString(CultureInfo.InvariantCulture) + ".png");
            string metaFile = file + ".json";
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            TileMetadata? metadata = null;
            byte[]? cached = null;
            if (File.Exists(file))
            {
                cached = await File.ReadAllBytesAsync(file, token);
                try { metadata = JsonSerializer.Deserialize<TileMetadata>(await File.ReadAllTextAsync(metaFile, token)); } catch (Exception) { }
                await DisplayTile(key, cached, token);
            }
            bool fresh = cached != null && metadata != null && metadata.ExpiresUtc > DateTimeOffset.UtcNow;
            if (!fresh)
            {
                await Requests.WaitAsync(token);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://tile.openstreetmap.org/{key.Z}/{key.X}/{key.Y}.png");
                    if (metadata?.ETag is string etag && EntityTagHeaderValue.TryParse(etag, out var parsedTag)) request.Headers.IfNoneMatch.Add(parsedTag);
                    if (metadata?.LastModifiedUtc is DateTimeOffset modified) request.Headers.IfModifiedSince = modified;
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
                    {
                        metadata!.ExpiresUtc = TileExpiry(response);
                        await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(metadata), token);
                        await DisplayTile(key, cached, token);
                    }
                    else if (response.IsSuccessStatusCode)
                    {
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
                        await File.WriteAllBytesAsync(file, bytes, token);
                        var headers = response.Content.Headers;
                        metadata = new TileMetadata
                        {
                            ExpiresUtc = TileExpiry(response),
                            ETag = response.Headers.ETag?.ToString(),
                            LastModifiedUtc = headers.LastModified ?? response.Content.Headers.LastModified,
                        };
                        await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(metadata), token);
                        await DisplayTile(key, bytes, token);
                    }
                    else await SetTileRetry(key);
                }
                finally { Requests.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException) { await SetTileRetry(key); }
        catch (HttpRequestException) { await SetTileRetry(key); }
        catch (IOException) { await SetTileRetry(key); }
        catch (UnauthorizedAccessException) { await SetTileRetry(key); }
        finally
        {
            if (!disposed) await Dispatcher.InvokeAsync(() => { pending.Remove(key); InvalidateVisual(); });
        }
    }

    private Task SetTileRetry((int Z, int X, int Y) key) => disposed ? Task.CompletedTask
        : Dispatcher.InvokeAsync(() => retryAfter[key] = DateTimeOffset.UtcNow.AddSeconds(45)).Task;

    private static DateTimeOffset TileExpiry(HttpResponseMessage response) => response.Headers.CacheControl?.MaxAge is TimeSpan maxAge
        ? DateTimeOffset.UtcNow + maxAge
        : response.Content.Headers.Expires ?? DateTimeOffset.UtcNow.AddDays(7);

    private async Task DisplayTile((int Z, int X, int Y) key, byte[] bytes, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (disposed || token.IsCancellationRequested) return;
            try
            {
                var bitmap = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                tiles[key] = bitmap;
            }
            catch (NotSupportedException) { }
        });
    }

    private void DrawLayers(DrawingContext dc)
    {
        double scale = PixelScale(zoom);
        var matrix = new Matrix(scale, 0, 0, -scale, ActualWidth / 2 - center.X * scale, ActualHeight / 2 + center.Y * scale);
        var colors = new[] { Color.FromRgb(220, 57, 70), Color.FromRgb(26, 125, 185), Color.FromRgb(30, 145, 97), Color.FromRgb(140, 78, 172), Color.FromRgb(226, 132, 36) };
        for (int li = 0; li < layers.Count; li++)
        {
            var layer = layers[li]; if (!layer.Visible) continue;
            var stroke = new SolidColorBrush(colors[li % colors.Length]); stroke.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(45, stroke.Color.R, stroke.Color.G, stroke.Color.B)); fill.Freeze();
            var pen = new Pen(stroke, Math.Clamp(1.2 * Math.Pow(2, zoom - Math.Floor(zoom)), 1, 3) / scale); pen.Freeze();
            var geometries = GetGeometries(layer);
            foreach (var feature in layer.Features.Where(feature => feature.Type is 1 or 8))
            foreach (var point in feature.Parts.SelectMany(part => part))
            {
                Point p = Screen(point, scale);
                if (p.X < -6 || p.Y < -6 || p.X > ActualWidth + 6 || p.Y > ActualHeight + 6) continue;
                dc.DrawEllipse(Brushes.White, new Pen(stroke, 1), p, 4, 4); dc.DrawEllipse(stroke, null, p, 2.4, 2.4);
            }
            dc.PushTransform(new MatrixTransform(matrix));
            for (int fi = 0; fi < layer.Features.Length; fi++)
            {
                var feature = layer.Features[fi];
                if (feature.Type is 1 or 8) continue;
                var geometry = geometries[fi];
                if (geometry == null) continue;
                dc.DrawGeometry(feature.Type == 5 ? fill : null, pen, geometry);
            }
            dc.Pop();
        }
    }

    private Geometry?[] GetGeometries(GisLayer layer)
    {
        if (geometryCache.TryGetValue(layer, out var cached)) return cached;
        var geometries = new Geometry?[layer.Features.Length];
        for (int i = 0; i < layer.Features.Length; i++)
        {
            var feature = layer.Features[i];
            if (feature.Type is 1 or 8) continue;
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var context = geometry.Open())
            {
                foreach (var part in feature.Parts)
                {
                    if (part.Length == 0) continue;
                    context.BeginFigure(part[0], feature.Type == 5, feature.Type == 5);
                    if (part.Length > 1) context.PolyLineTo(part.Skip(1).ToArray(), true, false);
                }
            }
            geometry.Freeze(); geometries[i] = geometry;
        }
        geometryCache[layer] = geometries;
        return geometries;
    }

    private Point Screen(Point world, double scale) => new((world.X - center.X) * scale + ActualWidth / 2, (center.Y - world.Y) * scale + ActualHeight / 2);

    private static double PixelScale(double level) => 256 * Math.Pow(2, level) / WorldWidth;
    private static Point ToWorld(Point lonLat)
    {
        double lon = Math.Clamp(lonLat.X, -180, 180), lat = Math.Clamp(lonLat.Y, -85.05112878, 85.05112878) * Math.PI / 180;
        return new Point(EarthRadius * lon * Math.PI / 180, EarthRadius * Math.Log(Math.Tan(Math.PI / 4 + lat / 2)));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel(); cancellation.Dispose();
    }

    private sealed class TileMetadata
    {
        public DateTimeOffset ExpiresUtc { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastModifiedUtc { get; set; }
    }
}

internal static class GeoTiffReader
{
    private sealed record Tag(ushort Type, uint Count, byte[] Data);
    private const double SemiMajor = 6378137d;
    private const double Flattening = 1d / 298.257223563d;
    private const double Scale = 0.9996d;

    internal static GisRasterLayer Read(string path, string name)
    {
        var tags = ReadTags(path);
        double[] pixelScale = Doubles(tags, 33550);
        double[] tiepoint = Doubles(tags, 33922);
        if (pixelScale.Length < 2 || tiepoint.Length < 6)
            throw new NotSupportedException("O TIFF não contém georreferenciamento GeoTIFF (escala e ponto de amarração).");
        double[] keys = Shorts(tags, 34735).Select(value => (double)value).ToArray();
        int epsg = GeoEpsg(keys);
        if (epsg == 0) throw new NotSupportedException("Não foi possível identificar o sistema de coordenadas do GeoTIFF.");
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("O GeoTIFF não contém uma imagem raster.");
        BitmapSource image = decoder.Frames[0];
        if (image.PixelWidth < 1 || image.PixelHeight < 1 || (long)image.PixelWidth * image.PixelHeight > 180_000_000)
            throw new InvalidDataException("A imagem raster excede o limite de 180 megapixels.");
        image.Freeze();

        // GeoTIFF PixelScale + Tiepoint describes the common north-up raster layout.
        double x0 = tiepoint[3] - tiepoint[0] * pixelScale[0];
        double y0 = tiepoint[4] + tiepoint[1] * pixelScale[1];
        double x1 = x0 + image.PixelWidth * pixelScale[0];
        double y1 = y0 - image.PixelHeight * pixelScale[1];
        Point[] corners = [ToWorld(new(x0, y0), epsg), ToWorld(new(x1, y0), epsg),
            ToWorld(new(x1, y1), epsg), ToWorld(new(x0, y1), epsg)];
        Rect extent = new(corners[0], new Size(0, 0));
        foreach (Point corner in corners.Skip(1)) extent.Union(corner);
        if (!double.IsFinite(extent.Width) || !double.IsFinite(extent.Height) || extent.Width <= 0 || extent.Height <= 0)
            throw new InvalidDataException("A extensão geográfica do raster é inválida.");
        return new GisRasterLayer(path, name, image, extent);
    }

    private static Dictionary<ushort, Tag> ReadTags(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        byte[] header = reader.ReadBytes(8);
        if (header.Length != 8 || !((header[0] == 'I' && header[1] == 'I') || (header[0] == 'M' && header[1] == 'M')))
            throw new InvalidDataException("Cabeçalho TIFF inválido.");
        bool little = header[0] == 'I';
        ushort U16(ReadOnlySpan<byte> bytes) => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        uint U32(ReadOnlySpan<byte> bytes) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (U16(header.AsSpan(2, 2)) != 42) throw new NotSupportedException("O formato BigTIFF ainda não é suportado.");
        uint offset = U32(header.AsSpan(4, 4));
        if (offset > stream.Length - 2) throw new InvalidDataException("Índice TIFF fora dos limites do arquivo.");
        stream.Position = offset;
        ushort entries = U16(reader.ReadBytes(2));
        var tags = new Dictionary<ushort, Tag>();
        for (int i = 0; i < entries; i++)
        {
            byte[] entry = reader.ReadBytes(12);
            if (entry.Length != 12) throw new InvalidDataException("Diretório TIFF incompleto.");
            ushort id = U16(entry.AsSpan(0, 2)), type = U16(entry.AsSpan(2, 2));
            uint count = U32(entry.AsSpan(4, 4));
            if (id is not (33550 or 33922 or 34735)) continue;
            int size = type switch { 3 => 2, 4 => 4, 12 => 8, _ => throw new NotSupportedException($"Tipo de tag GeoTIFF não suportado ({type}).") };
            long length = (long)count * size;
            if (length <= 0 || length > 1_000_000) throw new InvalidDataException("Tag GeoTIFF excede o limite permitido.");
            byte[] data;
            if (length <= 4) data = entry.AsSpan(8, (int)length).ToArray();
            else
            {
                uint dataOffset = U32(entry.AsSpan(8, 4));
                if (dataOffset > stream.Length - length) throw new InvalidDataException("Tag GeoTIFF fora dos limites do arquivo.");
                stream.Position = dataOffset;
                data = reader.ReadBytes((int)length);
                stream.Position = offset + 2L + (i + 1) * 12L;
            }
            if (!little && size > 1)
                for (int start = 0; start < data.Length; start += size)
                    Array.Reverse(data, start, size);
            tags[id] = new Tag(type, count, data);
        }
        return tags;
    }

    private static double[] Doubles(Dictionary<ushort, Tag> tags, ushort id)
    {
        if (!tags.TryGetValue(id, out var tag) || tag.Type != 12) return [];
        return Enumerable.Range(0, (int)tag.Count).Select(i => BitConverter.Int64BitsToDouble(
            unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(tag.Data.AsSpan(i * 8, 8))))).ToArray();
    }

    private static ushort[] Shorts(Dictionary<ushort, Tag> tags, ushort id)
    {
        if (!tags.TryGetValue(id, out var tag) || tag.Type != 3) return [];
        return Enumerable.Range(0, (int)tag.Count).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(tag.Data.AsSpan(i * 2, 2))).ToArray();
    }

    private static int GeoEpsg(double[] keyDirectory)
    {
        if (keyDirectory.Length < 4) return 0;
        int count = (int)keyDirectory[3];
        int geographic = 0, projected = 0;
        for (int i = 0, start = 4; i < count && start + 3 < keyDirectory.Length; i++, start += 4)
        {
            int key = (int)keyDirectory[start], location = (int)keyDirectory[start + 1];
            if (location == 0 && key is 3072 or 2048)
            {
                int code = (int)keyDirectory[start + 3];
                if (code is not (0 or 32767))
                {
                    if (key == 3072) projected = code;
                    else geographic = code;
                }
            }
        }
        return projected != 0 ? projected : geographic;
    }

    private static Point ToWorld(Point coordinate, int epsg)
    {
        if (epsg == 3857 || epsg == 102100) return coordinate;
        Point lonLat = epsg == 4326 ? coordinate : IsUtm(epsg) ? UTMToLonLat(coordinate, epsg) : throw new NotSupportedException($"Sistema de coordenadas EPSG:{epsg} não suportado. Use EPSG:4326, EPSG:3857 ou UTM WGS84.");
        double lon = Math.Clamp(lonLat.X, -180, 180), lat = Math.Clamp(lonLat.Y, -85.05112878, 85.05112878) * Math.PI / 180;
        return new Point(SemiMajor * lon * Math.PI / 180, SemiMajor * Math.Log(Math.Tan(Math.PI / 4 + lat / 2)));
    }

    private static bool IsUtm(int epsg) => epsg is >= 32601 and <= 32660 or >= 32701 and <= 32760;

    private static Point UTMToLonLat(Point point, int epsg)
    {
        double e2 = Flattening * (2 - Flattening), ep2 = e2 / (1 - e2);
        int zone = epsg % 100;
        double x = point.X - 500000, y = point.Y - (epsg >= 32700 ? 10000000 : 0);
        double m = y / Scale, mu = m / (SemiMajor * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
        double phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
            + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
            + 151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu) + 1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);
        double sin = Math.Sin(phi1), cos = Math.Cos(phi1), tan = Math.Tan(phi1);
        double n1 = SemiMajor / Math.Sqrt(1 - e2 * sin * sin), t1 = tan * tan, c1 = ep2 * cos * cos;
        double r1 = SemiMajor * (1 - e2) / Math.Pow(1 - e2 * sin * sin, 1.5), d = x / (n1 * Scale);
        double lat = phi1 - n1 * tan / r1 * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
            + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        double lon0 = (zone * 6 - 183) * Math.PI / 180;
        double lon = lon0 + (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
            + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
        return new Point(lon * 180 / Math.PI, lat * 180 / Math.PI);
    }
}

internal static class ShapefileReader
{
    internal static GisLayer Read(string path, string name)
    {
        Func<Point, Point> project = CreateProjection(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (ReadBigEndianInt32(reader) != 9994) throw new InvalidDataException("Cabeçalho de shapefile inválido.");
        stream.Position = 24;
        int declaredWords = ReadBigEndianInt32(reader);
        if (declaredWords * 2L > stream.Length || stream.Length < 100) throw new InvalidDataException("Arquivo .shp incompleto.");
        stream.Position = 32;
        int headerType = reader.ReadInt32();
        stream.Position = 100;
        var features = new List<GisFeature>();
        Rect? bounds = null;
        while (stream.Position + 8 <= Math.Min(stream.Length, declaredWords * 2L))
        {
            _ = ReadBigEndianInt32(reader);
            int bytes = checked(ReadBigEndianInt32(reader) * 2);
            long end = stream.Position + bytes;
            if (bytes < 4 || end > stream.Length) throw new InvalidDataException("Registro vetorial truncado.");
            int type = reader.ReadInt32();
            if (type == 0) { stream.Position = end; continue; }
            int family = Family(type);
            Point[][] parts;
            if (family == 1)
            {
                Point p = new(reader.ReadDouble(), reader.ReadDouble()); parts = [[p]];
            }
            else if (family == 8)
            {
                stream.Position += 32;
                int count = reader.ReadInt32();
                if (count < 0 || count > 20_000_000) throw new InvalidDataException("Quantidade de pontos inválida.");
                var points = new Point[count]; for (int i = 0; i < count; i++) points[i] = new(reader.ReadDouble(), reader.ReadDouble());
                parts = [points];
            }
            else if (family is 3 or 5)
            {
                stream.Position += 32;
                int partCount = reader.ReadInt32(), pointCount = reader.ReadInt32();
                if (partCount < 0 || pointCount < 0 || partCount > pointCount || pointCount > 20_000_000) throw new InvalidDataException("Partes de geometria inválidas.");
                var starts = new int[partCount + 1]; for (int i = 0; i < partCount; i++) starts[i] = reader.ReadInt32(); starts[partCount] = pointCount;
                var points = new Point[pointCount]; for (int i = 0; i < pointCount; i++) points[i] = new(reader.ReadDouble(), reader.ReadDouble());
                parts = new Point[partCount][];
                for (int i = 0; i < partCount; i++) parts[i] = points[starts[i]..starts[i + 1]];
            }
            else throw new NotSupportedException($"Tipo de geometria SHP não suportado ({type}).");
            for (int pi = 0; pi < parts.Length; pi++)
            for (int i = 0; i < parts[pi].Length; i++)
            {
                Point lonLat = project(parts[pi][i]);
                if (!double.IsFinite(lonLat.X) || !double.IsFinite(lonLat.Y) || lonLat.X < -180 || lonLat.X > 180 || lonLat.Y < -90 || lonLat.Y > 90)
                    throw new InvalidDataException("coordenadas inválidas para longitude/latitude; confira o sistema de referência no arquivo .prj.");
                Point world = new(6378137d * lonLat.X * Math.PI / 180, 6378137d * Math.Log(Math.Tan(Math.PI / 4 + Math.Clamp(lonLat.Y, -85.05112878, 85.05112878) * Math.PI / 360)));
                parts[pi][i] = world;
                if (bounds == null) bounds = new Rect(world, new Size(0, 0));
                else { var currentBounds = bounds.Value; currentBounds.Union(world); bounds = currentBounds; }
            }
            features.Add(new GisFeature(family, parts));
            stream.Position = end;
        }
        if (features.Count == 0 || bounds == null) throw new InvalidDataException("O shapefile não contém feições.");
        string? warning = File.Exists(Path.ChangeExtension(path, ".prj")) ? null : "arquivo .prj ausente; as coordenadas foram interpretadas como longitude/latitude.";
        return new GisLayer(path, name, features.ToArray(), bounds.Value, warning);
    }

    private static int Family(int type) => type switch
    {
        1 or 11 or 21 => 1,
        3 or 13 or 23 => 3,
        5 or 15 or 25 => 5,
        8 or 18 or 28 => 8,
        _ => throw new NotSupportedException($"Tipo de geometria SHP não suportado ({type})."),
    };

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (reader.Read(bytes) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private static Func<Point, Point> CreateProjection(string path)
    {
        string prjPath = Path.ChangeExtension(path, ".prj");
        if (!File.Exists(prjPath)) return source => source;
        string wkt = File.ReadAllText(prjPath);
        if (wkt.Contains("Transverse_Mercator", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = Regex.Matches(wkt, "PARAMETER\\s*\\[\\s*\"([^\"]+)\"\\s*,\\s*([-+0-9.Ee]+)", RegexOptions.IgnoreCase)
                .Cast<Match>().ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            var spheroid = Regex.Match(wkt, "SPHEROID\\s*\\[\\s*\"[^\"]+\"\\s*,\\s*([-+0-9.Ee]+)\\s*,\\s*([-+0-9.Ee]+)", RegexOptions.IgnoreCase);
            double a = spheroid.Success ? double.Parse(spheroid.Groups[1].Value, CultureInfo.InvariantCulture) : 6378137d;
            double invF = spheroid.Success ? double.Parse(spheroid.Groups[2].Value, CultureInfo.InvariantCulture) : 298.257223563d;
            return source => InverseTransverseMercator(source, parameters, a, invF);
        }
        if (wkt.Contains("Pseudo_Mercator", StringComparison.OrdinalIgnoreCase)
            || wkt.Contains("Mercator_Auxiliary_Sphere", StringComparison.OrdinalIgnoreCase)
            || wkt.Contains("Popular Visualisation Pseudo Mercator", StringComparison.OrdinalIgnoreCase))
            return source => new Point(source.X / 6378137d * 180 / Math.PI, (2 * Math.Atan(Math.Exp(source.Y / 6378137d)) - Math.PI / 2) * 180 / Math.PI);
        if (Regex.IsMatch(wkt, @"^\s*(GEOGCS|GEOGCRS)\s*\[", RegexOptions.IgnoreCase))
            return source => source;
        throw new NotSupportedException("projeção não reconhecida; informe um .prj geográfico, Web Mercator ou Transversa de Mercator/UTM.");
    }

    private static Point InverseTransverseMercator(Point p, Dictionary<string, double> parameters, double a, double invF)
    {
        double e2 = 2 / invF - 1 / (invF * invF), ep2 = e2 / (1 - e2);
        double lat0 = Param(parameters, "latitude_of_origin") * Math.PI / 180, lon0 = Param(parameters, "central_meridian") * Math.PI / 180;
        double k0 = Param(parameters, "scale_factor", 1), x = p.X - Param(parameters, "false_easting"), y = p.Y - Param(parameters, "false_northing");
        double m = y / k0 + a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256) * lat0;
        double mu = m / (a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));
        double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
        double phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu) + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu) + 151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu) + 1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);
        double sin = Math.Sin(phi1), cos = Math.Cos(phi1), tan = Math.Tan(phi1);
        double n1 = a / Math.Sqrt(1 - e2 * sin * sin), r1 = a * (1 - e2) / Math.Pow(1 - e2 * sin * sin, 1.5), t1 = tan * tan, c1 = ep2 * cos * cos, d = x / (n1 * k0);
        double lat = phi1 - n1 * tan / r1 * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24 + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        double lon = lon0 + (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
        return new Point(lon * 180 / Math.PI, lat * 180 / Math.PI);
    }

    private static double Param(Dictionary<string, double> values, string name, double fallback = 0) => values.TryGetValue(name, out double value) ? value : fallback;
}
