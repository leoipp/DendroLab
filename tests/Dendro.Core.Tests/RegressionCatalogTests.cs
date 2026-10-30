using System.Text.Json.Nodes;
using Dendro.Core;
using Dendro.Core.Configuration;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tests;

public class RegressionCatalogTests
{
    private static JsonObject Model(string id, int p, string formula, int k) =>
        new()
        {
            ["id"] = id,
            ["nome"] = id,
            ["preditores"] = p,
            ["formula"] = formula,
            ["coeficientes"] = k,
        };

    [Fact]
    public void DefaultsBelongToEachProjectAndEmptyCatalogStaysEmpty()
    {
        var a = ProjectStore.Create("A");
        var b = ProjectStore.Create("B");
        a.Obj("regressoes").Arr("modelos").Clear();
        Assert.Empty(RegressionCatalog.Families(a));
        Assert.Equal(12, RegressionCatalog.Families(b).Length);
        Assert.Empty(ProjectModels.Metadata(a)["models"]!.AsArray());
    }

    [Fact]
    public void EngineUsesOnlyProjectFamiliesAndRespectsTheirNamesAndFormulas()
    {
        var project = ProjectStore.Create("Teste");
        project.Obj("regressoes")["modelos"] = new JsonArray(
            Model("Reta local", 1, "b0+b1*x1", 2),
            Model("Dois X", 2, "b0+b1*x1+b2*x2", 3),
            Model("Três X", 3, "b0+b1*x1+b2*x2+b3*x3", 4)
        );
        var table = new DataTable(["Idade", "Altura"]);
        for (int i = 1; i <= 12; i++)
            table.Add((double)i, 2d + 3 * i);
        var result = new RegressionEngine(RegressionCatalog.Families(project)).Generate(
            table,
            new JsonObject { ["x"] = "Idade", ["y"] = "Altura" },
            -2
        );
        var fit = Assert.Single(result.Chart["fits"]!.AsArray());
        Assert.Equal("Reta local", fit.S("model"));
        Assert.Equal("Reta local", fit.S("model_id"));
        Assert.Equal(2, fit.N("b0"), 8);
        Assert.Equal(3, fit.N("b1"), 8);
        Assert.Single(RegressionCatalog.Families(project, 2));
        Assert.Single(RegressionCatalog.Families(project, 3));
    }

    [Fact]
    public void CatalogOrderCannotChangeTheMeaningOfAnEquation()
    {
        var data = new DataTable(["X", "Y"]);
        for (int i = 1; i <= 15; i++)
            data.Add((double)i, 2d + 4 * i + .5 * i * i);
        var family = new RegressionFamily("Modelo próprio", "b0+b1*x+b2*x^2", 3, "local");
        var result = new RegressionEngine([family]).Generate(
            data,
            new JsonObject { ["x"] = "X", ["y"] = "Y" },
            0
        );
        var fit = Assert.Single(result.Chart["fits"]!.AsArray());
        Assert.True(fit.B("converged"));
        Assert.Equal(.5, fit.N("b2"), 8);
    }

    [Fact]
    public void InvalidVariablesAndDuplicateIdsAreRejected()
    {
        var p = ProjectStore.Create("Teste");
        p.Obj("regressoes")["modelos"] = new JsonArray(Model("Inválido", 2, "b0+b1*x3", 2));
        Assert.Throws<InvalidDataException>(() => RegressionCatalog.Validate(p));
        p.Obj("regressoes")["modelos"] = new JsonArray(
            Model("igual", 1, "b0*x", 1),
            Model("IGUAL", 3, "b0*x1", 1)
        );
        Assert.Throws<InvalidDataException>(() => RegressionCatalog.Validate(p));
    }

    [Fact]
    public void CatalogSurvivesSavingAndLegacyMigrationWithoutChangingOriginal()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "DENDRO-catalog-" + Guid.NewGuid().ToString("N") + ".dendro"
        );
        try
        {
            var p = ProjectStore.Create("Legado");
            p.Remove("regressoes");
            File.WriteAllText(path, p.ToJsonString());
            byte[] original = File.ReadAllBytes(path);
            var loaded = ProjectStore.Read(path);
            Assert.Equal(12, RegressionCatalog.Families(loaded).Length);
            Assert.Equal(original, File.ReadAllBytes(path));
            loaded.Obj("regressoes")["modelos"] = new JsonArray(
                Model("Projeto 3P", 3, "b0+b1*x1+b2*x2+b3*x3", 4)
            );
            ProjectStore.Save(path, loaded);
            Assert.Single(RegressionCatalog.Families(ProjectStore.Read(path), 3));
            Assert.Empty(RegressionCatalog.Families(ProjectStore.Read(path), 1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
