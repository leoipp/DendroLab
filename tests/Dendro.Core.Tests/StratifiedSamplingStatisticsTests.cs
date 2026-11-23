using Dendro.Core.Processing;
using Dendro.Core.Statistics;

namespace Dendro.Core.Tests;

public sealed class StratifiedSamplingStatisticsTests
{
    [Fact]
    public void WeightedMarginUsesAreaWeightsAndWithinStratumVariance()
    {
        AreaStratumSample[] strata =
        [
            new(90, [10, 20]),
            new(10, [100, 100, 100]),
        ];

        double margin = StratifiedSamplingStatistics.AreaWeightedMarginOfError(strata, .95, 3);

        double expected = InventoryReport.StudentT(.95, 1) * 4.5;
        Assert.Equal(expected, margin, 10);
        Assert.NotEqual(InventoryReport.StudentT(.95, 4) * Math.Sqrt(50 / 4d), margin, 6);
    }

    [Fact]
    public void RequiresTwoObservationsPerAreaStratumAndConfiguredMinimum()
    {
        Assert.True(double.IsNaN(StratifiedSamplingStatistics.AreaWeightedMarginOfError(
            [new(90, [10, 20]), new(10, [100])], .95, 3)));
        Assert.True(double.IsNaN(StratifiedSamplingStatistics.AreaWeightedMarginOfError(
            [new(90, [10, 20]), new(10, [100, 100])], .95, 5)));
    }

    [Fact]
    public void EqualWithinStratumValuesHaveZeroMargin()
    {
        Assert.Equal(0, StratifiedSamplingStatistics.AreaWeightedMarginOfError(
            [new(40, [10, 10]), new(60, [100, 100])], .95, 4));
    }
}
