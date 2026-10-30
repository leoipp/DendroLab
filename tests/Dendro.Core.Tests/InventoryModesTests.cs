using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class InventoryModesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-modes-" + Guid.NewGuid().ToString("N"));
    public InventoryModesTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static JsonObject Config()
    {
        var cfg = ProjectStore.Create("Teste");
        cfg.Obj("cadastro").Obj("fallback_idade")["ativo"] = true;
        return cfg;
    }

    [Theory]
    [InlineData("00", 2000)]
    [InlineData("01", 2001)]
    [InlineData("10", 2010)]
    [InlineData("30", 2030)]
    [InlineData("31", 1931)]
    [InlineData("88", 1988)]
    [InlineData("89", 1989)]
    [InlineData("99", 1999)]
    public void TwoDigitYearsRespectCenturyCutoff(string suffix, int year)
    {
        var settings = Config()["cadastro"]!["fallback_idade"]!;
        Assert.Equal(new DateTime(year, 6, 1), RegistryAgeFallback.InferDate("GNVIVI00518P" + suffix + "-003", settings));
        settings["limite_ano_2000"] = 50;
        Assert.Equal(new DateTime(2050, 6, 1), RegistryAgeFallback.InferDate("P50", settings));
        Assert.Throws<InvalidDataException>(() => RegistryAgeFallback.InferDate("sem-ano", settings));
    }

    [Fact]
    public void LetterSuffixUsesPlantingYearAndUpgradesOnlyPreviousDefault()
    {
        var cfg = Config();
        var settings = cfg.Obj("cadastro").Obj("fallback_idade");
        foreach (string code in new[] { "GNVIVI00532R10-31S", "GNVIVI00532R10-003", "GNVIVI00532R10" })
            Assert.Equal(new DateTime(2010, 6, 1), RegistryAgeFallback.InferDate(code, settings));
        settings["padrao_ano"] = RegistryAgeFallback.LegacyDefaultPattern;
        Assert.Equal(new DateTime(2010, 6, 1), RegistryAgeFallback.InferDate("GNVIVI00532R10-31S", settings));
        string path = Path.Combine(folder, "legacy.dendro");
        ProjectStore.Save(path, cfg);
        Assert.Equal(RegistryAgeFallback.DefaultPattern, ProjectStore.Read(path).At("cadastro.fallback_idade").S("padrao_ano"));
        settings["padrao_ano"] = "^plantio-(?<ano>\\d{4})$";
        ProjectStore.Save(path, cfg);
        Assert.Equal("^plantio-(?<ano>\\d{4})$", ProjectStore.Read(path).At("cadastro.fallback_idade").S("padrao_ano"));
        Assert.Equal(new DateTime(1999, 6, 1), RegistryAgeFallback.InferDate("plantio-1999", settings));
        Assert.Throws<InvalidDataException>(() => RegistryAgeFallback.InferDate("GNVIVI00532R10-31S", settings));
    }

    [Fact]
    public void SpacedCodeInferencePreservesOriginalKeysAndRejectsCodesWithoutYear()
    {
        var cfg = Config();
        const string code = "RDIPMA00437P07 -719";
        var input = new DataTable(["Talhão", "Data"]); input.Add(code, new DateTime(2025, 1, 1).ToOADate());
        var registry = new DataTable(["Talhão", "Data de Plantio"]);
        Assert.Equal(1, RegistryAgeFallback.Apply(input, registry, cfg));
        Assert.Equal(code, input.Text(1, 0)); Assert.Equal(code, registry.Text(1, 0));
        Assert.Equal("01/06/2007", input.Text(1, input.Require(RegistryAgeFallback.DateColumn)));
        Assert.Throws<InvalidDataException>(() => RegistryAgeFallback.InferDate("RDBOBA00432CON - 232", cfg.At("cadastro.fallback_idade")!));
    }

    [Theory]
    [InlineData("RDIPMA00437P07 -719", 2007)]
    [InlineData("RDIPMA00437P13 -018", 2013)]
    [InlineData("RDIPMA00437P13 -023", 2013)]
    [InlineData("RDIPMA00437P14 -020", 2014)]
    [InlineData("RDIPMA00437R13 -019", 2013)]
    [InlineData("  RDIPMA00437P07  -  719  ", 2007)]
    [InlineData("RDIPMA00437P07\u00a0-\u00a0719", 2007)]
    [InlineData("RDIPMA00437R13 - 019S", 2013)]
    public void DefaultYearAcceptsWhitespaceAroundSeparator(string code, int year)
        => Assert.Equal(new DateTime(year, 6, 1), RegistryAgeFallback.InferDate(code, Config().At("cadastro.fallback_idade")!));

    [Fact]
    public void RealCodesResolveWithoutRegexAndAllMissingYearsAreReportedTogether()
    {
        var cfg = Config(); var settings = cfg.At("cadastro.fallback_idade")!;
        Assert.Equal(new DateTime(2016, 6, 1), RegistryAgeFallback.InferDate("RDBOBA00413P16-147", settings));
        Assert.Throws<InvalidDataException>(() => RegistryAgeFallback.InferDate("RDBOBA00432CON-232", settings));
        var input = new DataTable(["Talhão", "Data"]);
        double measured = new DateTime(2025, 1, 1).ToOADate();
        input.Add("RDBOBA00432CON-232", measured); input.Add("RDBOBA00432CON-232", measured);
        input.Add("0", measured); input.Add("RDBOBA00413P16-147", measured);
        var registry = new DataTable(["Talhão", "Data de Plantio"]);
        var error = Assert.Throws<InvalidDataException>(() => RegistryAgeFallback.Apply(input, registry, cfg));
        var issues = Assert.IsType<DataTable>(error.Data[RegistryAgeFallback.DiagnosticsKey]);
        Assert.Equal(3, issues.Count);
        Assert.Equal("RDBOBA00432CON-232", issues.Text(1, 2)); Assert.Equal(2, issues.Number(1, 3));
        Assert.Equal("0", issues.Text(2, 2)); Assert.Equal(1, issues.Number(2, 3));
        Assert.Equal(1, registry.Count); Assert.Equal(-1, input.Column(RegistryAgeFallback.FlagColumn));
        registry.Add("RDBOBA00432CON-232", new DateTime(2010, 1, 1).ToOADate());
        input.Rows.RemoveAt(3);
        Assert.Equal(1, RegistryAgeFallback.Apply(input, registry, cfg));
        Assert.Equal(3, registry.Count); // CON is valid when its planting date exists in the registry.
    }

    [Fact]
    public void RepeatedHistoricalCodesAreResolvedOnceAndCustomPatternsRemainSupported()
    {
        var cfg = Config();
        var input = new DataTable(["Talhão", "Data"]);
        double measured = new DateTime(2025, 1, 1).ToOADate();
        for (int i = 0; i < 150000; i++) input.Add("RDBOBA00413P16-147", measured);
        var registry = new DataTable(["Talhão", "Data de Plantio"]);
        Assert.Equal(1, RegistryAgeFallback.Apply(input, registry, cfg));
        Assert.Equal(2, registry.Count);
        Assert.Equal("01/06/2016", input.Text(input.Count - 1, input.Require(RegistryAgeFallback.DateColumn)));
        cfg.Obj("cadastro").Obj("fallback_idade")["padrao_ano"] = "(?<=P)(?<ano>\\d{2})(?=-)";
        Assert.Equal(new DateTime(2016, 6, 1), RegistryAgeFallback.InferDate("RDBOBA00413P16-147", cfg.At("cadastro.fallback_idade")!));
    }

    [Fact]
    public void FallbackKeepsKnownDatesAndFeedsNeuralPreparationWithoutInventingArea()
    {
        var cfg = Config();
        cfg.Obj("ajuste_altura")["estatisticas_parcela"] = new JsonArray();
        cfg.Obj("ajuste_altura")["variaveis_customizadas"] = new JsonArray();
        var input = new DataTable(["Talhão", "Parcela", "Data"]);
        double measured = new DateTime(2024, 6, 1).ToOADate(), known = new DateTime(2005, 6, 1).ToOADate();
        input.Add("P99", "1", measured); input.Add("P10", "1", measured); input.Add("P10", "2", measured);
        string planted = cfg.At("cadastro.colunas").S("data_plantio");
        var registry = new DataTable(["Talhão", planted, "Área Efetiva"]);
        registry.Add("P99", known, 10d);
        FeaturePreparation.Prepare(input, registry, cfg);
        Assert.Equal(known, input.Number(1, input.Require(planted)));
        Assert.Equal(new DateTime(2010, 6, 1).ToOADate(), input.Number(2, input.Require(planted)));
        Assert.Equal("", input.Text(1, input.Require(RegistryAgeFallback.FlagColumn)));
        Assert.Equal("Sim", input.Text(2, input.Require(RegistryAgeFallback.FlagColumn)));
        Assert.Equal("01/06/2010", input.Text(3, input.Require(RegistryAgeFallback.DateColumn)));
        Assert.True(double.IsFinite(input.Number(1, input.Require("Idade_anos"))));
        Assert.Equal(14, input.Number(2, input.Require("Idade_anos")), 2);
        Assert.True(double.IsNaN(registry.Number(2, 2)));
        Assert.Equal(3, registry.Count);
        Assert.Equal(0, RegistryAgeFallback.Apply(input, registry, cfg));
    }

    [Fact]
    public void FuturePlantingAndInvalidRegexFailClearlyAndDisabledFallbackLeavesMissingKey()
    {
        var cfg = Config();
        string planted = cfg.At("cadastro.colunas").S("data_plantio");
        var input = new DataTable(["Talhão", "Data"]); input.Add("P30", new DateTime(2024, 1, 1).ToOADate());
        var registry = new DataTable(["Talhão", planted]);
        Assert.Contains("posterior", Assert.Throws<InvalidDataException>(() => RegistryAgeFallback.Apply(input, registry, cfg)).Message);
        cfg.Obj("cadastro").Obj("fallback_idade")["padrao_ano"] = "[";
        Assert.Throws<InvalidDataException>(() => ProjectStore.Validate(cfg));
        cfg = Config(); cfg.Obj("cadastro").Obj("fallback_idade")["ativo"] = false;
        Assert.Throws<InvalidDataException>(() => FeaturePreparation.Join(input, new DataTable(["Talhão", planted]), cfg));
    }

    [Fact]
    public void DirectModeMatchesCompleteTreeVolumesAndPersistsOptions()
    {
        var cfg = Config();
        cfg.Obj("volume").Arr("equacoes").Add(new JsonObject { ["codigo"] = "AL1R", ["b0"] = 0, ["b1"] = 1, ["b2"] = 1 });
        string config = Path.Combine(folder, "run.dendro");
        string fixtures = MigrationTests.Fixtures;
        string Run(string name) {
            ProjectStore.Save(config, cfg);
            string output = Path.Combine(folder, name + ".dlb");
            var result = new InventoryProcessor().Process(new(Path.Combine(fixtures, "inventário.xlsx"), config, Path.Combine(fixtures, "cadastro.xlsx"), output, "Inventário"));
            Assert.Equal(11, result.Volume, 10); Assert.Equal(3, result.Trees); Assert.Equal(2, result.Plots);
            return output;
        }
        string full = Run("full");
        cfg.Obj("processador")["modo"] = "resumo_direto";
        cfg.Obj("processador").Obj("saidas")["arvore"] = true;
        string direct = Run("direct");
        var trees = TableStore.Read(new(direct, "DIRETO - ARVORES"));
        Assert.Equal(11, Enumerable.Range(1, 3).Sum(r => trees.Number(r, trees.Require("Volume_m3"))), 10);
        for(int row=1;row<trees.Count;row++)
            Assert.Equal(Math.PI*Math.Pow(trees.Number(row,trees.Require("DAP"))/10,2)/40000,trees.Number(row,trees.Require("AS")),12);
        var directPlots=TableStore.Read(new(direct,"DIRETO - PARCELAS"));
        for(int row=1;row<directPlots.Count;row++)
        {
            string id=directPlots.Text(row,directPlots.Require("Parcela_ID"));
            double sum=Enumerable.Range(1,trees.Count-1).Where(r=>trees.Text(r,trees.Require("Parcela_ID"))==id).Sum(r=>trees.Number(r,trees.Require("AS")));
            Assert.Equal(sum,directPlots.Number(row,directPlots.Require("AB")),12);
            Assert.True(directPlots.Number(row,directPlots.Require("area_corr"))>0);
            Assert.NotEmpty(directPlots.Text(row,directPlots.Require("Data_Medicao")));
        }
        Assert.Contains("RESULTADO - ARVORES", TableStore.Tables(full));
        Assert.Equal(6, TableStore.Tables(direct).Length);
        Assert.DoesNotContain("CALCULOS", TableStore.Tables(direct));
        foreach (var (path, prefix, suffixes) in new[] {
            (full, "RESUMO - ", new[] { "BYPLOT", "BYTALHAO" }),
            (direct, "DIRETO - ", new[] { "PARCELAS", "TALHOES" }) })
            foreach (string suffix in suffixes) {
                var ages = TableStore.Read(new(path, prefix + suffix));
                for (int r = 1; r < ages.Count; r++) {
                    Assert.True(ages.Number(r, ages.Require("Idade_anos")) > 0);
                    Assert.Equal(ages.Number(r, ages.Require("Idade_anos")) * 12, ages.Number(r, ages.Require("Idade_meses")), 10);
                }
            }
        var reloaded = ProjectStore.Read(config);
        Assert.Equal("resumo_direto", reloaded["processador"].S("modo"));
        Assert.True(reloaded.At("cadastro.fallback_idade").B("ativo"));
    }

    [Fact]
    public void MissingRegistryKeyProcessesWithAuditWithoutChangingRegistryFile()
    {
        var cfg = Config();
        cfg.Obj("volume")["idade_limite_equacao_geral"] = 100;
        cfg.Obj("volume").Arr("equacoes").Add(new JsonObject { ["codigo"] = "AL1R", ["b0"] = 0, ["b1"] = 1, ["b2"] = 1 });
        var input = SpreadsheetReader.Read(Path.Combine(MigrationTests.Fixtures, "inventário.xlsx"), "Inventário");
        for (int r = 1; r < input.Count; r++) input.Set(r, input.Require("Talhão"), "GNVIVI00532R00-31S");
        string source = Path.Combine(folder, "input.xlsx"), config = Path.Combine(folder, "run.dendro");
        WorkbookWriter.Write(source, [new("Inventário", input)]);
        string registry = Path.Combine(MigrationTests.Fixtures, "cadastro.xlsx");
        byte[] before = File.ReadAllBytes(registry);
        foreach (string mode in new[] { "completo", "resumo_direto" }) {
            cfg.Obj("processador")["modo"] = mode;
            cfg.Obj("processador").Obj("saidas")["arvore"] = true;
            ProjectStore.Save(config, cfg);
            string output = Path.Combine(folder, mode + ".dlb");
            var result = new InventoryProcessor().Process(new(source, config, registry, output, "Inventário"));
            Assert.Equal(11, result.Volume, 10);
            var trees = TableStore.Read(new(output, mode == "completo" ? "RESULTADO - ARVORES" : "DIRETO - ARVORES"));
            Assert.Equal("Sim", trees.Text(1, trees.Require(RegistryAgeFallback.FlagColumn)));
            Assert.Equal("01/06/2000", trees.Text(1, trees.Require(RegistryAgeFallback.DateColumn)));
        }
        Assert.Equal(before, File.ReadAllBytes(registry));
    }

    [Fact]
    public void DirectTotalsWeightStandsByAreaAndSeparateYearsAndSelectedLevels()
    {
        var cfg = Config(); cfg.Obj("processador")["modo"] = "resumo_direto";
        foreach (string level in DirectInventorySummary.Levels) cfg.Obj("processador").Obj("saidas")[level] = level == "projeto";
        string path = Path.Combine(folder, "weighted.dlb");
        using (var summary = new DirectInventorySummary(path, cfg)) {
            summary.Add("A", "1", "L", 2024, 2, 10, 2, true, 100, 10, "", "");
            summary.Add("B", "1", "L", 2024, 3, 10, 4, true, 100, 30, "", "");
            summary.Add("A", "1", "L", 2025, 4, 11, 3, true, 100, double.NaN, "Sim", "01/06/2010");
            summary.Complete();
        }
        Assert.Equal(new[] { "DIRETO - PROJETO", "INFORMACOES" }, TableStore.Tables(path));
        var table = TableStore.Read(new(path, "DIRETO - PROJETO"));
        Assert.Equal(3, table.Count);
        Assert.Equal(6, table.Number(1, table.Require("Volume_amostrado_m3")));
        Assert.Equal(350, table.Number(1, table.Require("Volume_m3_ha")));
        Assert.Equal(14000, table.Number(1, table.Require("Volume_total_m3")));
        Assert.Equal(3.5, table.Number(1, table.Require("VMI_m3_fuste")));
        Assert.True(double.IsNaN(table.Number(2, table.Require("Volume_total_m3"))));
        Assert.True(double.IsNaN(table.Number(2, table.Require("Volume_m3_ha"))));
        cfg.Obj("processador").Obj("saidas")["projeto"] = false;
        Assert.Throws<InvalidDataException>(() => DirectInventorySummary.Validate(cfg["processador"]));
    }
}
