using System.Windows;

namespace Dendro.Desktop;

internal readonly record struct PlotScreenPoint(int Row, double X, double Y);

/// <summary>Immutable screen-space buckets shared with background lasso queries.</summary>
internal sealed class PlotSelectionIndex
{
    private const int Cell = 32;
    private readonly Dictionary<(int X, int Y), PlotScreenPoint[]> buckets;
    internal readonly Dictionary<int, Point> Positions;
    internal int Count { get; }
    internal PlotSelectionIndex(IEnumerable<PlotScreenPoint> source)
    {
        var points = source.ToArray(); Count = points.Length;
        buckets = points.GroupBy(p => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        Positions = points.ToDictionary(p => p.Row, p => new Point(p.X, p.Y));
    }
    private IEnumerable<PlotScreenPoint> Candidates(Rect area)
    {
        for (int y = (int)Math.Floor(area.Top / Cell); y <= (int)Math.Floor(area.Bottom / Cell); y++)
            for (int x = (int)Math.Floor(area.Left / Cell); x <= (int)Math.Floor(area.Right / Cell); x++)
                if (buckets.TryGetValue((x, y), out var points))
                    foreach (var point in points)
                        if (area.Contains(new Point(point.X, point.Y))) yield return point;
    }
    internal int[] Nearest(Point position)
    {
        double distance = 144; int? row = null;
        foreach (var p in Candidates(new Rect(position.X - 12, position.Y - 12, 24, 24))) {
            double d = (p.X - position.X) * (p.X - position.X) + (p.Y - position.Y) * (p.Y - position.Y);
            if (d < distance || d == distance && (!row.HasValue || p.Row < row.Value)) { distance = d; row = p.Row; }
        }
        return row.HasValue ? [row.Value] : [];
    }
    internal int[] Lasso(Point[] polygon, CancellationToken cancellation)
    {
        if (polygon.Length < 3) return [];
        var area = new Rect(new Point(polygon.Min(p => p.X), polygon.Min(p => p.Y)),
            new Point(polygon.Max(p => p.X), polygon.Max(p => p.Y)));
        var result = new List<int>(); int checkedPoints = 0;
        foreach (var p in Candidates(area)) {
            if ((checkedPoints++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++) {
                var a = polygon[i]; var b = polygon[j];
                double cross = (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
                if (Math.Abs(cross) < 1e-7 && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
                    && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y)) { inside = true; break; }
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            if (inside) result.Add(p.Row);
        }
        return result.ToArray();
    }
}
