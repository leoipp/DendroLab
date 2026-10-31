using System.Text.Json.Nodes;
using Dendro.Core;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.NeuralNetworks;
using Dendro.Core.Processing;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Dendro.Core.Tests;

public class MigrationTests
{
    public static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void ProjectDefaultsAreCompatible()
    {
        var p = ProjectStore.Create("Teste C#");
        ProjectStore.Validate(p, true);
        Assert.Equal(14, p["ajuste_altura"]!["features"]!.AsArray().Count);
        Assert.Equal(40, ProjectStore.Fields.Count);
    }

    [Theory]
    [InlineData(20, 10, 2)]
    [InlineData(40, 20, 8)]
    public void IndividualVolumesMatchC(double dap, double height, double expected) =>
        Assert.Equal(expected, InventoryMath.Volume(dap, height, 0, 1, 1), 10);

    [Fact]
    public void FormulaPrecedenceAndNamedColumns()
    {
        var f = new Formula("-2**2 + 3*sqrt(4)", []);
        Assert.Equal(2, f.Evaluate(_ => 0));
        Assert.Equal(
            2,
            new Formula("DAP / Média de DAP", ["DAP", "Média de DAP"]).Evaluate(s =>
                s == "DAP" ? 20 : 10
            )
        );
        Assert.Throws<InvalidDataException>(() => new Formula("__import__('os')", []));
    }

    [Fact]
    public void LegacyExcelAndWorksheetSelection()
    {
        var path = Path.Combine(Fixtures, "inventario_legacy.XLS");
        Assert.Contains("Dados", SpreadsheetReader.Sheets(path));
        var table = SpreadsheetReader.Read(path, "Dados");
        Assert.Equal(4, table.Count);
        Assert.Equal(20, table.Number(1, table.Require("DAP")));
    }

    [Fact]
    public void DenseAndScalerPredictionMatchesC()
    {
        var m = NeuralModel.Load(Path.Combine(Fixtures, "rna"));
        Assert.Equal(193, m.Predict([14], []), 5);
        Assert.Equal(178, m.Predict([8], []), 5);
        Assert.True(double.IsNaN(m.Predict([double.NaN], [])));
    }

    [Fact]
    public void UserZipPredictionMatchesC()
    {
        string path = Environment.GetEnvironmentVariable("DENDRO_REFERENCE_ZIP") ?? "";
        if (path.Length == 0)
            return;
        var m = NeuralModel.Load(path);
        double actual = m.Predict(
            [150, 100, 150, 200, 200, 250, 300, 60, .001, 1, .75],
            ["AL", "Alto fuste", "N"]
        );
        Assert.Equal(247.638275, actual, 3);
    }

    [Fact]
    public void ReportValuesAndStructureMatchLegacy()
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "dendro-tests-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(folder);
        try
        {
            var p = ProjectStore.Create("Paridade");
            // The new analysis tables supplement the legacy report; compare its original tabs unchanged.
            p.Obj("saida").Obj("sheets")["resultado_arvore"] = false;
            p.Obj("saida").Obj("sheets")["resumo_bylote"] = false;
            p.Obj("volume")
                .Arr("equacoes")
                .Add(
                    new JsonObject
                    {
                        ["codigo"] = "AL1R",
                        ["b0"] = 0,
                        ["b1"] = 1,
                        ["b2"] = 1,
                    }
                );
            string config = Path.Combine(folder, "teste.dendro"),
                output = Path.Combine(folder, "relatorio.xlsx");
            ProjectStore.Save(config, p);
            var result = new InventoryProcessor().Process(
                new(
                    Path.Combine(Fixtures, "inventário.xlsx"),
                    config,
                    Path.Combine(Fixtures, "cadastro.xlsx"),
                    output,
                    "Inventário"
                )
            );
            Assert.Equal(11, result.Volume, 10);
            Assert.Equal(3, result.Trees);
            Assert.Equal(2, result.Plots);
            Assert.Equal(1, result.Stands);
            using (var doc = SpreadsheetDocument.Open(output, false))
            {
                var errors = new OpenXmlValidator().Validate(doc).Take(10).ToArray();
                Assert.True(
                    errors.Length == 0,
                    string.Join("\n", errors.Select(e => e.Description + " " + e.Path?.XPath))
                );
            }
            var actualSheets = SpreadsheetReader.Sheets(output);
            var expectedSheets = SpreadsheetReader.Sheets(Path.Combine(Fixtures, "resultado.xlsx"));
            Assert.Equal(expectedSheets, actualSheets);
            foreach (string sheet in actualSheets.Where(s => s != "INFORMACOES"))
            {
                var expected = SpreadsheetReader.Read(
                    Path.Combine(Fixtures, "resultado.xlsx"),
                    sheet
                );
                var actual = SpreadsheetReader.Read(output, sheet);
                Assert.Equal(expected.Count, actual.Count);
                bool ages = sheet is "RESUMO - BYPLOT" or "RESUMO - BYTALHAO";
                bool expectedDominant = expected.Rows.SelectMany(row => row).Any(cell => cell?.ToString() is "DAPd" or "DAPd Med");
                bool actualDominant = actual.Rows.SelectMany(row => row).Any(cell => cell?.ToString() is "DAPd" or "DAPd Med");
                bool dominant = !expectedDominant && actualDominant;
                int dominantAfter = -1;
                if (dominant)
                {
                    var header = expected.Rows.FirstOrDefault(row => row.Any(cell => cell?.ToString() == "H Med"))
                        ?? expected.Rows.First(row => row.Any(cell => cell?.ToString() == "H"));
                    dominantAfter = header.FindIndex(cell => cell?.ToString() == (header.Any(cell => cell?.ToString() == "H Med") ? "H Med" : "H"));
                }
                Assert.Equal(expected.Width + (ages ? 1 : 0) + (dominant ? 2 : 0), actual.Width);
                for (int r = 0; r < expected.Count; r++)
                for (int c = 0; c < expected.Width; c++)
                {
                    int ac = c + (ages && c > expected.Require("Idade_anos") ? 1 : 0)
                        + (dominant && c > dominantAfter ? 2 : 0);
                    double a = actual.Number(r, ac),
                        e = expected.Number(r, c);
                    if (double.IsFinite(e))
                        Assert.True(
                            Math.Abs(a - e) <= (sheet == "ESTATISTICAS INV" ? 0.0005000001 : 1e-9 * Math.Max(1, Math.Abs(e))),
                            $"{sheet}!{WorkbookWriter.Reference(r, c)}: {a} != {e}"
                        );
                    else
                        Assert.Equal(expected.Text(r, c), actual.Text(r, ac));
                }
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AnalysisTablesRespectOutputConfiguration()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dendro-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = ProjectStore.Create("Abas de análise");
            project.Obj("volume").Arr("equacoes").Add(new JsonObject
            {
                ["codigo"] = "AL1R", ["b0"] = 0, ["b1"] = 1, ["b2"] = 1,
            });
            string config = Path.Combine(folder, "project.dendro"), output = Path.Combine(folder, "results.xlsx");
            ProjectStore.Save(config, project);
            new InventoryProcessor().Process(new(Path.Combine(Fixtures, "inventário.xlsx"), config,
                Path.Combine(Fixtures, "cadastro.xlsx"), output, "Inventário"));
            var trees = SpreadsheetReader.Read(output, "RESULTADO - ARVORES");
            var lots = SpreadsheetReader.Read(output, "RESUMO - BYLOTE");
            Assert.Equal(4, trees.Count);
            Assert.Equal(11, Enumerable.Range(1, trees.Count - 1).Sum(r => trees.Number(r, trees.Column("VTCC"))), 9);
            Assert.Equal("Lot", lots.Text(0, 0));
            Assert.Equal(2, lots.Count); // One lot, with no title, blank rows or aggregate footer.
            Assert.Equal(2, lots.Number(1, lots.Column("n")));
            project.Obj("saida").Obj("sheets")["resultado_arvore"] = false;
            project.Obj("saida").Obj("sheets")["resumo_bylote"] = false;
            ProjectStore.Save(config, project);
            output = Path.Combine(folder, "disabled.xlsx");
            new InventoryProcessor().Process(new(Path.Combine(Fixtures, "inventário.xlsx"), config,
                Path.Combine(Fixtures, "cadastro.xlsx"), output, "Inventário"));
            Assert.DoesNotContain("RESULTADO - ARVORES", SpreadsheetReader.Sheets(output));
            Assert.DoesNotContain("RESUMO - BYLOTE", SpreadsheetReader.Sheets(output));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void RegressionAndFrontiersAreStratified()
    {
        var table = new DataTable(["X", "Y", "Grupo"]);
        foreach (string group in new[] { "A", "B" })
            for (int x = 1; x <= 12; x++)
                table.Add((double)x, group == "A" ? 5d + 2 * x : 3d + 4 * x, group);
        var config = new JsonObject
        {
            ["path"] = "fixture.xlsx",
            ["x"] = "X",
            ["y"] = "Y",
            ["stratum"] = "Grupo",
            ["parameters"] = 2,
            ["frontier"] = 0,
        };
        var engine = new RegressionEngine();
        var result = engine.Generate(table, config, 0);
        Assert.Equal(2, result.Chart["fits"]!.AsArray().Count);
        var a = result.Chart["fits"]!.AsArray().Single(f => f.S("group") == "A");
        Assert.Equal(5, a.N("b0"), 10);
        Assert.Equal(2, a.N("b1"), 10);
        config["frontier"] = 3;
        config["bins"] = 4;
        result = engine.Generate(table, config, 0);
        Assert.Equal(16, result.Chart["frontier_points"]!.AsArray().Count);
        Assert.Equal(4, result.Chart["fits"]!.AsArray().Count);
    }
}
