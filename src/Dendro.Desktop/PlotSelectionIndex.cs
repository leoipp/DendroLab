using System.Windows;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal readonly record struct PlotScreenPoint(int Row, float X, float Y);

/// <summary>Immutable screen-space buckets over compact source points for lasso queries.</summary>
internal sealed class PlotSelectionIndex
{
    private const int Cell = 32;
    private readonly Dictionary<(int X, int Y), int[]> buckets;
    private readonly ChartPointView? source;
    private readonly Rect plot;
    private readonly ChartBounds bounds;
    private readonly bool logX;
    private readonly bool logY;
    private readonly int[]? rowOffsets;
    private readonly Dictionary<int, int>? sparseRows;
    internal int Count { get; }

    internal sealed class Builder
    {
        private readonly Dictionary<(int X, int Y), int> counts = new();
        internal int MaxRow { get; private set; } = -1;

        internal void Add(int row, Point position)
        {
            var key = ((int)Math.Floor(position.X / Cell), (int)Math.Floor(position.Y / Cell));
            counts[key] = counts.GetValueOrDefault(key) + 1;
            MaxRow = Math.Max(MaxRow, row);
        }

        internal Dictionary<(int X, int Y), int> Counts => counts;
    }

    private PlotSelectionIndex()
    {
        buckets = new();
        plot = Rect.Empty;
        bounds = new(0, 1, 0, 1);
    }

    internal static PlotSelectionIndex Empty() => new();

    internal static PlotSelectionIndex Create(
        ChartPointView source,
        int[] sourceIndexes,
        int sourceCount,
        Rect plot,
        ChartBounds bounds,
        bool logX,
        bool logY,
        CancellationToken cancellation = default
    )
    {
        var result = new PlotSelectionIndex(source, sourceIndexes, sourceCount, plot, bounds, logX, logY, cancellation);
        return result;
    }

    internal static PlotSelectionIndex Create(
        ChartPointView source,
        int[] sourceIndexes,
        int sourceCount,
        Builder builder,
        Rect plot,
        ChartBounds bounds,
        bool logX,
        bool logY,
        CancellationToken cancellation = default
    ) => new(source, sourceIndexes, sourceCount, builder.Counts, builder.MaxRow,
        plot, bounds, logX, logY, cancellation);

    private PlotSelectionIndex(
        ChartPointView source,
        int[] sourceIndexes,
        int sourceCount,
        Dictionary<(int X, int Y), int> counts,
        int maxRow,
        Rect plot,
        ChartBounds bounds,
        bool logX,
        bool logY,
        CancellationToken cancellation
    )
    {
        this.source = source;
        this.plot = plot;
        this.bounds = bounds;
        this.logX = logX;
        this.logY = logY;
        Count = sourceCount;

        buckets = new Dictionary<(int X, int Y), int[]>(counts.Count);
        foreach (var pair in counts)
        {
            cancellation.ThrowIfCancellationRequested();
            buckets.Add(pair.Key, new int[pair.Value]);
            counts[pair.Key] = 0;
        }
        bool useDenseRows = maxRow >= 0 && maxRow <= sourceCount * 4L + 4096 && maxRow <= 8_000_000;
        if (useDenseRows)
        {
            rowOffsets = new int[maxRow + 1];
            Array.Fill(rowOffsets, -1);
        }
        else sparseRows = new Dictionary<int, int>(sourceCount);
        for (int item = 0; item < sourceCount; item++)
        {
            if ((item & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            int pointIndex = sourceIndexes[item];
            var point = source.Get(pointIndex);
            var key = CellKey(Position(point));
            var bucket = buckets[key];
            bucket[counts[key]++] = pointIndex;
            if (useDenseRows) rowOffsets![point.Row] = pointIndex;
            else sparseRows![point.Row] = pointIndex;
        }
    }

    private PlotSelectionIndex(
        ChartPointView source,
        int[] sourceIndexes,
        int sourceCount,
        Rect plot,
        ChartBounds bounds,
        bool logX,
        bool logY,
        CancellationToken cancellation
    )
    {
        this.source = source;
        this.plot = plot;
        this.bounds = bounds;
        this.logX = logX;
        this.logY = logY;
        Count = sourceCount;

        var counts = new Dictionary<(int X, int Y), int>();
        int maxRow = -1;
        for (int item = 0; item < sourceCount; item++)
        {
            if ((item & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            int pointIndex = sourceIndexes[item];
            var point = source.Get(pointIndex);
            Point position = Position(point);
            maxRow = Math.Max(maxRow, point.Row);
            var key = CellKey(position);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        buckets = new Dictionary<(int X, int Y), int[]>(counts.Count);
        foreach (var pair in counts)
        {
            cancellation.ThrowIfCancellationRequested();
            buckets.Add(pair.Key, new int[pair.Value]);
            counts[pair.Key] = 0;
        }
        bool useDenseRows = maxRow >= 0 && maxRow <= sourceCount * 4L + 4096 && maxRow <= 8_000_000;
        if (useDenseRows)
        {
            rowOffsets = new int[maxRow + 1];
            Array.Fill(rowOffsets, -1);
        }
        else sparseRows = new Dictionary<int, int>(sourceCount);
        for (int item = 0; item < sourceCount; item++)
        {
            if ((item & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            int pointIndex = sourceIndexes[item];
            var point = source.Get(pointIndex);
            var key = CellKey(Position(point));
            var bucket = buckets[key];
            bucket[counts[key]++] = pointIndex;
            if (useDenseRows) rowOffsets![point.Row] = pointIndex;
            else sparseRows![point.Row] = pointIndex;
        }
    }

    private Point Position(ChartPoint point)
    {
        double x = logX ? Math.Log10(point.X) : point.X;
        double y = logY ? Math.Log10(point.Y) : point.Y;
        return new(
            plot.Left + (x - bounds.XMin) / (bounds.XMax - bounds.XMin) * plot.Width,
            plot.Bottom - (y - bounds.YMin) / (bounds.YMax - bounds.YMin) * plot.Height
        );
    }

    private (int X, int Y) CellKey(Point position) =>
        ((int)Math.Floor(position.X / Cell), (int)Math.Floor(position.Y / Cell));

    private PlotScreenPoint ScreenPoint(int pointIndex)
    {
        var point = source!.Get(pointIndex);
        Point position = Position(point);
        return new(point.Row, (float)position.X, (float)position.Y);
    }

    internal bool TryPosition(int row, out Point position)
    {
        // Selections can arrive from the violin while the scatter plot is still
        // preparing its screen index (or during a resize). In that state there is
        // deliberately no row dictionary yet, so simply defer drawing the marker.
        if (source is null || rowOffsets is null && sparseRows is null)
        {
            position = default;
            return false;
        }
        int pointIndex = rowOffsets != null
            ? row >= 0 && row < rowOffsets.Length ? rowOffsets[row] : -1
            : sparseRows!.GetValueOrDefault(row, -1);
        if (pointIndex < 0) { position = default; return false; }
        position = Position(source!.Get(pointIndex));
        return true;
    }

    private IEnumerable<PlotScreenPoint> Candidates(Rect area)
    {
        for (int y = (int)Math.Floor(area.Top / Cell); y <= (int)Math.Floor(area.Bottom / Cell); y++)
            for (int x = (int)Math.Floor(area.Left / Cell); x <= (int)Math.Floor(area.Right / Cell); x++)
                if (buckets.TryGetValue((x, y), out var indexes))
                    foreach (int index in indexes)
                    {
                        var point = ScreenPoint(index);
                        if (area.Contains(new Point(point.X, point.Y))) yield return point;
                    }
    }

    internal int[] Nearest(Point position)
    {
        double distance = 144;
        int? row = null;
        foreach (var point in Candidates(new Rect(position.X - 12, position.Y - 12, 24, 24)))
        {
            double dx = point.X - position.X, dy = point.Y - position.Y;
            double current = dx * dx + dy * dy;
            if (current < distance || current == distance && (!row.HasValue || point.Row < row.Value))
            {
                distance = current;
                row = point.Row;
            }
        }
        return row.HasValue ? [row.Value] : [];
    }

    internal int[] Lasso(Point[] polygon, CancellationToken cancellation)
    {
        if (polygon.Length < 3) return [];
        var area = new Rect(new Point(polygon.Min(point => point.X), polygon.Min(point => point.Y)),
            new Point(polygon.Max(point => point.X), polygon.Max(point => point.Y)));
        var result = new List<int>();
        int checkedPoints = 0;
        foreach (var point in Candidates(area))
        {
            if ((checkedPoints++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                double cross = (point.X - a.X) * (b.Y - a.Y) - (point.Y - a.Y) * (b.X - a.X);
                if (Math.Abs(cross) < 1e-7 && point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X)
                    && point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y))
                {
                    inside = true;
                    break;
                }
                if ((a.Y > point.Y) != (b.Y > point.Y)
                    && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            if (inside) result.Add(point.Row);
        }
        return result.ToArray();
    }

    internal int[] Stroke(Point start, Point end, double tolerance, CancellationToken cancellation)
    {
        double pad = Math.Max(1, tolerance);
        var area = new Rect(
            new Point(Math.Min(start.X, end.X) - pad, Math.Min(start.Y, end.Y) - pad),
            new Point(Math.Max(start.X, end.X) + pad, Math.Max(start.Y, end.Y) + pad));
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        double maxDistanceSquared = tolerance * tolerance;
        var rows = new List<int>();
        int checkedPoints = 0;
        foreach (var point in Candidates(area))
        {
            if ((checkedPoints++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
            double t = lengthSquared <= 1e-12 ? 0
                : Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
            double offsetX = point.X - (start.X + t * dx);
            double offsetY = point.Y - (start.Y + t * dy);
            if (offsetX * offsetX + offsetY * offsetY <= maxDistanceSquared) rows.Add(point.Row);
        }
        return rows.Distinct().ToArray();
    }
}
