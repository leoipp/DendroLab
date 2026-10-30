using Dendro.Core;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Statistics;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public class ScientificTests
{
    [Fact]
    public void PairedTMatchesClosedFormStudentProbability()
    {
        var result = HypothesisTests.Paired(SampleMoments.From([1, 2, 3, 4, 5]));
        Assert.Equal(Math.Sqrt(18), result.N("statistic"), 10);
        Assert.Equal(1 - 36 / (11 * Math.Sqrt(11)), result.N("p_value"), 10);
        Assert.Equal(4, result.N("df1"));
        Assert.True(result.N("ci_low") > 0);
    }

    [Fact]
    public void WelchAllowsUnequalSampleSizesAndDoesNotRejectEqualMeans()
    {
        var a = SampleMoments.From([1, 2, 3, 4, 5]);
        var b = SampleMoments.From([0, 2, 4, 6]);
        var result = HypothesisTests.Welch(a, b);
        Assert.Equal(0, result.N("statistic"));
        Assert.Equal(1, result.N("p_value"));
        Assert.True(result.N("df1") < 7);
        Assert.Equal(5, result.N("n_a"));
        Assert.Equal(4, result.N("n_b"));
    }

    [Fact]
    public void VarianceFMatchesNistCriticalValues()
    {
        static SampleMoments Sample(double mean, double sd) =>
            SampleMoments.From(
                Enumerable
                    .Range(0, 240)
                    .Select(i => mean + (i < 120 ? -1 : 1) * sd * Math.Sqrt(239d / 240))
            );
        var result = HypothesisTests.Variances(
            Sample(688.9987, 65.54909),
            Sample(611.1559, 61.85425)
        );
        Assert.InRange(result.N("statistic"), 1.12302, 1.12305);
        Assert.InRange(result.N("critical_low"), .7755, .7757);
        Assert.InRange(result.N("critical_high"), 1.2893, 1.2895);
        Assert.True(result.N("p_value") > .05);
    }

    [Fact]
    public void AnovaMatchesAnalyticalFProbabilityAndBrownForsytheIgnoresLocationShift()
    {
        var result = HypothesisTests.Anova([
            SampleMoments.From([1, 2, 3]),
            SampleMoments.From([2, 3, 4]),
            SampleMoments.From([8, 9, 10]),
        ]);
        Assert.Equal(43, result.N("statistic"), 10);
        Assert.Equal(Math.Pow(3d / 46, 3), result.N("p_value"), 10);
        var brown = HypothesisTests.BrownForsythe([
            [1, 2, 3],
            [11, 12, 13],
        ]);
        Assert.Equal(1, brown.N("p_value"));
    }

    [Fact]
    public void DegenerateSamplesAreReportedAsUndefinedAndInvalidAlphaIsRejected()
    {
        var constant = SampleMoments.From([2, 2, 2]);
        var result = HypothesisTests.Paired(constant);
        Assert.Null(result["p_value"]);
        Assert.Contains("indefinido", result.S("decision"));
        Assert.Throws<InvalidDataException>(() => HypothesisTests.Welch(constant, constant, 0));
        Assert.Throws<InvalidDataException>(() =>
            HypothesisTests.Variances(SampleMoments.From([1]), constant)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelIdentityDistinguishesCommonAndDifferentEquations(bool different)
    {
        var table = new DataTable(["Idade", "Volume", "Grupo"]);
        for (int i = 1; i <= 20; i++)
        {
            double noise = (i % 3 - 1) * .2;
            table.Add((double)i, 2 + 3d * i + noise, "A");
            table.Add((double)i, (different ? 15 : 2) + 3d * i + noise, "B");
        }
        var result = ModelIdentity.Test(
            table,
            "Idade",
            "Volume",
            "Grupo",
            RegressionEngine.Models[0]
        );
        Assert.Equal(2, result.Summary.N("df1"));
        Assert.Equal(36, result.Summary.N("df2"));
        Assert.Equal(different, result.Summary.N("p_value") < .05);
        Assert.Equal(3, result.Plot!.Curves.Length);
        Assert.Equal(4, result.Details.Count);
    }

    [Fact]
    public void IdentityRequiresMultipleGroupsAndEnoughObservations()
    {
        var table = new DataTable(["X", "Y", "Grupo"]);
        table.Add(1d, 1d, "A");
        table.Add(2d, 2d, "A");
        table.Add(3d, 3d, "A");
        Assert.Throws<InvalidDataException>(() =>
            ModelIdentity.Test(table, "X", "Y", "Grupo", RegressionEngine.Models[0])
        );
    }

    [Fact]
    public void NonlinearIdentityIsExplicitlyAnApproximation()
    {
        var table = new DataTable(["X", "Y", "Grupo"]);
        for (int i = 1; i <= 15; i++)
            foreach (string group in new[] { "A", "B" })
                table.Add(
                    (double)i,
                    (group == "A" ? 100 : 140) * Math.Exp(.05 * i) + .1 * (i % 3 - 1),
                    group
                );
        var result = ModelIdentity.Test(table, "X", "Y", "Grupo", RegressionEngine.Models[3]);
        Assert.Contains("aproximação", result.Summary.S("test"));
        Assert.Contains("não linear", result.Summary.S("notes"));
        Assert.True(result.Summary.N("p_value") < .05);
    }

    [Fact]
    public void IdentityRejectsUnusedCoefficientsAndReportsNumericallyZeroResidual()
    {
        var data = new DataTable(["X", "Y", "G"]);
        for (int i = 1; i <= 10; i++) foreach (string group in new[] { "A", "B" }) data.Add((double)i, 2 + 3d * i, group);
        Assert.Throws<InvalidDataException>(() => ModelIdentity.Test(data, "X", "Y", "G", new("Incorreto", "b0+b1*x", 3)));
        var result = ModelIdentity.Test(data, "X", "Y", "G", RegressionEngine.Models[0]); Assert.Null(result.Summary["p_value"]); Assert.Contains("nulo", result.Summary.S("notes"));
    }

    [Fact]
    public void GrowthCrossingIdentifiesMaximumImaAndConvertsMonths()
    {
        var annual = GrowthDiagnostics.Calculate(
            new("Anual", "exp(b0+b1/t)", [Math.Log(1000), -10]),
            1,
            20
        );
        var monthly = GrowthDiagnostics.Calculate(
            new("Mensal", "exp(b0+b1/t)", [Math.Log(1000), -120], "meses"),
            1,
            20
        );
        Assert.Equal(10, Assert.Single(annual.Crossings).Age, 5);
        Assert.Contains("Máximo", annual.Crossings[0].Kind);
        Assert.Equal(annual.MaximumIma, monthly.MaximumIma, 6);
        Assert.Equal(10, monthly.Crossings[0].Age, 5);
    }

    [Fact]
    public void MissingCrossingIsLimitedToIntervalAndExponentialCrossingIsMinimum()
    {
        var limited = GrowthDiagnostics.Calculate(
            new("Curva", "exp(b0+b1/t)", [Math.Log(1000), -10]),
            .5,
            5
        );
        Assert.Empty(limited.Crossings);
        Assert.Contains("intervalo", limited.Status);
        var exponential = GrowthDiagnostics.Calculate(
            new("Exponencial", "b0*exp(b1*t)", [100, .2]),
            1,
            15
        );
        var crossing = Assert.Single(exponential.Crossings);
        Assert.Equal(5, crossing.Age, 5);
        Assert.Contains("Mínimo", crossing.Kind);
    }

    [Fact]
    public void CoincidentAndAnnualIncrementHaveDistinctInterpretations()
    {
        var line = GrowthDiagnostics.Calculate(new("Linear", "b0*t", [10]), 1, 10);
        Assert.Empty(line.Crossings);
        Assert.Contains("coincidem", line.Status);
        var square = GrowthDiagnostics.Calculate(new("Quadrático", "b0*t^2", [1]), 1, 5, true);
        Assert.Equal(1, Assert.Single(square.Crossings).Age, 6);
        Assert.Contains("não é necessariamente", square.Status);
        Assert.Throws<InvalidDataException>(() =>
            GrowthDiagnostics.Calculate(new("Inválido", "b0*t", [1]), 0, 5)
        );
    }

    [Fact]
    public void TabularPairedTestKeepsCorrespondenceWhenCellsAreMissing()
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "DENDRO-stats-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "pares.xlsx");
        try
        {
            var table = new DataTable(["Antes", "Depois"]);
            table.Add(1d, 2d);
            table.Add(null, 7d);
            table.Add(5d, null);
            table.Add(4d, 3d);
            table.Add(7d, 4d);
            WorkbookWriter.Write(path, [new("Dados", table)]);
            var result = StatisticalAnalysis.Run(
                new(path),
                new(StatisticalTest.PairedT, "Antes", "Depois")
            );
            Assert.Equal(3, result.Summary.N("n_pairs"));
            Assert.Equal(2, result.Summary.N("omitted"));
            Assert.Equal(1, result.Summary.N("difference"));
            var bank = TableTools.Merge([new(path), new(path)], folder, "Pares", true);
            var bankResult = StatisticalAnalysis.Run(
                new(bank.Path),
                new(StatisticalTest.PairedT, "Antes", "Depois")
            );
            Assert.Equal(6, bankResult.Summary.N("n_pairs"));
            Assert.Equal(1, bankResult.Summary.N("difference"));
            File.Delete(bank.Path);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(folder);
        }
    }
}
