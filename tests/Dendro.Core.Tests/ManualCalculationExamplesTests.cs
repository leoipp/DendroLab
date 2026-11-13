using Dendro.Core.Mathematics;
using Dendro.Core.Processing;
using Dendro.Core.Sampling;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class ManualCalculationExamplesTests
{
    [Fact]
    public void DiameterAreaAndVolumeExamplesUseTheEnginesUnits()
    {
        Assert.Equal(0.3, InventoryMath.Volume(200, 150, Math.Log(0.00005), 2, 1), 10);
        double area = InventoryMath.Area(200, 200, 30, 0);
        Assert.Equal(346.4101615, area, 6);
        Assert.Equal(230.9401077, 8 * 10000 / area, 6);
        Assert.Equal(4d / 9, InventoryMath.Gini([0, 10, 20]), 10);
    }

    [Fact]
    public void InventoryIntervalExampleUsesStudentTAndThreePlots()
    {
        var sample = SampleMoments.From([180, 200, 220]);
        Assert.Equal(200, sample.Mean);
        Assert.Equal(400, sample.Variance);
        double margin = InventoryReport.StudentT(.95, 2) * Math.Sqrt(sample.Variance / sample.Count);
        Assert.InRange(margin, 49.68, 49.69);
        Assert.InRange(100 * margin / sample.Mean, 24.84, 24.85);
    }

    [Fact]
    public void WelchAndPairedExamplesMatchTheTestsShownInTheManual()
    {
        double a = Math.Sqrt(90);
        var first = SampleMoments.From(Enumerable.Range(0, 10).Select(i => 100 + (i < 5 ? -a : a)));
        var second = SampleMoments.From(Enumerable.Range(0, 10).Select(i => 110 + (i < 5 ? -a : a)));
        var welch = HypothesisTests.Welch(first, second);
        Assert.Equal(-Math.Sqrt(5), welch.N("statistic"), 9);
        Assert.Equal(18, welch.N("df1"), 9);
        Assert.InRange(welch.N("p_value"), .037, .039);
        Assert.Equal(Math.Sqrt(18), HypothesisTests.Paired(SampleMoments.From([1, 2, 3, 4, 5])).N("statistic"), 10);
        Assert.Equal(35, ForestSampler.PlanGrtsSampleSize(95, 10, 30));
    }

    [Fact]
    public void ValidationExampleMatchesAllReportedMetrics()
    {
        string file = Path.Combine(Path.GetTempPath(), "dendro-manual-" + Guid.NewGuid().ToString("N") + ".dlb");
        try
        {
            using (var writer = new CompactBankWriter(file))
            {
                string table = writer.AddTable(["Y", "Estimado"], "Dados");
                writer.Add(table, [100d, 110d]); writer.Add(table, [200d, 190d]); writer.Add(table, [300d, 330d]); writer.Complete();
            }
            var metrics = ModelValidation.Run(new(file, "Dados"), "Y", ["Estimado"]).Series[0].Metrics;
            Assert.Equal(10, metrics.Bias!.Value, 10);
            Assert.Equal(50d / 3, metrics.MeanAbsoluteError!.Value, 10);
            Assert.Equal(Math.Sqrt(1100d / 3), metrics.Rmse!.Value, 10);
            Assert.Equal(.945, metrics.R2!.Value, 10);
            Assert.Equal(5, metrics.MeanErrorPercent!.Value, 10);
            Assert.Equal(25d / 3, metrics.MeanAbsolutePercentError!.Value, 10);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
