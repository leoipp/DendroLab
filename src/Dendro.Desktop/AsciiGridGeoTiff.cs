using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dendro.Desktop;

internal sealed record AsciiGrid(int Width, int Height, double XMin, double YMin, double CellWidth, double CellHeight,
    double NoData, float[] Values, int Bands = 1);

internal static class AsciiGridGeoTiff
{
    private const int MaxCells = 40_000_000;

    internal static AsciiGrid Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? firstDataRow = null;
        bool readingData = false;
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!readingData && parts.Length >= 2 && IsHeader(parts[0])
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                header[parts[0]] = value;
                continue;
            }
            readingData = true;
            firstDataRow = line;
            break;
        }

        int width = HeaderInt(header, "ncols"), height = HeaderInt(header, "nrows");
        if ((long)width * height > MaxCells) throw new InvalidDataException("O ASCII excede o limite de 40 milhões de células.");
        bool center = header.ContainsKey("xllcenter") && header.ContainsKey("yllcenter");
        bool corner = header.ContainsKey("xllcorner") && header.ContainsKey("yllcorner");
        if (!center && !corner) throw new InvalidDataException("O cabeçalho precisa conter xllcorner/yllcorner ou xllcenter/yllcenter.");
        double x = header[center ? "xllcenter" : "xllcorner"], y = header[center ? "yllcenter" : "yllcorner"];
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("As coordenadas de origem precisam ser finitas.");
        double dx = header.TryGetValue("cellsize", out double cell) ? cell : header.GetValueOrDefault("dx");
        double dy = header.TryGetValue("cellsize", out cell) ? cell : header.GetValueOrDefault("dy", dx);
        if (!(dx > 0) || !(dy > 0) || !double.IsFinite(dx) || !double.IsFinite(dy))
            throw new InvalidDataException("cellsize ou dx/dy deve ser positivo e finito.");
        if (center) { x -= dx / 2; y -= dy / 2; }
        double nodata = (float)header.GetValueOrDefault("nodata_value", -9999);

        var values = new float[checked(width * height)];
        for (int row = 0; row < height; row++)
        {
            string? dataLine = row == 0 ? firstDataRow : ReadNextDataLine(reader);
            if (dataLine == null) throw new InvalidDataException($"O arquivo contém {row} linhas de dados; o cabeçalho declara {height}.");
            string[] parts = dataLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != width) throw new InvalidDataException($"A linha {row + 1} contém {parts.Length} valores; o cabeçalho declara {width} colunas.");
            for (int col = 0; col < width; col++)
            {
                if (!float.TryParse(parts[col], NumberStyles.Float, CultureInfo.InvariantCulture, out float sample)
                    || !float.IsFinite(sample)) throw new InvalidDataException($"Valor inválido na linha {row + 1}, coluna {col + 1}.");
                values[row * width + col] = sample;
            }
        }
        if (ReadNextDataLine(reader) != null) throw new InvalidDataException("O arquivo contém mais linhas de dados que o indicado no cabeçalho.");
        return new AsciiGrid(width, height, x, y, dx, dy, nodata, values);
    }

    internal static GisRasterLayer ReadLayer(string path, string name, int epsg)
    {
        if (epsg <= 0) throw new InvalidDataException("Informe o EPSG do raster ASCII para exibi-lo no mapa.");
        var grid = Read(path);
        var image = Render(grid);
        double right = grid.XMin + grid.Width * grid.CellWidth, top = grid.YMin + grid.Height * grid.CellHeight;
        Point[] corners = [GeoTiffReader.ToWorld(new(grid.XMin, top), epsg), GeoTiffReader.ToWorld(new(right, top), epsg),
            GeoTiffReader.ToWorld(new(right, grid.YMin), epsg), GeoTiffReader.ToWorld(new(grid.XMin, grid.YMin), epsg)];
        Rect extent = new(corners[0], new Size(0, 0));
        foreach (Point corner in corners.Skip(1)) extent.Union(corner);
        return new GisRasterLayer(path, name, image, extent);
    }

    internal static BitmapSource Render(AsciiGrid grid)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (int i = 0; i < grid.Values.Length; i++)
        {
            float value = grid.Values[i];
            if (grid.Bands > 1 && i % grid.Bands != 0) continue;
            if (float.IsFinite(value) && (float.IsNaN((float)grid.NoData) || value != (float)grid.NoData))
            { min = Math.Min(min, value); max = Math.Max(max, value); }
        }
        int stride = checked(grid.Width * 4);
        byte[] pixels = new byte[checked(stride * grid.Height)];
        double span = max > min ? max - min : 1;
        for (int i = 0; i < grid.Width * grid.Height; i++)
        {
            float value = grid.Values[i * grid.Bands];
            if (!float.IsFinite(value) || (!float.IsNaN((float)grid.NoData) && value == (float)grid.NoData) || !float.IsFinite(min)) continue;
            int p = i * 4;
            if (grid.Bands >= 3)
            {
                pixels[p] = (byte)Math.Clamp(grid.Values[i * grid.Bands + 2], 0, 255);
                pixels[p + 1] = (byte)Math.Clamp(grid.Values[i * grid.Bands + 1], 0, 255);
                pixels[p + 2] = (byte)Math.Clamp(grid.Values[i * grid.Bands], 0, 255);
                pixels[p + 3] = grid.Bands >= 4 ? (byte)Math.Clamp(grid.Values[i * grid.Bands + 3], 0, 255) : (byte)255;
            }
            else
            {
                byte gray = (byte)Math.Clamp(30 + (value - min) / span * 215, 0, 255);
                pixels[p] = pixels[p + 1] = pixels[p + 2] = gray; pixels[p + 3] = 255;
            }
        }
        var image = BitmapSource.Create(grid.Width, grid.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    internal static void Write(string inputPath, string outputPath, int epsg)
    {
        var grid = Read(inputPath);
        Write(outputPath, grid, epsg);
    }

    internal static void Write(string outputPath, AsciiGrid grid, int epsg)
    {
        if (epsg <= 0) throw new InvalidDataException("O código EPSG deve ser positivo.");
        WriteFloatGeoTiff(outputPath, grid, epsg);
    }

    private static void WriteFloatGeoTiff(string path, AsciiGrid grid, int epsg)
    {
        ushort entries = (ushort)(grid.Bands == 4 ? 16 : 15);
        uint ifdEnd = 8u + 2u + (uint)entries * 12u + 4u;
        byte[] geoKeys = new byte[32];
        ushort[] keys = [1, 1, 0, 3, 1024, 0, 1, (ushort)(epsg == 4326 ? 2 : 1),
            1025, 0, 1, 1, (ushort)(epsg == 4326 ? 2048 : 3072), 0, 1, checked((ushort)epsg)];
        for (int i = 0; i < keys.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(geoKeys.AsSpan(i * 2), keys[i]);
        byte[] scale = new byte[24]; PutDouble(scale, 0, grid.CellWidth); PutDouble(scale, 8, grid.CellHeight);
        byte[] tie = new byte[48]; PutDouble(tie, 24, grid.XMin); PutDouble(tie, 32, grid.YMin + grid.Height * grid.CellHeight);
        byte[] nodata = Encoding.ASCII.GetBytes(grid.NoData.ToString("R", CultureInfo.InvariantCulture) + "\0");
        uint bitsOffset = ifdEnd, scaleOffset = bitsOffset + (uint)(grid.Bands > 2 ? grid.Bands * 2 : 0), tieOffset = scaleOffset + (uint)scale.Length;
        uint keysOffset = tieOffset + (uint)tie.Length, nodataOffset = keysOffset + (uint)geoKeys.Length;
        uint dataOffset = nodataOffset + (uint)nodata.Length;
        ulong byteCount = (ulong)grid.Values.Length * sizeof(float);
        if ((ulong)dataOffset + byteCount > uint.MaxValue) throw new InvalidDataException("O GeoTIFF excede o limite do formato TIFF clássico (4 GB).");
        uint bytes = (uint)byteCount;
        var tags = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256,4,1,(uint)grid.Width),(257,4,1,(uint)grid.Height),(258,3,(uint)grid.Bands,grid.Bands > 2 ? bitsOffset : 32),(259,3,1,1),(262,3,1,grid.Bands > 1 ? 2u : 1u),
            (273,4,1,dataOffset),(277,3,1,(uint)grid.Bands),(278,4,1,(uint)grid.Height),(279,4,1,bytes),(284,3,1,1),
            (339,3,(uint)grid.Bands,grid.Bands > 2 ? bitsOffset + (uint)(grid.Bands * 2) : 3),(33550,12,3,scaleOffset),(33922,12,6,tieOffset),(34735,3,16,keysOffset),(42113,2,(uint)nodata.Length,nodataOffset),
        };
        if (grid.Bands == 4) tags.Add((338, 3, 1, 2));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write(8u);
        writer.Write(entries);
        foreach (var (tag, type, count, value) in tags.OrderBy(entry => entry.Tag))
        {
            writer.Write(tag); writer.Write(type); writer.Write(count);
            if (type == 3 && count == 1) { writer.Write((ushort)value); writer.Write((ushort)0); }
            else writer.Write(value);
        }
        writer.Write(0u);
        if (grid.Bands > 2)
            for (int i = 0; i < grid.Bands; i++) writer.Write((ushort)32);
        if (grid.Bands > 2)
            for (int i = 0; i < grid.Bands; i++) writer.Write((ushort)3);
        writer.Write(scale); writer.Write(tie); writer.Write(geoKeys); writer.Write(nodata);
        byte[] row = new byte[checked(grid.Width * grid.Bands * sizeof(float))];
        for (int r = 0; r < grid.Height; r++)
        {
            int rowStart = r * grid.Width * grid.Bands;
            for (int c = 0; c < grid.Width * grid.Bands; c++)
                BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(c * sizeof(float)), BitConverter.SingleToInt32Bits(grid.Values[rowStart + c]));
            writer.Write(row);
        }
    }

    private static void PutDouble(byte[] target, int offset, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(target.AsSpan(offset), BitConverter.DoubleToInt64Bits(value));

    private static int HeaderInt(Dictionary<string, double> values, string key)
    {
        if (!values.TryGetValue(key, out double value) || value < 1 || value > int.MaxValue || value != Math.Truncate(value))
            throw new InvalidDataException($"O cabeçalho não informa {key} válido.");
        return (int)value;
    }

    private static string? ReadNextDataLine(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
        return null;
    }

    private static bool IsHeader(string value) => value.Equals("ncols", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nrows", StringComparison.OrdinalIgnoreCase) || value.Equals("xllcorner", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yllcorner", StringComparison.OrdinalIgnoreCase) || value.Equals("xllcenter", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yllcenter", StringComparison.OrdinalIgnoreCase) || value.Equals("cellsize", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nodata_value", StringComparison.OrdinalIgnoreCase) || value.Equals("dx", StringComparison.OrdinalIgnoreCase)
        || value.Equals("dy", StringComparison.OrdinalIgnoreCase);
}
