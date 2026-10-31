using Dendro.Core.Tabular;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tests;

public class CompactBankTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-compact-" + Guid.NewGuid().ToString("N"));
    public CompactBankTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void RoundTripMultipleTablesUnicodeAndNullsAcrossBlocks()
    {
        string path = Path.Combine(folder, "dados.dlb");
        using (var writer = new CompactBankWriter(path))
        {
            string a = writer.AddTable(["Ano", "Talhão", "V", "Vazio"], "Medições");
            string b = writer.AddTable(["ID", "Valor"], "Resumo");
            for (int r = 0; r < 6000; r++) writer.Add(a, [2026d, "植林 · Água", (double)r / 3, r % 2 == 0 ? null : ""]);
            writer.Add(b, ["A", double.NaN]); writer.Complete();
        }
        Assert.Equal(new[] { "Medições", "Resumo" }, TableStore.Tables(path));
        var page = TableStore.Page(new(path, "Medições"), 2045, 10);
        Assert.Equal(6000, page.Total);
        Assert.Equal(11, page.Data.Count);
        Assert.Equal(2045d / 3, page.Data.Number(1, 2), 12);
        Assert.Equal("植林 · Água", page.Data.Text(1, 1));
        using var cursor = new TableCursor(new(path, "Resumo"));
        Assert.Null(cursor.Rows().Single()[1]);
        Assert.Equal(1, TableStore.Page(new(path, "Medições"), 6000).Data.Count);
    }

    [Fact]
    public void ExcelPageCountsAndSkipsBlankRowsWithoutChangingPageContents()
    {
        string path = Path.Combine(folder, "sparse.xlsx");
        var input = new DataTable(["ID", "Nome"]);
        input.Add(1d, "A");
        input.Add(null, null);
        input.Add("", "");
        input.Add(2d, "B");
        WorkbookWriter.Write(path, [new("Dados", input)]);

        var first = TableStore.Page(new(path, "Dados"), 0, 1);
        Assert.Equal(2, first.Total);
        Assert.Equal(2, first.Data.Count);
        Assert.Equal(1d, first.Data.Number(1, 0));
        var second = TableStore.Page(new(path, "Dados"), 1, 1);
        Assert.Equal(2, second.Total);
        Assert.Equal(2, second.Data.Count);
        Assert.Equal(2d, second.Data.Number(1, 0));
        Assert.Equal(1, TableStore.Page(new(path, "Dados"), 2, 1).Data.Count);
    }

    [Fact]
    public void CompactMergeAndStrataRemainInDatabaseAndUseSameValues()
    {
        string input = Path.Combine(folder, "entrada.dlb");
        using (var writer = new CompactBankWriter(input)) {
            string id = writer.AddTable(["Grupo", "V"]);
            writer.Add(id, ["A", 2d]); writer.Add(id, ["B", 4d]); writer.Complete();
        }
        var merged = TableTools.Merge([new(input), new(input)], folder, "unidas", compact: true);
        Assert.EndsWith(".dlb", merged.Path);
        Assert.Equal(4, merged.Rows);
        var strata = TableTools.Stratify(new(merged.Path), "Grupo", [],
            [new(TableTools.Key("A"), "A"), new(TableTools.Key("B"), "B")], folder, compact: true);
        Assert.All(strata, s => Assert.Equal(2, s.Rows));
        Assert.All(strata, s => Assert.EndsWith(".dlb", s.Path));
        Assert.Empty(Directory.GetFiles(folder, "*.xlsx"));
        Assert.Equal(12, Enumerable.Range(1, 4).Sum(r => TableStore.Read(new(merged.Path)).Number(r, 1)));
        Assert.Throws<IOException>(() => TableTools.Merge([new(input), new(input)], folder, "unidas", compact: true));
    }

    [Fact]
    public void CorruptedBlocksAndIncompleteFilesAreRejected()
    {
        string path = Path.Combine(folder, "dados.dlb");
        using (var writer = new CompactBankWriter(path)) {
            string id = writer.AddTable(["V"]); writer.Add(id, [1d]); writer.Complete();
        }
        using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) {
            file.Position = 16; int original = file.ReadByte(); file.Position = 16; file.WriteByte((byte)(original ^ 1));
        }
        Assert.Throws<InvalidDataException>(() => TableStore.Page(new(path)));
        string incomplete = Path.Combine(folder, "incompleto.dlb");
        using (var writer = new CompactBankWriter(incomplete)) writer.AddTable(["V"]);
        Assert.Throws<InvalidDataException>(() => TableStore.Tables(incomplete));
    }

    [Fact]
    public void LatePageAllocationsAndDiskSizeStaySmallForLargeRepeatedTable()
    {
        string path = Path.Combine(folder, "grande.dlb");
        using (var writer = new CompactBankWriter(path)) {
            string id = writer.AddTable(["Ano", "Estrato", "Parcela", "Volume"]);
            for (int r = 0; r < 100_000; r++) writer.Add(id, [2026d, "Eucalipto · Estrato 01", (double)(r % 500), r * .125]);
            writer.Complete();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var page = TableStore.Page(new(path), 99_500, 500);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(100_000, page.Total);
        Assert.Equal(99_500 * .125, page.Data.Number(1, 3));
        Assert.True(allocated < 8 * 1024 * 1024, $"Página alocou {allocated:N0} bytes.");
        Assert.True(new FileInfo(path).Length < 1_000_000, "Compressão insuficiente na base de referência com valores repetidos.");
    }

    [Fact]
    public void ImportHonorsCancellationAndHeaderSelection()
    {
        string path = Path.Combine(folder, "origem.dlb");
        using (var writer = new CompactBankWriter(path)) {
            string id = writer.AddTable(["Título", ""]); writer.Add(id, ["Grupo", "V"]); writer.Add(id, ["A", 3d]); writer.Complete();
        }
        var source = new TableSource(path, HeaderRow: 2);
        var copy = TableTools.Import(source, folder, "copia");
        Assert.Equal(1, copy.Rows);
        Assert.Equal("Grupo", TableStore.Page(new(copy.Path)).Data.Text(0, 0));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TableTools.Import(source, folder, "cancelado", cancellation.Token));
        Assert.False(File.Exists(Path.Combine(folder, "cancelado.dlb")));
        Assert.Empty(Directory.GetFiles(folder, ".dendro-*.tmp"));
    }
}
