using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.NeuralNetworks;
using Dendro.Core.Processing;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Dendro.Core.Tests;

public class ParityTests
{
    private static string Fixtures => MigrationTests.Fixtures;

    [Theory]
    [InlineData("growth", 0)]
    [InlineData("growth", 1)]
    [InlineData("mapped", 0)]
    [InlineData("predictor", 0)]
    [InlineData("predictor", 1)]
    [InlineData("rna", 0)]
    public void FullReportsMatchC(string scenario, int variant)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dendro-parity-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            string Reference(string name) => Path.Combine(Fixtures, "Reference", name);
            string filename = scenario switch
            {
                "growth" => "growth_run.dendro",
                "mapped" => "mapped.dendro",
                "predictor" => "predictor_run.dendro",
                _ => "rna_project.dendro",
            };
            var cfg = JsonNode.Parse(File.ReadAllText(Reference(filename)))!.AsObject();
            string input = Path.Combine(Fixtures, "inventário.xlsx"),
                registry = Path.Combine(Fixtures, "cadastro.xlsx"),
                golden = "";
            switch (scenario)
            {
                case "growth":
                    ProjectModels.Activate(cfg, variant);
                    golden = variant == 0 ? "growth_report.xlsx" : "growth_report_new.xlsx";
                    break;
                case "mapped":
                    input = Reference("mapped_input.xlsx");
                    registry = Reference("mapped_registry.xlsx");
                    golden = "mapped_report.xlsx";
                    break;
                case "predictor":
                    input = Reference("predictor_input.xlsx");
                    cfg.Obj("volume")["fator"] = variant == 0 ? 1 : .5;
                    ProjectModels.Activate(cfg, 0);
                    golden = variant == 0 ? "predictor_report.xlsx" : "predictor_factor.xlsx";
                    break;
                case "rna":
                    input = Reference("rna_input.xlsx");
                    registry = Reference("rna_registry.xlsx");
                    cfg.Obj("ajuste_altura").Obj("modelo_geral")["caminho"] = Path.Combine(
                        Fixtures,
                        "rna"
                    );
                    golden = "rna_report.xlsx";
                    break;
            }
            string config = Path.Combine(root, "run.dendro"),
                output = Path.Combine(root, "out.xlsx");
            File.WriteAllText(config, cfg.ToJsonString());
            new InventoryProcessor().Process(
                new(input, config, registry, output, scenario == "rna" ? "Data" : "Inventário")
            );
            Compare(Reference(golden), output);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Compare(string expectedPath, string actualPath)
    {
        using (var document = SpreadsheetDocument.Open(actualPath, false))
        {
            var errors = new OpenXmlValidator().Validate(document).Take(10).ToArray();
            Assert.True(
                errors.Length == 0,
                string.Join("\n", errors.Select(e => e.Description + " " + e.Path?.XPath))
            );
        }
        var sheets = SpreadsheetReader.Sheets(expectedPath);
        Assert.Equal(sheets, SpreadsheetReader.Sheets(actualPath));
        foreach (string name in sheets.Where(s => s != "INFORMACOES"))
        {
            var expected = SpreadsheetReader.Read(expectedPath, name);
            var actual = SpreadsheetReader.Read(actualPath, name);
            Assert.Equal(expected.Count, actual.Count);
            bool ages = name is "RESUMO - BYPLOT" or "RESUMO - BYTALHAO";
            bool basal = expected.Column("AS") < 0 && actual.Column("AS") >= 0;
            Assert.Equal(expected.Width + (ages ? 1 : 0) + (basal ? 1 : 0), actual.Width);
            for (int r = 0; r < expected.Count; r++)
            for (int c = 0; c < expected.Width; c++)
            {
                int ac = c + (ages && c > expected.Require("Idade_anos") ? 1 : 0)
                    + (basal && c > expected.Require("DAP") ? 1 : 0);
                double e = expected.Number(r, c),
                    a = actual.Number(r, ac);
                if (double.IsFinite(e))
                    Assert.True(
                        Math.Abs(a - e) <= 1e-8 * Math.Max(1, Math.Abs(e)),
                        $"{name}!{WorkbookWriter.Reference(r, c)} {a} != {e}"
                    );
                else
                    Assert.Equal(expected.Text(r, c), actual.Text(r, ac));
            }
        }
    }

    [Theory]
    [InlineData(.95, 1, 12.7062047361747)]
    [InlineData(.95, 10, 2.22813885196494)]
    [InlineData(.90, 9, 1.83311293265363)]
    public void StudentDistributionIsAccurate(double confidence, int df, double expected) =>
        Assert.Equal(expected, InventoryReport.StudentT(confidence, df), 9);

    [Theory]
    [InlineData("gompertz", 300, 1, .03)]
    [InlineData("logistica", 200, 2, .04)]
    public void GrowthUsesDeclaredAgeUnit(string family, double b0, double b1, double b2)
    {
        var v = new JsonObject
        {
            ["modelo"] = family,
            ["b0"] = b0,
            ["b1"] = b1,
            ["b2"] = b2,
        };
        var t = new DataTable(["Unused"]);
        t.Add(0d);
        var program = new ModelProgram(v, t, t, true);
        double actual = program.Aggregate([new(t, t, 1, 1, 5, 20, 100, 100, 10)]);
        var known =
            family == "gompertz"
                ? 300 * Math.Exp(-Math.Exp(1 - .03 * 60))
                : 200 / (1 + Math.Exp(2 - .04 * 60));
        Assert.Equal(known, actual, 10);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void NonlinearFamiliesFitKnownCurves(int model)
    {
        double[] b = [2, .05, 1, 0];
        switch (model)
        {
            case 4:
                b = [3, 1.2, 0, 0];
                break;
            case 5:
                b = [2, 3, 0, 0];
                break;
            case 6:
                b = [4, -2, 0, 0];
                break;
            case 7:
                b = [50, -2, 0, 0];
                break;
            case 8:
            case 9:
                b = [100, 2, .08, 0];
                break;
            case 10:
            case 11:
                b = [100, .05, 1.4, 0];
                break;
        }
        var table = new DataTable(["X", "Y"]);
        var family = RegressionEngine.Models[model];
        var formula = new Formula(family.Formula, ["b0", "b1", "b2", "b3", "x", "t"]);
        for (int i = 1; i <= 40; i++)
        {
            int x = i;
            table.Add((double)x, formula.Evaluate(s => s is "x" or "t" ? x : b[s[1] - '0']));
        }
        var chart = new JsonObject
        {
            ["x"] = "X",
            ["y"] = "Y",
            ["parameters"] = family.Parameters,
        };
        var result = new RegressionEngine().Generate(table, chart, model);
        var fit = result.Chart["fits"]![0]!;
        Assert.True(fit.B("converged"), family.Name);
        Assert.InRange(fit.N("rmse"), 0, .005);
    }
}
