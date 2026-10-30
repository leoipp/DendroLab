using System.Text.Json.Nodes;
using Dendro.Core;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tests;

public class FrontierTests
{
    private static JsonObject Config() => new() {
        ["x"] = "X", ["y"] = "Y", ["stratum"] = "Grupo", ["title"] = "Teste",
    };

    [Fact]
    public void ExtremesUseObservedCoordinatesAndSeparateStrata()
    {
        var data = new DataTable(["X", "Y", "Grupo"]);
        foreach (string group in new[] { "A", "B" })
            for (int i = 1; i <= 4; i++) data.Add((double)i, (double)(i * (group == "A" ? 1 : 10)), group);
        var report = FrontierAnalysis.Run(data, Config(), new(Bins: 2, MinimumPerBin: 2, Side: 3));
        Assert.Equal(8, report.Summary.N("points"));
        Assert.Equal(4, report.Plot!.Curves.Length);
        foreach (var point in report.Plot.Chart["frontier_points"]!.AsArray())
            Assert.Contains(report.Plot.Points, p => p.X == point.N("x") && p.Y == point.N("y") && p.Group == point.S("group"));
        Assert.Equal(2, report.Plot.Curves[0][0][0]);
        Assert.Equal(1, report.Plot.Curves[1][0][0]);
    }

    [Fact]
    public void PercentilesInterpolateAndTailMeansUseSelectedX()
    {
        var data = new DataTable(["X", "Y", "Grupo"]);
        for (int i = 0; i < 4; i++) data.Add((double)i, (double)(i * 10), "A");
        var quantiles = FrontierAnalysis.Run(data, Config(), new(FrontierMethod.Percentile, Bins: 1,
            MinimumPerBin: 4, Side: 3, Percentile: 75));
        Assert.Equal(22.5, quantiles.Plot!.Curves[0][0][1], 10);
        Assert.Equal(7.5, quantiles.Plot.Curves[1][0][1], 10);
        Assert.Equal(1.5, quantiles.Plot.Curves[0][0][0], 10);
        var means = FrontierAnalysis.Run(data, Config(), new(FrontierMethod.TailMean, Bins: 1,
            MinimumPerBin: 4, Side: 3, TailPercent: 50));
        Assert.Equal(25, means.Plot!.Curves[0][0][1], 10);
        Assert.Equal(2.5, means.Plot.Curves[0][0][0], 10);
        Assert.Equal(5, means.Plot.Curves[1][0][1], 10);
    }

    [Fact]
    public void EqualCountExclusionsAndFiltersAreHonoredWithoutMutatingOrigin()
    {
        var data = new DataTable(["X", "Y", "Grupo"]);
        for (int i = 0; i < 9; i++) data.Add((double)(i * i), (double)i, "A");
        data.Add(100d, 100d, "B");
        var config = Config();
        config["excluded"] = new JsonArray(9); config["group"] = "A"; config["group_selected"] = true;
        string original = config.ToJsonString();
        var report = FrontierAnalysis.Run(data, config, new(Partition: FrontierPartition.EqualCount,
            Bins: 2, MinimumPerBin: 4, ShowObservations: false));
        Assert.Equal(8, report.Summary.N("n"));
        Assert.Equal(2, report.Summary.N("points"));
        Assert.Equal(2, report.Plot!.Points.Length);
        Assert.All(report.Plot.Points, p => Assert.Equal("A", p.Group));
        Assert.Equal(original, config.ToJsonString());
    }

    [Fact]
    public void EmptyBinsAndInsufficientSamplesAreReported()
    {
        var data = new DataTable(["X", "Y", "Grupo"]);
        data.Add(1d, 1d, "A"); data.Add(2d, 2d, "A"); data.Add(100d, 5d, "A");
        var report = FrontierAnalysis.Run(data, Config(), new(Bins: 4, MinimumPerBin: 2));
        Assert.Equal(1, report.Summary.N("points"));
        Assert.Equal(3, report.Summary.N("skipped_bins"));
        Assert.Throws<InvalidDataException>(() => FrontierAnalysis.Run(data, Config(), new(Bins: 4, MinimumPerBin: 3)));
        Assert.Throws<InvalidDataException>(() => FrontierAnalysis.Run(data, Config(), new(Bins: 0)));
        Assert.Throws<InvalidDataException>(() => FrontierAnalysis.Run(data, Config(), new(Percentile: double.NaN)));
    }
}
