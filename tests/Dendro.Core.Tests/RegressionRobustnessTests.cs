using System.Text.Json.Nodes;
using Dendro.Core;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tests;

public sealed class RegressionRobustnessTests
{
    [Fact]
    public void NestedMultivariateChapmanRichardsUsesWarmStartAndImprovesFit()
    {
        const string baseId = "cr-1p";
        const string expandedId = "cr-site-rate";
        var baseModel = new RegressionFamily("Chapman-Richards", "b0*(1-exp(-b1*x1))^b2", 3, baseId, 1);
        var expandedModel = new RegressionFamily("Chapman-Richards with site", "b0*(1-exp(-b1*exp(b3*x2)*x1))^b2", 4, expandedId, 2);
        var data = new DataTable(["Age", "Site", "Volume"]);
        for (int i = 0; i < 140; i++)
        {
            double age = 18 + i * .95;
            double site = 12 + (i * 29 % 49);
            double growthRate = .019 * Math.Exp(.006 * site);
            double volume = 500 * Math.Pow(1 - Math.Exp(-growthRate * age), 1.9)
                + Math.Sin(i * 1.71) * 18 + Math.Cos(i * .43) * 8;
            data.Add(age, site, volume);
        }
        var chart = new JsonObject
        {
            ["x"] = "Age",
            ["x_variables"] = new JsonArray("Age", "Site"),
            ["y"] = "Volume",
            ["fit_predictors"] = new JsonArray(1, 2),
        };

        var result = new RegressionEngine([expandedModel, baseModel]).Generate(data.Rows[0].Select(v => v?.ToString() ?? "").ToArray(), data.Rows.Skip(1), chart, -2);
        var fits = result.Chart["fits"]!.AsArray().OfType<JsonObject>().ToDictionary(f => f.S("model_id"));
        var simple = fits[baseId];
        var expanded = fits[expandedId];

        Assert.True(expanded.B("converged"));
        Assert.True(expanded.N("r2") > simple.N("r2") + .01,
            $"Expanded R² {expanded.N("r2"):G8} should improve on nested model R² {simple.N("r2"):G8}.");
        Assert.Equal("CONVERGENTE", expanded.S("diagnostic_status"));
        Assert.True(expanded.N("starts_attempted") > 5);
        Assert.True(expanded.N("function_evaluations") > 0);
        Assert.True(expanded.N("objective_evaluations") > 0);
        Assert.NotNull(expanded["initial_parameters"]);
        Assert.False(string.IsNullOrWhiteSpace(expanded.S("termination_reason")));

        var singleChart = new JsonObject
        {
            ["x"] = "Age",
            ["x_variables"] = new JsonArray("Age", "Site"),
            ["y"] = "Volume",
        };
        var singleResult = new RegressionEngine([expandedModel], [baseModel, expandedModel])
            .Generate(data.Rows[0].Select(v => v?.ToString() ?? "").ToArray(), data.Rows.Skip(1), singleChart, 0);
        var singleFit = Assert.Single(singleResult.Chart["fits"]!.AsArray().OfType<JsonObject>());
        Assert.True(singleFit.N("r2") > simple.N("r2") + .01);
    }

    [Fact]
    public void ConstantPredictionsAreFlaggedAsDegenerateEvenWhenSolverStops()
    {
        var model = new RegressionFamily("Saturating custom", "b0*(1-exp(-b1*exp(b3*x2)*x1))^b2", 4, "saturated", 2);
        var data = new DataTable(["Age", "Site", "Volume"]);
        for (int i = 0; i < 40; i++)
            data.Add(20 + i * .01, 10 + i, 80 + i * 2.3);
        var chart = new JsonObject
        {
            ["x"] = "Age",
            ["x_variables"] = new JsonArray("Age", "Site"),
            ["y"] = "Volume",
        };

        var result = new RegressionEngine([model]).Generate(data.Rows[0].Select(v => v?.ToString() ?? "").ToArray(), data.Rows.Skip(1), chart, -2);
        var fit = Assert.Single(result.Chart["fits"]!.AsArray().OfType<JsonObject>());

        Assert.True(fit.B("converged"));
        Assert.Equal("SOLUÇÃO DEGENERADA", fit.S("diagnostic_status"));
        Assert.Contains("praticamente constantes", fit["diagnostics"]!.AsArray().Select(n => n!.GetValue<string>()).Single());
    }

    [Fact]
    public void ExpandedFitNeverLosesItsKnownNestedCandidate()
    {
        var baseModel = new RegressionFamily("Chapman-Richards", "b0*(1-exp(-b1*x1))^b2", 3, "base", 1);
        var expandedModel = new RegressionFamily("Chapman-Richards with site", "b0*(1-exp(-b1*exp(b3*x2)*x1))^b2", 4, "expanded", 2);
        var data = new DataTable(["Age", "Site", "Volume"]);
        for (int i = 0; i < 90; i++)
        {
            double age = 17 + i * 1.1;
            double site = 12 + (i * 23 % 49);
            double volume = 470 * Math.Pow(1 - Math.Exp(-.017 * age), 1.7)
                + Math.Sin(i * 1.31) * 10;
            data.Add(age, site, volume);
        }
        var chart = new JsonObject
        {
            ["x"] = "Age",
            ["x_variables"] = new JsonArray("Age", "Site"),
            ["y"] = "Volume",
            ["fit_predictors"] = new JsonArray(1, 2),
        };

        var result = new RegressionEngine([baseModel, expandedModel])
            .Generate(data.Rows[0].Select(v => v?.ToString() ?? "").ToArray(), data.Rows.Skip(1), chart, -2);
        var fits = result.Chart["fits"]!.AsArray().OfType<JsonObject>().ToDictionary(f => f.S("model_id"));

        Assert.True(fits["expanded"].B("converged"));
        Assert.True(fits["expanded"].N("objective") <= fits["base"].N("objective") + 1e-8);
        Assert.True(fits["expanded"].N("r2") >= fits["base"].N("r2") - 1e-10);
    }
}
