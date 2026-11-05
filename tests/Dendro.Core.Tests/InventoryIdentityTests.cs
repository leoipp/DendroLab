using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class InventoryIdentityTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-identities-" + Guid.NewGuid().ToString("N"));
    public InventoryIdentityTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private static DataTable Input()
    {
        var input = new DataTable(["Talhão", "Parcela", "Lote", "DAP", "H", "Data", "Compr", "Larg", "Dec_Compr", "Dec_Larg", "Cova", "Fuste", "Fila", "Fonte", "Tipo", "ID árvore", "ID parcela"]);
        input.Add("T1", "1", "XXXXAL00000P", 20d, 10d, new DateTime(2025, 1, 1).ToOADate(), 100d, 100d, 0d, 0d, 1d, 1d, 1d, "Base IFC", "IFC", "A", "P1");
        input.Add("T1", "1", "XXXXAL00000P", 40d, 20d, new DateTime(2025, 1, 1).ToOADate(), 100d, 100d, 0d, 0d, 1d, 1d, 1d, "Base IFPC", "IFPC", "B", "P2");
        return input;
    }
    private static JsonObject Config()
    {
        var cfg = ProjectStore.Create("Misto"); cfg.Obj("processador")["inventory_type"] = "MISTO";
        cfg.Obj("identificacao")["coluna_tipo_inventario"] = "Tipo";
        cfg.Obj("cadastro")["worksheet"] = "Dados";
        cfg.Obj("volume").Arr("equacoes").Add(new JsonObject { ["codigo"] = "AL1R", ["b0"] = 0, ["b1"] = 1, ["b2"] = 1 });
        cfg.Obj("ajuste_altura")["variaveis_customizadas"] = new JsonArray();
        cfg.Obj("ajuste_altura")["estatisticas_parcela"] = new JsonArray(); return cfg;
    }
    private string Save(string name, DataTable table)
    {
        string path = Path.Combine(folder, name + ".dlb"); using var writer = new CompactBankWriter(path);
        int width = table.Width; string id = writer.AddTable(Enumerable.Range(0, width).Select(c => table.Text(0, c)).ToArray(), "Dados");
        foreach (var row in table.Rows.Skip(1)) writer.Add(id, row.ToArray()); writer.Complete(); return path;
    }
    private (string Path, JsonObject Run) Process(JsonObject cfg, DataTable input, string name = "original")
    {
        var registry = new DataTable(["Talhão", "Data de Plantio", "Área efetiva (ha)"]); registry.Add("T1", new DateTime(2015, 1, 1).ToOADate(), 10d);
        var run = ProcessingCatalog.NewRecord("input", "MISTO", "talhao");
        cfg.Obj("processador")["execucao"] = run.DeepClone();
        string config = Path.Combine(folder, name + ".dendro"), output = Path.Combine(folder, name + ".dlb");
        File.WriteAllText(config, cfg.ToJsonString());
        new InventoryProcessor().Process(new(Save(name + "-input", input), config, Save(name + "-registry", registry), output, "Dados"));
        return (output, run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteAndDirectNeverMixIfcAndIfpcWithIdenticalStandPlotAndTree(bool direct)
    {
        var cfg = Config(); if (direct) cfg.Obj("processador")["modo"] = "resumo_direto";
        var (path, run) = Process(cfg, Input());
        var plots = TableStore.Read(new(path, direct ? "DIRETO - PARCELAS" : ProcessingHistory.Plots));
        Assert.Equal(3, plots.Count);
        Assert.Equal(new[] { "IFC", "IFPC" }, Enumerable.Range(1, 2).Select(r => plots.Text(r, plots.Require("Tipo_Inventario"))).Order());
        Assert.Equal(2, Enumerable.Range(1, 2).Select(r => plots.Text(r, plots.Require("Parcela_ID"))).Distinct().Count());
        if (direct)
        {
            Assert.Equal(3, TableStore.Read(new(path, "DIRETO - TALHOES")).Count);
            Assert.Equal(3, TableStore.Read(new(path, "DIRETO - PROJETO")).Count);
            return;
        }
        var trees = TableStore.Read(new(path, ProcessingHistory.Trees));
        Assert.Equal(2, Enumerable.Range(1, 2).Select(r => trees.Text(r, trees.Require("Arvore_ID"))).Distinct().Count());
        var stats = ProcessingPresentation.Inventory(new(path, "ESTATISTICAS INV"));
        Assert.Equal(3, stats.Lots.Count); Assert.Equal(3, stats.Stands.Count); Assert.Equal(5, stats.Totals.Count);
        Assert.Equal(3, TableStore.Read(new(path, "RESUMO - BYLOTE")).Count);
        var factors = ProcessingHistory.FactorTemplate(path, run, "talhao");
        Assert.Equal(3, factors.Count);
        for (int row = 1; row < factors.Count; row++) factors.Set(row, 3, factors.Text(row, factors.Require("Tipo_Inventario")) == "IFC" ? .5 : .25);
        var revision = ProcessingCatalog.NewRecord("input", "MISTO", "talhao", run, 2);
        string corrected = Path.Combine(folder, "corrected.dlb"); ProcessingHistory.Correct(path, run, revision, factors, corrected);
        var revisedPlots = TableStore.Read(new(corrected, ProcessingHistory.Plots));
        Assert.Equal(3, revisedPlots.Count);
        var volumes = Enumerable.Range(1, 2).Select(r => revisedPlots.Number(r, revisedPlots.Require("VTCC"))).Order().ToArray();
        Assert.Equal(1d, volumes[0], 10); Assert.Equal(2d, volumes[1], 10);
    }

    [Fact]
    public void CustomKeysReplaceDefaultTreeFieldsAndControlPlotGrouping()
    {
        var cfg = Config(); var input = Input();
        cfg.Obj("identificacao")["arvore"] = new JsonArray("ID árvore");
        cfg.Obj("identificacao")["parcela"] = new JsonArray("ID parcela");
        input.Set(2, input.Require("Fonte"), "Base IFC"); input.Set(2, input.Require("Tipo"), "IFC");
        foreach (string column in new[] { "Cova", "Fuste", "Fila" })
        {
            int index = input.Require(column); foreach (var row in input.Rows) row.RemoveAt(index);
        }
        var (path, _) = Process(cfg, input);
        Assert.Equal(3, TableStore.Read(new(path, ProcessingHistory.Plots)).Count);
        Assert.Equal(3, TableStore.Read(new(path, ProcessingHistory.Trees)).Count);
    }

    [Fact]
    public void DirectDeduplicationPrecedesPlotStatisticsAndPreservesSourceAndOtherContexts()
    {
        var cfg = Config(); cfg.Obj("processador")["modo"] = "resumo_direto";
        cfg.Obj("processador")["remover_duplicadas"] = true;
        cfg.Obj("ajuste_altura").Arr("estatisticas_parcela").Add(new JsonObject { ["nome"] = "DAPmed", ["coluna"] = "DAP", ["funcao"] = "mean" });
        var input = Input();
        input.Add(input.Rows[1].ToArray());
        input.Set(3, input.Require("Cova"), 2d); input.Set(3, input.Require("DAP"), 60d); input.Set(3, input.Require("H"), 30d);
        var unique = input.Copy();
        for (int i = 0; i < 4; i++) input.Add(input.Rows[1].ToArray());
        var (path, _) = Process(cfg, input);
        var plots = TableStore.Read(new(path, "DIRETO - PARCELAS"));
        Assert.Equal(3, plots.Count);
        var baseline = Process(ConfigDirect(), unique, "baseline");
        var expected = TableStore.Read(new(baseline.Path, "DIRETO - PARCELAS"));
        foreach (string column in new[] { "Volume_m3_ha", "Fustes_ha", "DAP_medio" })
            for (int row = 1; row < plots.Count; row++)
                Assert.Equal(expected.Number(row, expected.Require(column)), plots.Number(row, plots.Require(column)), 10);
        var info = TableStore.Read(new(path, "INFORMACOES"));
        Assert.Contains(info.Rows.Skip(1), row => row[0]?.ToString()?.StartsWith("Linhas duplicadas removidas") == true && Convert.ToDouble(row[1]) == 4);
        Assert.Equal(8, TableStore.Read(new(Path.Combine(folder, "original-input.dlb"), "Dados")).Count);
        static JsonObject ConfigDirect() { var c = Config(); c.Obj("processador")["modo"] = "resumo_direto"; return c; }
    }

    [Fact]
    public void DirectProcessingAfterDeletingSourceRowsStillDeduplicatesAndMatchesSingleTreeVolume()
    {
        var cfg = Config();
        cfg.Obj("processador")["modo"] = "resumo_direto";
        cfg.Obj("processador")["remover_duplicadas"] = true;
        cfg.Obj("processador").Obj("saidas")["arvore"] = true;
        var input = Input();
        input.Add(input.Rows[1].ToArray()); // duplicate IFC tree
        input.Add(input.Rows[2].ToArray()); // duplicate IFPC tree
        string editedSource = Save("edited-source", input);
        var source = new TableSource(editedSource, "Dados");
        var deletion = TableRowEditor.Delete(source, TableRowEditor.Version(source), [3, 5]);
        Assert.Equal(2, deletion.Removed);
        Assert.Equal(2, deletion.Remaining);

        var registry = new DataTable(["Talhão", "Data de Plantio", "Área efetiva (ha)"]);
        registry.Add("T1", new DateTime(2015, 1, 1).ToOADate(), 10d);
        string config = Path.Combine(folder, "edited-direct-config.dendro");
        File.WriteAllText(config, cfg.ToJsonString());
        string registryPath = Save("edited-registry", registry);
        string output = Path.Combine(folder, "edited-direct-output.dlb");
        new InventoryProcessor().Process(new(editedSource, config, registryPath, output, "Dados"));

        var trees = TableStore.Read(new(output, "DIRETO - ARVORES"));
        Assert.Equal(2, trees.Count); // one remaining IFC tree plus header
        Assert.Equal("IFC", trees.Text(1, trees.Require("Tipo_Inventario")));
        var info = TableStore.Read(new(output, "INFORMACOES"));
        Assert.Contains(info.Rows.Skip(1), row =>
            row[0]?.ToString()?.StartsWith("Linhas duplicadas removidas") == true
            && Convert.ToDouble(row[1]) == 1
        );

        var expectedInput = new DataTable(input.Rows[0].Select(cell => cell?.ToString() ?? "").ToArray());
        expectedInput.Add(input.Rows[1].ToArray());
        cfg.Obj("processador")["remover_duplicadas"] = false;
        var expected = Process(cfg, expectedInput, "edited-direct-expected");
        var actualPlots = TableStore.Read(new(output, "DIRETO - PARCELAS"));
        var expectedPlots = TableStore.Read(new(expected.Path, "DIRETO - PARCELAS"));
        Assert.Equal(expectedPlots.Number(1, expectedPlots.Require("Volume_m3_ha")),
            actualPlots.Number(1, actualPlots.Require("Volume_m3_ha")), 10);
    }

    [Fact]
    public void DuplicateRemovalRejectsConflictingPlotAreaInsteadOfInflatingPerHectareVolume()
    {
        var cfg = Config();
        cfg.Obj("processador")["modo"] = "resumo_direto";
        cfg.Obj("processador")["remover_duplicadas"] = true;
        var input = Input();
        input.Add(input.Rows[1].ToArray());
        input.Set(3, input.Require("Compr"), 2d);
        string config = Path.Combine(folder, "conflicting-duplicate-config.dlb");
        File.WriteAllText(config, cfg.ToJsonString());
        string source = Save("conflicting-duplicate-input", input);
        var registryTable = new DataTable(["Talhão", "Data de Plantio", "Área efetiva (ha)"]);
        registryTable.Add("T1", new DateTime(2015, 1, 1).ToOADate(), 10d);
        string registry = Save("conflicting-duplicate-registry", registryTable);
        var error = Assert.Throws<InvalidDataException>(() => new InventoryProcessor().Process(new(
            source, config, registry, Path.Combine(folder, "conflicting-duplicate-output.dlb"), "Dados")));
        Assert.Contains("dados conflitantes", error.Message);
        Assert.Contains("área da parcela diferente", error.Message);
        Assert.Contains("linhas 2 e 4", error.Message);
        Assert.Contains("área 100 m²", error.Message);
        Assert.Contains("área 2 m²", error.Message);
    }

    [Fact]
    public void DirectSummaryRejectsDifferentMeasurementsCollapsedByConfiguredPlotKey()
    {
        var cfg = Config();
        cfg.Obj("processador")["modo"] = "resumo_direto";
        string output = Path.Combine(folder, "mixed-measurements.dlb");
        using var summary = new DirectInventorySummary(output, cfg);
        summary.Add("T1", "1", "L1", 2024, 2, 10, 1, true, 100, 10, "", "",
            plotId: "same-configured-key", measurement: "2024-01-01");
        var error = Assert.Throws<InvalidDataException>(() => summary.Add(
            "T1", "1", "L1", 2025, 3, 11, 1, true, 100, 10, "", "",
            plotId: "same-configured-key", measurement: "2025-01-01"));
        Assert.Contains("medições diferentes", error.Message);
        Assert.Contains("Configurações > Identificação", error.Message);
    }

    [Fact]
    public void DirectSummaryExplainsWhenConfiguredPlotKeyOmitsLot()
    {
        var cfg = Config();
        cfg.Obj("processador")["modo"] = "resumo_direto";
        string output = Path.Combine(folder, "mixed-lots.dlb");
        using var summary = new DirectInventorySummary(output, cfg);
        summary.Add("T1", "1", "Lote A", 2024, 2, 10, 1, true, 100, 10, "", "",
            plotId: "same-configured-key", measurement: "2024-01-01");
        var error = Assert.Throws<InvalidDataException>(() => summary.Add(
            "T1", "1", "Lote B", 2024, 3, 11, 1, true, 100, 10, "", "",
            plotId: "same-configured-key", measurement: "2024-01-01"));
        Assert.Contains("lotes diferentes", error.Message);
        Assert.Contains("Lote A e Lote B", error.Message);
        Assert.Contains("Inclua a coluna Lote", error.Message);
    }

    [Fact]
    public void DirectSummaryKeepsSamePlotNumberSeparateWhenConfiguredKeyIncludesLot()
    {
        var cfg = Config();
        cfg.Obj("processador")["modo"] = "resumo_direto";
        string output = Path.Combine(folder, "separate-lots.dlb");
        using var summary = new DirectInventorySummary(output, cfg);
        summary.Add("T1", "1", "Lote A", 2024, 2, 10, 1, true, 100, 10, "", "",
            plotId: "key-lot-a", measurement: "2024-01-01");
        summary.Add("T1", "1", "Lote B", 2024, 3, 11, 1, true, 100, 10, "", "",
            plotId: "key-lot-b", measurement: "2024-01-01");
        Assert.Equal(2, summary.Complete().Plots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlotKeyCannotMergeDifferentPlotNumbers(bool direct)
    {
        var cfg = Config(); if (direct) cfg.Obj("processador")["modo"] = "resumo_direto";
        cfg.Obj("identificacao")["parcela"] = new JsonArray("talhao", "data_medicao");
        var input = Input();
        input.Set(2, input.Require("Fonte"), "Base IFC"); input.Set(2, input.Require("Tipo"), "IFC");
        input.Set(2, input.Require("Parcela"), "2");
        var error = Assert.Throws<InvalidDataException>(() => Process(cfg, input));
        Assert.Contains("números de parcela diferentes", error.Message);
    }

    [Fact]
    public void PlotStatisticsUseConfiguredKeysAndTypeColumnBeforeGlobalSelection()
    {
        var cfg = Config(); cfg.Obj("processador")["inventory_type"] = "IFPC";
        cfg.Obj("ajuste_altura").Arr("estatisticas_parcela").Add(new JsonObject { ["nome"] = "DAPmed", ["coluna"] = "DAP", ["funcao"] = "mean" });
        var input = Input(); var registry = new DataTable(["Talhão", "Data de Plantio"]); registry.Add("T1", new DateTime(2015, 1, 1).ToOADate());
        FeaturePreparation.Prepare(input, registry, cfg);
        Assert.Equal(20, input.Number(1, input.Require("DAPmed"))); Assert.Equal(40, input.Number(2, input.Require("DAPmed")));
        var plan = new InventoryIdentity(input, cfg);
        Assert.Equal("IFC", plan.Context(1).Type); Assert.Equal("IFPC", plan.Context(2).Type);
    }
}
