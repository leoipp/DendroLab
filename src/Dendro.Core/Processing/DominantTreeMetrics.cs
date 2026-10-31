namespace Dendro.Core.Processing;

/// <summary>Tracks the largest-DAP trees used for dominant diameter and height.</summary>
public sealed class DominantTreeMetrics
{
    private readonly PriorityQueue<(double Dap, double Height), double> largest;
    private readonly int count;

    public DominantTreeMetrics(int count)
    {
        this.count = Math.Max(1, count);
        largest = new(this.count);
    }

    public void Add(double dap, double height)
    {
        if (!double.IsFinite(dap) || dap <= 0)
            return;
        largest.Enqueue((dap, height), dap);
        if (largest.Count > count)
            largest.Dequeue();
    }

    public double Dap => largest.Count > 0 ? largest.UnorderedItems.Average(item => item.Element.Dap) : double.NaN;

    // Hd is the mean height of the same largest-DAP trees used to calculate DAPd.
    public double Height
    {
        get
        {
            var heights = largest.UnorderedItems.Select(item => item.Element.Height)
                .Where(value => double.IsFinite(value) && value > 0).ToArray();
            return heights.Length > 0 ? heights.Average() : double.NaN;
        }
    }

    public static double AreaWeightedMean(IEnumerable<(double Value, double Area)> values)
    {
        double weighted = 0, area = 0;
        foreach (var (value, weight) in values)
        {
            if (!double.IsFinite(value) || !double.IsFinite(weight) || weight <= 0)
                continue;
            weighted += value * weight;
            area += weight;
        }
        return area > 0 ? weighted / area : double.NaN;
    }
}
