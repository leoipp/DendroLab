using Dendro.Core.Spreadsheets;
using Dendro.Core.Statistics;

namespace Dendro.Core.Tests;

public class GrowthComparisonTests
{
    private static GrowthCurve Curve(double slope, string unit = "anos") => new("Curva " + slope, "b0*t", [slope], unit);

    [Fact]
    public void ComparesSameAgesConvertingMonthsToYears()
    {
        var report = GrowthComparison.Run(Curve(1, "meses"), Curve(12), new(1, 10, 5, 0, 1e-8));
        Assert.Contains("compatíveis", report.Summary["decision"]!.GetValue<string>());
        Assert.Equal(60d, report.Summary["Produção vigente na referência"]!.GetValue<double>());
        Assert.Equal(60d, report.Summary["Produção nova na referência"]!.GetValue<double>());
        Assert.Equal(202, report.Details.Count);
        Assert.Equal(402, report.Plot!.Points.Length);
        Assert.False(report.Summary.ContainsKey("Inferência de desempenho"));
    }

    [Fact]
    public void EvaluatesBothFixedCurvesOnSameRowsAndGroupsRepeatedMeasurements()
    {
        var data = new DataTable(["Idade", "Produção", "Talhão", "Ano"]);
        for (int unit = 1; unit <= 6; unit++)
        {
            data.Add((double)unit, 12d * unit, "T" + unit, 2025d);
            data.Add(unit + .5, 12 * (unit + .5), "T" + unit, 2026d);
        }
        var options = new GrowthComparisonOptions(1, 10, 5, AgeColumn: "Idade", ObservedColumn: "Produção",
            UnitColumns: ["Talhão"], YearColumn: "Ano", HistoricalThroughYear: 2025, IndependentValidation: true);
        var report = GrowthComparison.Run(Curve(10), Curve(12), options, data);
        Assert.Equal(12, report.Summary["Base comum · N"]!.GetValue<int>());
        Assert.Equal(6, report.Summary["Histórico · N"]!.GetValue<int>());
        Assert.Equal(6, report.Summary["Novos dados · N"]!.GetValue<int>());
        Assert.Equal(6L, report.Summary["Unidades independentes"]!.GetValue<long>());
        Assert.Equal(0d, report.Summary["Base comum · RMSE nova"]!.GetValue<double>());
        Assert.True(report.Summary["IC 95% da redução por unidade · inferior"]!.GetValue<double>() > 0);
        var descriptive = GrowthComparison.Run(Curve(10), Curve(12), options with { IndependentValidation = false }, data);
        Assert.False(descriptive.Summary.ContainsKey("IC 95% da redução por unidade · inferior"));
    }

    [Fact]
    public void OmitsIncompletePairsAndRowsOutsideCommonAgeDomain()
    {
        var data = new DataTable(["Idade", "Produção"]);
        data.Add(2d, 24d); data.Add(20d, 240d); data.Add(3d, null);
        var report = GrowthComparison.Run(Curve(10), Curve(12), new(1, 10, 5, AgeColumn: "Idade", ObservedColumn: "Produção"), data);
        Assert.Equal(1, report.Summary["Base comum · N"]!.GetValue<int>());
        Assert.Equal(1, report.Summary["Linhas inválidas omitidas"]!.GetValue<int>());
        Assert.Equal(1, report.Summary["Linhas fora da faixa omitidas"]!.GetValue<int>());
    }

    [Fact]
    public void RejectsInvalidDomainAndKeepsZeroBaselinePercentUndefined()
    {
        Assert.Throws<InvalidDataException>(() => GrowthComparison.Run(Curve(10), Curve(12), new(1, 5, 7)));
        Assert.Throws<InvalidDataException>(() => GrowthComparison.Run(Curve(-1), Curve(12), new(1, 10, 5)));
        var report = GrowthComparison.Run(Curve(0), Curve(12), new(1, 10, 5));
        Assert.Null(report.Summary["Maior diferença percentual absoluta na grade"]);
        Assert.Equal(201, report.Summary["Idades com percentual indefinido (vigente zero)"]!.GetValue<int>());
    }
}
