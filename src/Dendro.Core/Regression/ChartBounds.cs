using System.Text.Json.Nodes;

namespace Dendro.Core.Regression;

public readonly record struct ChartBounds(double XMin, double XMax, double YMin, double YMax)
{
    public static ChartBounds Calculate(IEnumerable<(double X, double Y)> visible, JsonObject config)
    {
        double Transform(double n, bool x) => config.B(x ? "log_x" : "log_y") ? Math.Log10(n) : n;
        var points = visible.Select(p => (X: Transform(p.X, true), Y: Transform(p.Y, false)))
            .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
        (double Min, double Max) Axis(bool x)
        {
            string lo = x ? "xmin" : "ymin", hi = x ? "xmax" : "ymax";
            double min = config[lo] != null ? Transform(config.N(lo), x) : points.Length == 0 ? 0 : points.Min(p => x ? p.X : p.Y);
            double max = config[hi] != null ? Transform(config.N(hi), x) : points.Length == 0 ? 1 : points.Max(p => x ? p.X : p.Y);
            if (max <= min) { min -= .5; max += .5; }
            double padding = (max - min) * .04;
            if (config[lo] == null) min -= padding;
            if (config[hi] == null) max += padding;
            return (min, max);
        }
        var x = Axis(true); var y = Axis(false);
        return new(x.Min, x.Max, y.Min, y.Max);
    }
}
