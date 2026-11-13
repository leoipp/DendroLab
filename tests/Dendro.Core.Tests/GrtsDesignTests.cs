using System.Text.Json;
using Dendro.Core.Sampling;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace Dendro.Core.Tests;

public sealed class GrtsDesignTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-grts-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private static ForestSamplingOptions Basic => new()
    {
        IdField = "ID", EdgeBufferMeters = 0, Method = ForestSamplingMethod.Grts,
        GrtsStratumField = "*", GrtsSampleSizeMode = GrtsSampleSizeMode.FixedPerStratum, GrtsFixedSampleSize = 12
    };

    public GrtsDesignTests()
    {
        Directory.CreateDirectory(folder); path = Path.Combine(folder, "stands.shp");
        var factory = new GeometryFactory();
        var features = Enumerable.Range(0, 2).Select(index =>
        {
            double x = 500000 + index * 500;
            var polygon = factory.CreatePolygon(new[] { new Coordinate(x, 7500000), new Coordinate(x + 400, 7500000),
                new Coordinate(x + 400, 7500400), new Coordinate(x, 7500400), new Coordinate(x, 7500000) });
            return new Feature(polygon, new AttributesTable
            {
                { "ID", "T" + index }, { "GRUPO", "L" + index }, { "CAT", index == 0 ? "A" : "B" },
                { "AUX", index == 0 ? 1d : 9d }
            });
        }).ToArray();
        Shapefile.WriteAllFeatures(features, path);
        File.WriteAllText(Path.ChangeExtension(path, ".prj"), "PROJCS[\"Metric test frame\",UNIT[\"metre\",1]]");
    }

    [Fact]
    public void EqualAreaDesignHasFixedSizeReproduciblePointsAndAreaWeights()
    {
        var first = ForestSampler.Run(path, Basic);
        var repeat = ForestSampler.Run(path, Basic);
        Assert.Equal(12, first.Samples.Count);
        Assert.Equal(12, first.RequiredPlots);
        Assert.Equal(first.Samples.Select(sample => (sample.X, sample.Y)), repeat.Samples.Select(sample => (sample.X, sample.Y)));
        Assert.All(first.Samples, sample => { Assert.Equal("Todos", sample.Stratum); Assert.True(sample.ProbabilityIsExact); Assert.Equal(32d / 12, sample.AreaWeightHa, 8); });
        Assert.Equal(32, first.Samples.Sum(sample => sample.AreaWeightHa), 8);
        Assert.Equal(first.Samples.Count, first.Samples.Select(sample => (sample.X, sample.Y)).Distinct().Count());
        Assert.False(first.Samples.Select(sample => (sample.X, sample.Y)).SequenceEqual(ForestSampler.Run(path, Basic with { Seed = 99 }).Samples.Select(sample => (sample.X, sample.Y))));
    }

    [Fact]
    public void UserFieldAndExplicitStratumQuantitiesAreApplied()
    {
        var result = ForestSampler.Run(path, Basic with
        {
            GrtsStratumField = "GRUPO", GrtsSampleSizeMode = GrtsSampleSizeMode.PerStratum,
            GrtsStratumSampleSizes = new() { ["L0"] = 3, ["L1"] = 7 }
        });
        Assert.Equal(3, result.Samples.Count(sample => sample.Stratum == "L0"));
        Assert.Equal(7, result.Samples.Count(sample => sample.Stratum == "L1"));
        Assert.Equal(2, result.GrtsDesign!.Count);
    }

    [Fact]
    public void ProportionalProbabilitiesRespectPositiveAuxiliaryValues()
    {
        var options = Basic with { GrtsFixedSampleSize = 20, GrtsSelectionType = GrtsSelectionType.Proportional, GrtsAuxiliaryField = "AUX" };
        var result = ForestSampler.Run(path, options);
        Assert.Equal(20, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(8d / Convert.ToDouble(sample.Attributes["AUX"]), sample.AreaWeightHa, 7));
        double averageHigh = Enumerable.Range(0, 30).Average(seed => ForestSampler.Run(path, options with { Seed = seed }).Samples.Count(sample => sample.StandId == "T1"));
        Assert.InRange(averageHigh, 16.5, 19.5);
        Assert.Throws<ArgumentException>(() => ForestSampler.Run(path, options with { GrtsAuxiliaryField = "ID" }));
    }

    [Fact]
    public void CategoryExpectedCountsControlProbabilitiesAndMustSumToBaseSize()
    {
        var options = Basic with
        {
            GrtsFixedSampleSize = 10, GrtsSelectionType = GrtsSelectionType.Categories, GrtsCategoryField = "CAT",
            GrtsCategorySampleSizes = new() { ["Todos"] = new() { ["A"] = 2, ["B"] = 8 } }
        };
        var result = ForestSampler.Run(path, options);
        Assert.Equal(10, result.Samples.Count);
        Assert.All(result.Samples, sample => Assert.Equal(sample.StandId == "T0" ? 8 : 2, sample.AreaWeightHa, 7));
        Assert.Throws<ArgumentException>(() => ForestSampler.Run(path, options with { GrtsFixedSampleSize = 11 }));
    }

    [Fact]
    public void ImpossibleDistanceReportsFailureWithoutDroppingBaseOrReplacementSites()
    {
        var result = ForestSampler.Run(path, Basic with { GrtsFixedSampleSize = 8, GrtsReplacementCount = 2, GrtsMinimumDistance = 10000, GrtsMaxAttempts = 3 });
        Assert.Equal(8, result.Samples.Count(sample => sample.SiteUse == "Base"));
        Assert.Equal(2, result.Samples.Count(sample => sample.SiteUse == "Reserva"));
        Assert.Equal(new[] { 1, 2 }, result.Samples.Where(sample => sample.SiteUse == "Reserva").Select(sample => sample.ReplacementOrder).Order());
        Assert.All(result.Samples, sample => Assert.False(sample.ProbabilityIsExact));
        Assert.False(result.GrtsDesign![0].DistanceSatisfied);
        Assert.Equal(3, result.GrtsDesign[0].Attempts);
        Assert.Contains("não atingida", result.Warning);
        Assert.Equal(10, result.Samples.Select(sample => (sample.X, sample.Y)).Distinct().Count());
    }

    [Fact]
    public void DensityAndPrecisionPlanningAreConfigurable()
    {
        var sparse = ForestSampler.Run(path, Basic with { GrtsPointDensity = 2 });
        var dense = ForestSampler.Run(path, Basic with { GrtsPointDensity = 40 });
        Assert.True(dense.GrtsDesign![0].FrameSize > sparse.GrtsDesign![0].FrameSize);
        Assert.Equal(35, ForestSampler.PlanGrtsSampleSize(95, 10, 30));
        var precision = ForestSampler.Run(path, Basic with { GrtsSampleSizeMode = GrtsSampleSizeMode.Precision });
        Assert.Equal(35, precision.Samples.Count);
        Assert.Throws<ArgumentException>(() => ForestSampler.PlanGrtsSampleSize(100, 10, 30));
        Assert.Throws<ArgumentException>(() => ForestSampler.Run(path, Basic with { GrtsPointDensity = 0 }));
    }

    [Fact]
    public void ExportRetainsSiteRoleAndDesignParameters()
    {
        var result = ForestSampler.Run(path, Basic with { GrtsReplacementCount = 2 });
        string output = Path.Combine(folder, "samples.shp"), design = Path.Combine(folder, "samples.design.json");
        ForestSampler.WriteShapefile(result, output); ForestSampler.WriteDesign(result, design);
        var rows = ForestSampler.ReadAttributeRecords(output);
        Assert.Equal(2, rows.Count(row => row["TIPO"]?.ToString() == "Reserva"));
        Assert.All(rows, row => Assert.True(row.ContainsKey("PI_EXATA") && row.ContainsKey("PESO_HA") && row.ContainsKey("ESTRATO")));
        using var document = JsonDocument.Parse(File.ReadAllText(design));
        Assert.Equal(2, document.RootElement.GetProperty("Options").GetProperty("GrtsReplacementCount").GetInt32());
        Assert.Equal(12, document.RootElement.GetProperty("RequiredBasePlots").GetInt32());
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
