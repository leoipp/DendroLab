using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class ProcessingPresentationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-presentation-" + Guid.NewGuid().ToString("N"));
    public ProcessingPresentationTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void HeightReaderPreservesMetricsConvertsOnlyGraphAxesAndBoundsSample()
    {
        var data = new DataTable();
        data.Add("ESTATÍSTICAS DO AJUSTE DE ALTURA (H) - RNA POR LOTE"); data.Add();
        data.Add("Métrica", "Valor", "Unidade"); data.Add("RMSE", 1.25d, "m"); data.Add("Modo", "FINETUNE_MODEL"); data.Add();
        data.Add("Lote", "Método", "N total", "N observações com predição", "N valores preenchidos", "R²", "RMSE (m)", "MAE (m)", "Bias (m)", "Resíduo médio (%)", "Desvio padrão resíduos (%)");
        data.Add("A", "BIAS", 100d, 100d, 0d, .8d, 1.25d, 1d, .1d, 2d, 3d); data.Add();
        data.Add("H_obs", "H_est", "Resíduo (%)", "Lote", "Parcela", "Método");
        for (int i = 0; i < 30000; i++) data.Add(100d, 110d, 10d, "A", "1", "BIAS");
        string path = Save("height", "H Ajuste", data);
        var report = ProcessingPresentation.Height(new(path, "H Ajuste"), sampleLimit: 50);
        Assert.Equal(30000, report.Observations); Assert.Equal(50, report.Sample.Length);
        Assert.All(report.Sample, p => { Assert.Equal(10, p.Observed); Assert.Equal(11, p.Estimated); Assert.Equal(10, p.Residual); });
        Assert.Equal(1.25, report.Metrics.Number(1, 1)); Assert.Equal(1.25, report.Lots.Number(1, 6));
        Assert.Throws<OperationCanceledException>(() => ProcessingPresentation.Height(new(path, "H Ajuste"), new CancellationToken(true)));
    }

    [Fact]
    public void InventoryReaderSeparatesLevelsAndTotalsWithoutIncludingReportTitles()
    {
        var data = new DataTable();
        data.Add("INVENTORY STATISTICS BY LOT"); data.Add("Lot", "n", "Área (ha)", "Vol/ha Med");
        data.Add("A", 4d, 10d, 120d); data.Add(); data.Add("TOTAL", 4d, 10d, 120d); data.Add();
        data.Add("INVENTORY STATISTICS BY STAND"); data.Add("Stand", "n", "Área (ha)", "Vol/ha Med");
        data.Add("T1", 4d, 10d, 120d); data.Add(); data.Add("TOTAL", 4d, 10d, 120d); data.Add("n = parcelas");
        var report = ProcessingPresentation.Inventory(new(Save("inventory", "ESTATISTICAS INV", data), "ESTATISTICAS INV"));
        Assert.Equal(2, report.Lots.Count); Assert.Equal(2, report.Stands.Count); Assert.Equal(3, report.Totals.Count);
        Assert.Equal("A", report.Lots.Text(1, 0)); Assert.Equal("T1", report.Stands.Text(1, 0)); Assert.Equal(120, report.Totals.Number(2, 3));
    }

    [Fact]
    public void BankAdjustedIncludesCalculatedColumnsWithoutEnablingDebug()
    {
        string fixtures = MigrationTests.Fixtures;
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "Reference", "rna_project.dendro")))!.AsObject();
        cfg.Obj("ajuste_altura")["debug"] = false;
        cfg.Obj("ajuste_altura").Obj("modelo_geral")["caminho"] = Path.Combine(fixtures, "rna");
        string config = Path.Combine(folder, "run.dendro"), output = Path.Combine(folder, "report.dlb");
        File.WriteAllText(config, cfg.ToJsonString());
        new InventoryProcessor().Process(new(Path.Combine(fixtures, "Reference", "rna_input.xlsx"), config,
            Path.Combine(fixtures, "Reference", "rna_registry.xlsx"), output, "Data"));
        var adjusted = TableStore.Read(new(output, "AJUSTADO"));
        Assert.True(adjusted.Column("Data de Plantio") >= 0);
        foreach (string name in cfg.At("ajuste_altura.estatisticas_parcela")!.AsArray().Select(e => e.S("nome")))
        {
            int column = adjusted.Require(name);
            Assert.Contains(Enumerable.Range(1, adjusted.Count - 1), row => double.IsFinite(adjusted.Number(row, column)));
        }
        Assert.DoesNotContain("Graficos", TableStore.Tables(output));
        var height = ProcessingPresentation.Height(new(output, "H Ajuste"));
        Assert.True(height.Observations > 0); Assert.True(height.Metrics.Count > 1);
    }

    private string Save(string name, string sheet, DataTable data)
    {
        string path = Path.Combine(folder, name + ".dlb"); int width = data.Width;
        using var writer = new CompactBankWriter(path);
        string id = writer.AddTable(Enumerable.Range(0, width).Select(c => data.Text(0, c)).ToArray(), sheet);
        for (int r = 1; r < data.Count; r++) writer.Add(id, Enumerable.Range(0, width).Select(c => data.Value(r, c)).ToArray());
        writer.Complete(); return path;
    }
}
