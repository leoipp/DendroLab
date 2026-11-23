namespace Dendro.Core.Statistics;

/// <summary>Sample observations from one independently sampled area stratum.</summary>
public sealed record AreaStratumSample(double AreaHa, IReadOnlyList<double> Values);

public static class StratifiedSamplingStatistics
{
    /// <summary>
    /// Two-sided t margin for an area-weighted stratified mean, using the
    /// Satterthwaite degrees of freedom. FPC is omitted because stratum population
    /// sizes are not available in the direct-summary input.
    /// </summary>
    public static double AreaWeightedMarginOfError(
        IEnumerable<AreaStratumSample> strata,
        double confidence,
        int minimumPlots)
    {
        ArgumentNullException.ThrowIfNull(strata);
        if (!double.IsFinite(confidence) || confidence <= 0 || confidence >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        if (minimumPlots < 2) throw new ArgumentOutOfRangeException(nameof(minimumPlots));

        var samples = strata.Select(stratum =>
        {
            ArgumentNullException.ThrowIfNull(stratum);
            double[] values = stratum.Values.Where(double.IsFinite).ToArray();
            return (stratum.AreaHa, Values: values);
        }).ToArray();

        if (samples.Length == 0 || samples.Any(s => !double.IsFinite(s.AreaHa) || s.AreaHa <= 0 || s.Values.Length < 2))
            return double.NaN;
        int totalPlots = samples.Sum(s => s.Values.Length);
        if (totalPlots < minimumPlots) return double.NaN;
        double totalArea = samples.Sum(s => s.AreaHa);
        if (!double.IsFinite(totalArea) || totalArea <= 0) return double.NaN;

        double variance = 0;
        double satterthwaiteDenominator = 0;
        foreach (var sample in samples)
        {
            int n = sample.Values.Length;
            double mean = sample.Values.Average();
            double sampleVariance = sample.Values.Sum(value => (value - mean) * (value - mean)) / (n - 1);
            double weight = sample.AreaHa / totalArea;
            double component = weight * weight * sampleVariance / n;
            variance += component;
            satterthwaiteDenominator += component * component / (n - 1);
        }

        if (!double.IsFinite(variance) || variance < 0) return double.NaN;
        if (variance == 0) return 0;
        if (satterthwaiteDenominator <= 0 || !double.IsFinite(satterthwaiteDenominator)) return double.NaN;
        double degrees = variance * variance / satterthwaiteDenominator;
        if (!double.IsFinite(degrees) || degrees < 1) return double.NaN;
        int conservativeDegrees = Math.Max(1, (int)Math.Floor(degrees));
        return Dendro.Core.Processing.InventoryReport.StudentT(confidence, conservativeDegrees) * Math.Sqrt(variance);
    }
}
