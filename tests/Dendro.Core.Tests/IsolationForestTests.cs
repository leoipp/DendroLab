using System.Text.Json;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Statistics;

namespace Dendro.Core.Tests;

public class IsolationForestTests
{
    private static DataTable Data()
    {
        var random = new Random(7); var data = new DataTable(["DAP", "H"]);
        for (int i = 0; i < 300; i++) data.Add(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
        data.Add(100d, 100d); return data;
    }

    [Fact]
    public void IsolatesDistantPointAndIsDeterministicWithSavedSeed()
    {
        var data = Data(); var options = new IsolationForestOptions(128, 128);
        var model = IsolationForest.Fit(data, ["DAP", "H"], "Teste", options);
        var again = IsolationForest.Fit(data, ["DAP", "H"], "Teste", options);
        Assert.Equal(JsonSerializer.Serialize(model), JsonSerializer.Serialize(again));
        double outlier = IsolationForest.Score(model, [100, 100]);
        Assert.True(outlier > .7);
        Assert.True(outlier > IsolationForest.Score(model, [0, 0]));
        Assert.True(outlier > model.Threshold);
        IsolationForest.ValidateModel(model);
        var report = IsolationForest.Report(data, model, "DAP", "H");
        Assert.Equal("Anomalia", report.Details.Text(301, report.Details.Require("IF_Classificacao")));
        Assert.Equal(301, report.Plot!.Points.Length);
    }

    [Fact]
    public void ModelRoundTripPreservesScoresAndThresholdForNewTables()
    {
        var model = IsolationForest.Fit(Data(), ["DAP", "H"], "Persistido", new(32, 64, FixedThreshold: .6));
        var restored = JsonSerializer.Deserialize<IsolationForestModel>(JsonSerializer.Serialize(model))!;
        IsolationForest.ValidateModel(restored);
        Assert.Equal(IsolationForest.Score(model, [1, 2]), IsolationForest.Score(restored, [1, 2]));
        var newData = new DataTable(["H", "Outra", "DAP"]); newData.Add(100d, "id", 100d);
        var report = IsolationForest.Report(newData, restored, "$row", "$score");
        Assert.Equal(.6, report.Summary["Limiar salvo"]!.GetValue<double>());
        Assert.Equal(IsolationForest.Score(model, [100, 100]), report.Details.Number(1, report.Details.Require("IF_Score")));
    }

    [Fact]
    public void KeepsIncompleteRowsWithoutClassifyingThemAsNormal()
    {
        var data = Data(); data.Add(null, 1d);
        var model = IsolationForest.Fit(data, ["DAP", "H"], "Sem ausentes", new(16, 64, TrainingLimit: 100));
        Assert.Equal(301, model.EligibleRows); Assert.Equal(100, model.TrainingRows);
        var report = IsolationForest.Report(data, model, "$row", "$score");
        Assert.Equal(data.Count, report.Details.Count);
        Assert.Equal("Incompleta", report.Details.Text(302, report.Details.Require("IF_Classificacao")));
        Assert.Null(report.Details.Value(302, report.Details.Require("IF_Score")));
        Assert.Equal(1, report.Summary["Linhas sem escore"]!.GetValue<int>());
    }

    [Fact]
    public void FixedThresholdOneFlagsNothingAndSamplingCapsAtAvailableRows()
    {
        var data = new DataTable(["X"]); data.Add(0d); data.Add(1d); data.Add(100d);
        var model = IsolationForest.Fit(data, ["X"], "Pequeno", new(10, 256, FixedThreshold: 1));
        Assert.Equal(3, model.EffectiveSampleSize);
        Assert.All(model.Trees, tree => Assert.Equal(3, tree[0].Size));
        var report = IsolationForest.Report(data, model, "$row", "$score");
        Assert.DoesNotContain(report.Plot!.Points, p => p.Group == "Anomalia");
        Assert.Equal(1, IsolationForest.ExpectedPath(2)); Assert.Equal(0, IsolationForest.ExpectedPath(1));
    }

    [Fact]
    public void RejectsConstantVariablesInvalidParametersAndCyclicModels()
    {
        var constants = new DataTable(["X"]); constants.Add(1d); constants.Add(1d);
        Assert.Throws<InvalidDataException>(() => IsolationForest.Fit(constants, ["X"], "Constante", new()));
        Assert.Throws<InvalidDataException>(() => IsolationForest.Fit(Data(), ["DAP"], "Inválido", new(500, 4096)));
        var model = IsolationForest.Fit(Data(), ["DAP"], "Válido", new(2, 16));
        model.Trees[0][0] = model.Trees[0][0] with { Left = 0 };
        Assert.Throws<InvalidDataException>(() => IsolationForest.ValidateModel(model));
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => IsolationForest.Fit(Data(), ["DAP"], "Cancelado", new(), cancelled.Token));
    }

    [Fact]
    public void AnalysisPlotSnapshotsKeepIcaImaLinesAndAnomalyGroups()
    {
        var report = GrowthDiagnostics.Report([new("Curva", "b0*t", [2d])], 1, 10, false);
        var plot = JsonSerializer.Deserialize<Dendro.Core.Regression.ChartResult>(JsonSerializer.Serialize(report.Plot))!;
        Assert.Equal(2, plot.Curves.Length);
        Assert.Equal(report.Plot!.Points.Length, plot.Points.Length);
        Assert.Equal("ICA e IMA", plot.Chart["title"]!.GetValue<string>());
        var model = IsolationForest.Fit(Data(), ["DAP", "H"], "Anomalias", new(16, 32));
        var anomalies = IsolationForest.Report(Data(), model, "DAP", "H");
        var roundTrip = JsonSerializer.Deserialize<Dendro.Core.Regression.ChartResult>(JsonSerializer.Serialize(anomalies.Plot))!;
        Assert.Equal("#C33E5E", roundTrip.Chart["group_colors"]!["Anomalia"]!.GetValue<string>());
    }
}
