using System.IO.Compression;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.NeuralNetworks;
using Dendro.Core.Processing;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData(-.1)]
    [InlineData(1.1)]
    public void InvalidFactorsAreRejected(double factor)
    {
        var p = ProjectStore.Create("Teste");
        p.Obj("volume")["fator"] = factor;
        Assert.Throws<InvalidDataException>(() => ProjectStore.Validate(p));
    }

    [Theory]
    [InlineData("estatisticas.nivel_confianca", 0)]
    [InlineData("estatisticas.nivel_confianca", 1)]
    [InlineData("estatisticas.min_parcelas_estatistica", 1)]
    [InlineData("estatisticas.min_parcelas_estatistica", 2.5)]
    [InlineData("comparativo_ifc.limiares.verde", 20)]
    public void InvalidStatisticsAreRejected(string key, double n)
    {
        var p = ProjectStore.Create("Teste");
        p.Set(key, JsonValue.Create(n));
        Assert.Throws<InvalidDataException>(() => ProjectStore.Validate(p));
    }

    [Theory]
    [InlineData("../out")]
    [InlineData("arquivo:x")]
    [InlineData("a?b")]
    public void OutputSuffixCannotChangePath(string suffix)
    {
        var p = ProjectStore.Create("Teste");
        p.Obj("saida")["sufixo_arquivo"] = suffix;
        Assert.Throws<InvalidDataException>(() => ProjectStore.Validate(p));
    }

    [Theory]
    [InlineData("b0+undeclared")]
    [InlineData("__import__('os')")]
    [InlineData("b0;delete")]
    [InlineData("sin(x")]
    public void FormulaRejectsScriptsAndUnknownVariables(string formula) =>
        Assert.Throws<InvalidDataException>(() => new Formula(formula, ["b0", "x"]));

    [Theory]
    [InlineData("soma", 6)]
    [InlineData("sum", 6)]
    [InlineData("mean", 2)]
    public void AggregateExpressionsUseWholePlot(string name, double expected)
    {
        var expression = new Formula($"{name}(x)", ["x"]);
        Func<string, double>[] rows = [_ => 1, _ => 2, _ => 3];
        Assert.Equal(expected, expression.Evaluate(_ => 100, rows));
    }

    [Fact]
    public void RegistryDuplicateKeysStopJoin()
    {
        var reg = new DataTable(["ID"]);
        reg.Add("same");
        reg.Add("same");
        Assert.Throws<InvalidDataException>(() => FeaturePreparation.RegistryIndex(reg, "ID"));
    }

    [Fact]
    public void GrowthHistoryIsUserSelectedAndVersioned()
    {
        var p = ProjectStore.Create("Histórico");
        JsonObject Request(int year, bool active) =>
            new()
            {
                ["id"] = "AL1R",
                ["model"] = "gompertz",
                ["year"] = year,
                ["b0"] = 300,
                ["b1"] = 1,
                ["b2"] = .03,
                ["current"] = active,
            };
        ProjectModels.SetGrowth(p, Request(2024, true));
        ProjectModels.SetGrowth(p, Request(2026, false));
        var a = p["curvas_crescimento"]!["equacoes"]!.AsArray();
        Assert.True(a[0].B("vigente"));
        ProjectModels.Activate(p, 1);
        Assert.False(a[0].B("vigente"));
        Assert.True(a[1].B("vigente"));
        Assert.Throws<InvalidDataException>(() => ProjectModels.SetGrowth(p, Request(2024, false)));
    }

    [Fact]
    public void MissingPredictorStopsBeforeOutputIsPublished()
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "dendro-missing-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(folder);
        try
        {
            string config = Path.Combine(folder, "input.dendro"),
                output = Path.Combine(folder, "output.xlsx");
            File.Copy(
                Path.Combine(MigrationTests.Fixtures, "Reference", "predictor_run.dendro"),
                config
            );
            var error = Assert.Throws<InvalidDataException>(() =>
                new InventoryProcessor().Process(
                    new(
                        Path.Combine(MigrationTests.Fixtures, "Reference", "predictor_input.xlsx"),
                        config,
                        Path.Combine(MigrationTests.Fixtures, "cadastro.xlsx"),
                        output,
                        "Inventário"
                    )
                )
            );
            Assert.Contains("Coluna ausente", error.Message);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void RemovedPointsInvalidateFitsAndUndoRestoresSignature()
    {
        var table = new DataTable(["X", "Y"]);
        for (int i = 1; i <= 8; i++)
            table.Add((double)i, 3d + 2 * i);
        var c = new JsonObject
        {
            ["x"] = "X",
            ["y"] = "Y",
            ["parameters"] = 2,
        };
        var engine = new RegressionEngine();
        var fit = engine.Generate(table, c, 0);
        string signature = fit.Chart.S("fit_signature");
        c = fit.Chart;
        c["excluded"] = new JsonArray(1);
        var changed = engine.Generate(table, c);
        Assert.Null(changed.Chart["fits"]);
        c["excluded"] = new JsonArray();
        var restored = engine.Generate(table, c);
        Assert.Equal(signature, restored.Chart.S("fit_signature"));
        Assert.Single(restored.Chart["fits"]!.AsArray());
    }

    [Fact]
    public void WorksheetAndHeaderAreRespected()
    {
        var t = new DataTable();
        t.Add("Título");
        t.Add("DAP", "H");
        t.Add(20d, 100d);
        t.UseHeader(2);
        Assert.Equal(0, t.Require("DAP"));
        Assert.Equal(20, t.Number(1, 0));
        Assert.Throws<InvalidDataException>(() => t.UseHeader(3));
    }

    [Fact]
    public void BundleWithInternalFolderLoadsWithoutExtraction()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dendro-model-" + Guid.NewGuid().ToString("N") + ".zip"
        );
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (
                    string suffix in new[] { "_metadata.json", "_model.keras", "_scalers.pkl" }
                )
                    zip.CreateEntryFromFile(
                        Path.Combine(MigrationTests.Fixtures, "rna" + suffix),
                        "pasta do modelo/altura" + suffix
                    );
            var model = NeuralModel.Load(path);
            Assert.Equal(193, model.Predict([14], []), 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("pickle")]
    public void InvalidBundlesAreRejected(string mode)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dendro-invalid-" + Guid.NewGuid().ToString("N") + ".zip"
        );
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (
                    string suffix in new[] { "_metadata.json", "_model.keras", "_scalers.pkl" }
                )
                {
                    if (mode == "missing" && suffix == "_scalers.pkl")
                        continue;
                    if (mode == "pickle" && suffix == "_scalers.pkl")
                    {
                        using var stream = zip.CreateEntry("altura" + suffix).Open();
                        stream.Write([0x80, 4, (byte)'c']);
                    }
                    else
                        zip.CreateEntryFromFile(
                            Path.Combine(MigrationTests.Fixtures, "rna" + suffix),
                            "altura" + suffix
                        );
                }
                if (mode == "ambiguous")
                {
                    using var writer = new StreamWriter(
                        zip.CreateEntry("outro_metadata.json").Open()
                    );
                    writer.Write("{}");
                }
            }
            Assert.ThrowsAny<Exception>(() => NeuralModel.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
