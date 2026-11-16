using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dendro.Desktop;

internal static class SatelliteGeoTiffWriter
{
    internal static byte[] FromPng(byte[] png, double xMin, double yMin, double xMax, double yMax)
    {
        using var sourceStream = new MemoryStream(png, writable: false);
        var decoder = BitmapDecoder.Create(sourceStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("O provedor não retornou uma imagem válida.");
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        frame.Freeze();
        int width = frame.PixelWidth, height = frame.PixelHeight;
        long byteCount = (long)width * height * 4;
        if (width < 1 || height < 1 || byteCount > 100_000_000)
            throw new InvalidDataException("A imagem excede o limite de 100 MB.");

        int stride = checked(width * 4);
        byte[] bgra = new byte[checked((int)byteCount)];
        frame.CopyPixels(bgra, stride, 0);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        }

        const ushort entryCount = 14;
        uint extraDataOffset = 8 + 2 + entryCount * 12 + 4;
        uint bitsOffset = extraDataOffset;
        uint scaleOffset = bitsOffset + 8;
        uint tiepointOffset = scaleOffset + 24;
        uint geoKeysOffset = tiepointOffset + 48;
        uint pixelOffset = geoKeysOffset + 32;
        if ((ulong)pixelOffset + (ulong)byteCount > uint.MaxValue)
            throw new InvalidDataException("O GeoTIFF excede o limite do formato TIFF clássico.");

        var entries = new (ushort Tag, ushort Type, uint Count, uint Value)[]
        {
            (256, 4, 1, (uint)width),
            (257, 4, 1, (uint)height),
            (258, 3, 4, bitsOffset),
            (259, 3, 1, 1),
            (262, 3, 1, 2),
            (273, 4, 1, pixelOffset),
            (277, 3, 1, 4),
            (278, 4, 1, (uint)height),
            (279, 4, 1, (uint)byteCount),
            (284, 3, 1, 1),
            (338, 3, 1, 2),
            (33550, 12, 3, scaleOffset),
            (33922, 12, 6, tiepointOffset),
            (34735, 3, 16, geoKeysOffset),
        };

        using var output = new MemoryStream(checked((int)(pixelOffset + byteCount)));
        using var writer = new BinaryWriter(output);
        writer.Write((byte)'I'); writer.Write((byte)'I');
        writer.Write((ushort)42); writer.Write(8u);
        writer.Write(entryCount);
        foreach (var entry in entries)
        {
            writer.Write(entry.Tag); writer.Write(entry.Type); writer.Write(entry.Count);
            writer.Write(entry.Value);
        }
        writer.Write(0u);

        writer.Write((ushort)8); writer.Write((ushort)8); writer.Write((ushort)8); writer.Write((ushort)8);
        WriteDoubles(writer, (xMax - xMin) / width, (yMax - yMin) / height, 0);
        WriteDoubles(writer, 0, 0, 0, xMin, yMax, 0);
        ushort[] geoKeys = [1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326];
        foreach (ushort key in geoKeys) writer.Write(key);
        writer.Write(bgra);
        return output.ToArray();
    }

    private static void WriteDoubles(BinaryWriter writer, params double[] values)
    {
        foreach (double value in values)
            writer.Write(value);
    }
}
