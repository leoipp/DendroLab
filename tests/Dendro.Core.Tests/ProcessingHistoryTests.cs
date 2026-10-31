using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public class ProcessingHistoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-history-" + Guid.NewGuid().ToString("N"));
    public ProcessingHistoryTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private (string Path, JsonObject Run) Original(bool duplicate = false, bool removeDuplicates = false)
    {
        var config = ProjectStore.Create("Histórico");
        config.Obj("volume").Arr("equacoes").Add(new JsonObject { ["codigo"] = "AL1R", ["b0"] = 0, ["b1"] = 1, ["b2"] = 1 });
        var data = TableStore.Read(new(Path.Combine(MigrationTests.Fixtures, "inventário.xlsx"), "Inventário"));
        int cova = data.Ensure("Cova"), fuste = data.Ensure("Fuste"), fila = data.Ensure("Fila");
        for (int row = 1; row < data.Count; row++) { data.Set(row, cova, (double)row); data.Set(row, fuste, 1d); data.Set(row, fila, 1d); }
        if (duplicate)
        {
            data.Add(data.Rows[1].ToArray());
            data.Set(data.Count - 1, data.Require("DAP"), 999d); // Same identity with conflicting measurements: keep the first.
        }
        string input = Path.Combine(folder, "input.dlb");
        using (var writer = new CompactBankWriter(input))
        {
            string id = writer.AddTable(Enumerable.Range(0, data.Width).Select(c => data.Text(0, c)).ToArray(), "Inventário");
            foreach (var row in data.Rows.Skip(1)) writer.Add(id, row.ToArray()); writer.Complete();
        }
        var record = ProcessingCatalog.NewRecord("input.dlb", "IFPC", "talhao");
        config.Obj("processador")["execucao"] = record.DeepClone();
        config.Obj("processador")["remover_duplicadas"] = removeDuplicates;
        config.Obj("volume")["fator"] = .3; // The original must still be uncorrected.
        string configFile = Path.Combine(folder, "run.dendro"), output = Path.Combine(folder, "original.dlb");
        ProjectStore.Save(configFile, config);
        new InventoryProcessor().Process(new(input, configFile, Path.Combine(MigrationTests.Fixtures, "cadastro.xlsx"), output, "Inventário"));
        return (output, record);
    }

    [Fact]
    public void DuplicatesRequireOptInAndAreRemovedBeforeTotalsWithAudit()
    {
        using var rejected = new ProcessingHistoryTests();
        var error = Assert.Throws<InvalidDataException>(() => rejected.Original(duplicate: true));
        Assert.Contains("Remover árvores duplicadas", error.Message);
        var (path, _) = Original(duplicate: true, removeDuplicates: true);
        var trees = TableStore.Read(new(path, ProcessingHistory.Trees));
        Assert.Equal(11d, Enumerable.Range(1, trees.Count - 1).Sum(r => trees.Number(r, trees.Require("VTCC"))), 10);
        var input = TableStore.Read(new(path, ProcessingHistory.InternalPrefix + "INPUT"));
        Assert.Equal(trees.Count, input.Count);
        Assert.DoesNotContain(Enumerable.Range(1, input.Count - 1), r => input.Number(r, input.Require("DAP")) == 999);
        var info = TableStore.Read(new(path, "PROCESSAMENTO"));
        Assert.Contains(info.Rows, row => row[0]?.ToString() == "removed_duplicates" && row[1]?.ToString() == "1");
        var source = TableStore.Read(new(Path.Combine(folder, "input.dlb"), "Inventário"));
        Assert.Equal(input.Count + 1, source.Count); // The source is untouched.
    }

    [Fact]
    public void LargeReportWritesAllRowsWithProgressAndCanBeCancelledWithoutPublishing()
    {
        var (original, _) = Original();
        var stored = TableStore.Read(new(original, ProcessingHistory.InternalPrefix + "CONFIG"));
        var cfg = JsonNode.Parse(stored.Text(1, 0))!.AsObject();
        cfg.Obj("ajuste_altura")["debug"] = true;
        var input = TableStore.Read(new(original, ProcessingHistory.InternalPrefix + "INPUT"));
        var trees = TableStore.Read(new(original, ProcessingHistory.Trees));
        var plots = TableStore.Read(new(original, ProcessingHistory.Plots));
        var stands = TableStore.Read(new(original, ProcessingHistory.InternalPrefix + "STANDS"));
        const int count = 211795;
        foreach (var table in new[] { input, trees })
        {
            object?[] row = table.Rows[1].ToArray();
            int dap = table.Require("DAP"), id = table.Column("Arvore_ID");
            while (table.Count <= count)
            {
                table.Add(row);
                table.Set(table.Count - 1, dap, 10d + table.Count % 3000 / 10d);
                if (id >= 0) table.Set(table.Count - 1, id, $"tree-{table.Count:D8}");
            }
        }
        var result = new ProcessingResult(count, plots.Count - 1, stands.Count - 1, 11);
        string output = Path.Combine(folder, "large.dlb");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var updates = new List<(int Percent, string Message)>();
        ProjectStore.AtomicWrite(output, temp => InventoryReport.Write(temp, cfg, input, (int)stored.Number(1, 1), trees, plots, stands,
            result, true, (p, message) => updates.Add((p, message))));
        Console.WriteLine($"Relatório com {count:N0} árvores: {watch.Elapsed.TotalSeconds:N2} s; {new FileInfo(output).Length:N0} bytes.");
        using (var reader = new CompactBankReader(output))
        {
            var table = reader.Table(ProcessingHistory.Trees);
            Assert.Equal(count, table.Rows);
            Assert.Single(reader.Rows(table, count - 1, 1));
            Assert.Equal(count, reader.Table(ProcessingHistory.InternalPrefix + "INPUT").Rows);
        }
        Assert.True(updates.Count > 100);
        Assert.Contains(updates, u => u.Message.StartsWith("Gravando RESULTADO - ARVORES"));
        using var cancellation = new CancellationTokenSource();
        string cancelled = Path.Combine(folder, "cancelled.dlb");
        Assert.Throws<OperationCanceledException>(() => ProjectStore.AtomicWrite(cancelled, temp => InventoryReport.Write(temp, cfg, input,
            (int)stored.Number(1, 1), trees, plots, stands, result, true, (_, _) => cancellation.Cancel(), cancellation.Token)));
        Assert.False(File.Exists(cancelled));
        Assert.Empty(Directory.GetFiles(folder, "cancelled.dlb*.tmp"));
    }

    [Fact]
    public void CancelledImportPreservesExistingBank()
    {
        var (stage, _) = Original();
        string bank = Path.Combine(folder, "shared.dlb");
        CompactBankFile.Import(bank, [(stage, "first.dlb")]);
        byte[] before = File.ReadAllBytes(bank);
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => CompactBankFile.Import(bank, [(stage, "second.dlb")], cancellation: cancellation.Token,
            progress: (_, _) => cancellation.Cancel()));
        Assert.Equal(before, File.ReadAllBytes(bank));
        Assert.Empty(Directory.GetFiles(folder, "shared.dlb.*.tmp"));
    }

    [Fact]
    public void RevisionsAlwaysUseOriginalVolumesAndPreserveHeightsIdsAndOriginal()
    {
        var (originalPath, original) = Original();
        byte[] before = File.ReadAllBytes(originalPath);
        var initial = TableStore.Read(new(originalPath, ProcessingHistory.Trees));
        Assert.Equal(11d, Enumerable.Range(1, initial.Count - 1).Sum(r => initial.Number(r, initial.Require("VTCC"))), 10);
        var factors = ProcessingHistory.FactorTemplate(originalPath, original, "talhao");
        for (int row = 1; row < factors.Count; row++) factors.Set(row, 3, .5);
        var second = ProcessingCatalog.NewRecord("input.dlb", "IFPC", "talhao", original, 2);
        var result = ProcessingHistory.Correct(originalPath, original, second, factors, Path.Combine(folder, "second.dlb"));
        Assert.Equal(5.5, result.Volume, 10);
        var corrected = TableStore.Read(new(Path.Combine(folder, "second.dlb"), ProcessingHistory.Trees));
        for (int row = 1; row < initial.Count; row++)
        {
            Assert.Equal(initial.Number(row, initial.Require("H")), corrected.Number(row, corrected.Require("H")));
            Assert.Equal(initial.Text(row, initial.Require("Arvore_ID")), corrected.Text(row, corrected.Require("Arvore_ID")));
            Assert.Equal("IFPC", corrected.Text(row, corrected.Require("Tipo_Inventario")));
            Assert.Equal(2, corrected.Number(row, corrected.Require("Versao")));
        }
        for (int row = 1; row < factors.Count; row++) factors.Set(row, 3, .8);
        var third = ProcessingCatalog.NewRecord("input.dlb", "IFPC", "talhao", second, 3);
        Assert.Equal(8.8, ProcessingHistory.Correct(originalPath, original, third, factors, Path.Combine(folder, "third.dlb")).Volume, 10);
        for (int row = 1; row < factors.Count; row++) factors.Set(row, 3, 0d);
        Assert.Equal(0, ProcessingHistory.Correct(originalPath, original, third, factors, Path.Combine(folder, "zero.dlb")).Volume);
        Assert.Equal(before, File.ReadAllBytes(originalPath));
    }

    [Fact]
    public void FactorsCannotComeFromAnotherInventoryAndMustCoverAllCodes()
    {
        var (path, run) = Original();
        var template = ProcessingHistory.FactorTemplate(path, run, "lote");
        var factors = template.Copy(); factors.Set(1, 0, "outro-inventario");
        Assert.Throws<InvalidDataException>(() => ProcessingHistory.ValidateFactors(factors, template));
        factors = template.Copy(); factors.Set(1, 3, 1.1);
        Assert.Throws<InvalidDataException>(() => ProcessingHistory.ValidateFactors(factors, template));
        factors = template.Copy(); factors.Rows.RemoveAt(1);
        Assert.Throws<InvalidDataException>(() => ProcessingHistory.ValidateFactors(factors, template));
    }

    [Fact]
    public async Task ParallelPublicationsPreserveBothRunsAndApprovalDetectsStaleRevision()
    {
        string project = Path.Combine(folder, "project.dlb"), bank = project;
        ProjectStore.Save(project, ProjectStore.Create("Rede"));
        string Stage(string name)
        {
            string path = Path.Combine(folder, name + ".dlb");
            using var writer = new CompactBankWriter(path); string id = writer.AddTable(["X"]); writer.Add(id, [1d]); writer.Complete(); return path;
        }
        var a = ProcessingCatalog.NewRecord("a", "IFC", "talhao"); var b = ProcessingCatalog.NewRecord("b", "IFPC", "lote");
        a["path"] = "project.dlb/a.dlb"; b["path"] = "project.dlb/b.dlb";
        string sa = Stage("a"), sb = Stage("b");
        await Task.WhenAll(Task.Run(() => ProcessingCatalog.Publish(project, [], bank, sa, a)), Task.Run(() => ProcessingCatalog.Publish(project, [], bank, sb, b)));
        Assert.Equal(2, ProcessingCatalog.Read(project).Arr("records").Count);
        Assert.True(CompactBankFile.Exists(Path.Combine(bank, "a.dlb"))); Assert.True(CompactBankFile.Exists(Path.Combine(bank, "b.dlb")));
        var approved = ProcessingCatalog.ChangeStatus(project, [], a.S("id"), 0, "aprovado");
        Assert.Throws<InvalidOperationException>(() => ProcessingCatalog.ChangeStatus(project, [], a.S("id"), 0, "em_analise"));
        Assert.Contains(approved.Arr("records"), r => r.S("id") == a.S("id") && r.B("current_approved"));
        Assert.NotEmpty(approved.Arr("records").First(r => r.S("id") == a.S("id")).S("approved_by"));
    }

    [Fact]
    public void ProjectSaveDoesNotOverwriteAnotherUsersChanges()
    {
        string path = Path.Combine(folder, "shared.dendro"); ProjectStore.Save(path, ProjectStore.Create("Inicial"));
        var a = ProjectStore.Read(path); var b = ProjectStore.Read(path);
        a.Obj("_dendro")["name"] = "Usuário A"; ProjectStore.Save(path, a);
        b.Obj("_dendro")["name"] = "Usuário B";
        Assert.Throws<InvalidOperationException>(() => ProjectStore.Save(path, b));
        Assert.Equal("Usuário A", ProjectStore.Read(path).Obj("_dendro").S("name"));
    }

    [Fact]
    public async Task ConflictingCorrectionsCannotOverwriteEachOtherAndApprovalPreservesAudit()
    {
        string project = Path.Combine(folder, "conflict.dlb"), bank = project;
        ProjectStore.Save(project, ProjectStore.Create("Conflito"));
        string Stage(string name)
        {
            string path = Path.Combine(folder, name + ".dlb"); using var writer = new CompactBankWriter(path);
            string id = writer.AddTable(["X"]); writer.Add(id, [1d]); writer.Complete(); return path;
        }
        var original = ProcessingCatalog.NewRecord("input", "IFC", "lote");
        original["path"] = "conflict.dlb/original.dlb"; original["sheets"] = new JsonArray("Dados");
        ProcessingCatalog.Publish(project, [], bank, Stage("original"), original);
        var a = ProcessingCatalog.NewRecord("input", "IFC", "lote", original, 2);
        var b = ProcessingCatalog.NewRecord("input", "IFC", "lote", original, 2);
        a["path"] = "conflict.dlb/a.dlb"; b["path"] = "conflict.dlb/b.dlb";
        a["sheets"] = new JsonArray("Dados"); b["sheets"] = new JsonArray("Dados");
        string sa = Stage("a"), sb = Stage("b");
        bool Publish(string stage, JsonObject record)
        {
            try { ProcessingCatalog.Publish(project, [], bank, stage, record); return true; }
            catch (InvalidOperationException) { return false; }
        }
        bool[] results = await Task.WhenAll(Task.Run(() => Publish(sa, a)), Task.Run(() => Publish(sb, b)));
        Assert.Single(results, r => r);
        var catalog = ProcessingCatalog.Read(project); Assert.Equal(2, catalog.Arr("records").Count);
        using (var reader = new CompactBankReader(bank))
            Assert.Equal(2, reader.Catalog.Tables.Count(table => table.Dataset is "original.dlb" or "a.dlb" or "b.dlb"));
        ProcessingCatalog.ChangeStatus(project, [], original.S("id"), 0, "aprovado");
        ProcessingHistory.ExportApproved(project, [], original.S("id"), ["Dados"], Path.Combine(folder, "original-aprovado.xlsx"));
        var second = catalog.Arr("records").Single(r => r.N("number") == 2);
        catalog = ProcessingCatalog.ChangeStatus(project, [], second.S("id"), 0, "aprovado");
        var old = catalog.Arr("records").Single(r => r.S("id") == original.S("id"));
        Assert.Equal("aprovado", old.S("status")); Assert.False(old.B("current_approved"));
        Assert.Equal(3, old!.AsObject().Arr("audit").Count);
        Assert.Throws<InvalidOperationException>(() => ProcessingHistory.ExportApproved(project, [], original.S("id"), ["Dados"], Path.Combine(folder, "proibido.xlsx")));
    }
}
