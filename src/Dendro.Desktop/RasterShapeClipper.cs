using System.IO;
using System.Windows;

namespace Dendro.Desktop;

internal sealed record RasterClipOutput(string FeatureName, string Path, int Width, int Height, long ValidCells);

internal static class RasterShapeClipper
{
    internal static IReadOnlyList<RasterClipOutput> Clip(AsciiGrid raster, int epsg, GisLayer shapes, DbfTable dbf,
        int fieldIndex, string outputFolder, bool crop, bool allTouched, string prefix, string suffix, float? nodataOverride)
    {
        if (shapes.Features.Any(feature => feature.Type != 5))
            throw new InvalidDataException("O shapefile de corte precisa conter polígonos.");
        Directory.CreateDirectory(outputFolder);
        var outputs = new List<RasterClipOutput>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int fi = 0; fi < shapes.Features.Length; fi++)
        {
            GisFeature feature = shapes.Features[fi];
            int record = feature.RecordIndex >= 0 ? feature.RecordIndex : fi;
            if (record >= dbf.Rows.Count) continue;
            string originalName = dbf.ReadValue(dbf.Rows[record], fieldIndex).Trim();
            if (originalName.Length == 0) originalName = $"feature_{record + 1}";
            string stem = Sanitize(prefix + originalName + suffix);
            if (stem.Length == 0) stem = $"feature_{record + 1}";
            string unique = stem;
            for (int index = 2; File.Exists(Path.Combine(outputFolder, unique + ".tif")) || !usedNames.Add(unique); index++) unique = $"{stem}_{index}";

            Point[][] rings = feature.Parts.Select(part => part.Select(point => GeoTiffReader.FromWorld(point, epsg)).ToArray()).ToArray();
            Point[] all = rings.SelectMany(ring => ring).ToArray();
            if (all.Length < 3) continue;
            double minX = all.Min(point => point.X), maxX = all.Max(point => point.X);
            double minY = all.Min(point => point.Y), maxY = all.Max(point => point.Y);
            double rasterMaxX = raster.XMin + raster.Width * raster.CellWidth;
            double rasterMaxY = raster.YMin + raster.Height * raster.CellHeight;
            if (maxX <= raster.XMin || minX >= rasterMaxX || maxY <= raster.YMin || minY >= rasterMaxY) continue;

            int colStart = crop ? Math.Clamp((int)Math.Floor((minX - raster.XMin) / raster.CellWidth), 0, raster.Width - 1) : 0;
            int colEnd = crop ? Math.Clamp((int)Math.Ceiling((maxX - raster.XMin) / raster.CellWidth), 1, raster.Width) : raster.Width;
            int rowStart = crop ? Math.Clamp((int)Math.Floor((rasterMaxY - maxY) / raster.CellHeight), 0, raster.Height - 1) : 0;
            int rowEnd = crop ? Math.Clamp((int)Math.Ceiling((rasterMaxY - minY) / raster.CellHeight), 1, raster.Height) : raster.Height;
            if (colEnd <= colStart || rowEnd <= rowStart) continue;

            float outputNoData = nodataOverride ?? (float)raster.NoData;
            int outputBands = raster.Bands;
            var values = Enumerable.Repeat(outputNoData, checked((colEnd - colStart) * (rowEnd - rowStart) * outputBands)).ToArray();
            long valid = 0;
            for (int row = rowStart; row < rowEnd; row++)
            for (int col = colStart; col < colEnd; col++)
            {
                double left = raster.XMin + col * raster.CellWidth;
                double top = rasterMaxY - row * raster.CellHeight;
                double right = left + raster.CellWidth, bottom = top - raster.CellHeight;
                bool inside = allTouched
                    ? TouchesCell(rings, left, bottom, right, top)
                    : Contains(rings, new Point((left + right) / 2, (bottom + top) / 2));
                if (!inside) continue;
                int sourceOffset = (row * raster.Width + col) * outputBands;
                int targetOffset = ((row - rowStart) * (colEnd - colStart) + col - colStart) * outputBands;
                bool sampleValid = true;
                for (int band = 0; band < outputBands; band++)
                {
                    float sample = raster.Values[sourceOffset + band];
                    if (!float.IsFinite(sample) || (!float.IsNaN((float)raster.NoData) && sample == (float)raster.NoData)) { sampleValid = false; break; }
                }
                if (!sampleValid) continue;
                Array.Copy(raster.Values, sourceOffset, values, targetOffset, outputBands);
                valid++;
            }
            if (valid == 0) continue;
            var outputGrid = new AsciiGrid(colEnd - colStart, rowEnd - rowStart,
                raster.XMin + colStart * raster.CellWidth,
                raster.YMin + (raster.Height - rowEnd) * raster.CellHeight,
                raster.CellWidth, raster.CellHeight, outputNoData, values, outputBands);
            string target = Path.Combine(outputFolder, unique + ".tif");
            try { AsciiGridGeoTiff.Write(target, outputGrid, epsg); }
            catch { if (File.Exists(target)) File.Delete(target); throw; }
            outputs.Add(new RasterClipOutput(originalName, target, outputGrid.Width, outputGrid.Height, valid));
        }
        return outputs;
    }

    internal static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).Distinct().ToArray();
        string safe = new(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray());
        return safe.Trim().TrimEnd('.').Replace(' ', '_');
    }

    private static bool Contains(Point[][] rings, Point point)
    {
        bool inside = false;
        foreach (Point[] ring in rings)
        {
            if (ring.Length < 3) continue;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                Point a = ring[j], b = ring[i];
                if (OnSegment(a, b, point)) return true;
                if ((a.Y > point.Y) != (b.Y > point.Y)
                    && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        }
        return inside;
    }

    private static bool TouchesCell(Point[][] rings, double left, double bottom, double right, double top)
    {
        if (Contains(rings, new Point((left + right) / 2, (bottom + top) / 2))) return true;
        Point[] corners = [new(left, bottom), new(right, bottom), new(right, top), new(left, top)];
        if (corners.Any(point => Contains(rings, point))) return true;
        foreach (Point[] ring in rings)
        {
            for (int i = 0; i < ring.Length; i++)
            {
                Point a = ring[i], b = ring[(i + 1) % ring.Length];
                if (a.X >= left && a.X <= right && a.Y >= bottom && a.Y <= top) return true;
                if (SegmentIntersectsRect(a, b, left, bottom, right, top)) return true;
            }
        }
        return false;
    }

    private static bool SegmentIntersectsRect(Point a, Point b, double left, double bottom, double right, double top)
    {
        if (Math.Max(a.X, b.X) < left || Math.Min(a.X, b.X) > right || Math.Max(a.Y, b.Y) < bottom || Math.Min(a.Y, b.Y) > top) return false;
        Point bl = new(left, bottom), br = new(right, bottom), tr = new(right, top), tl = new(left, top);
        return SegmentsIntersect(a, b, bl, br) || SegmentsIntersect(a, b, br, tr)
            || SegmentsIntersect(a, b, tr, tl) || SegmentsIntersect(a, b, tl, bl);
    }

    private static bool SegmentsIntersect(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point p, Point q, Point r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        return abC * abD <= 0 && cdA * cdB <= 0;
    }

    private static bool OnSegment(Point a, Point b, Point p) =>
        Math.Abs((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) < 1e-9
        && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
        && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
}
