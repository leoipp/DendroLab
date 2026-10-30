using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class TableRowEditorTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-row-edit-" + Guid.NewGuid().ToString("N"));
    public TableRowEditorTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private TableSource Input(int count = 1200)
    {
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".dlb");
        using var writer = new CompactBankWriter(path);
        string other = writer.AddTable(["Texto"], "Outra"); writer.Add(other, ["Preservar"]);
        string id = writer.AddTable(["ID", "Valor"], "Entrada");
        for (int i = 0; i < count; i++) writer.Add(id, [(double)i, i % 2 == 0 ? "zero" : "um"]);
        writer.Complete(); return new(path, "Entrada");
    }

    [Fact]
    public void DeletesFilteredRowsAcrossPagesAndPreservesOtherTables()
    {
        var input = Input(); var version = TableRowEditor.Version(input);
        using var filtered = FilteredTable.Create(input, [new("Valor", FilterOperator.Equal, "zero")], temporaryFolder: folder);
        Assert.Equal(600, filtered.Page().Page.Total);
        var result = TableRowEditor.Delete(input, version, filtered.SourceRowNumbers());
        Assert.Equal(600, result.Removed); Assert.Equal(600, result.Remaining);
        using var reader = new CompactBankReader(input.Path);
        Assert.Equal(version.Id, reader.Table("Entrada").Id);
        Assert.All(reader.Rows(reader.Table("Entrada")), row => Assert.Equal("um", row[1]));
        Assert.Equal("Preservar", reader.Rows(reader.Table("Outra")).Single()[0]);
    }

    [Fact]
    public void SelectedRowsAreOriginalPositionsAndEmptyTableIsValid()
    {
        var input = Input(4);
        var deleted = TableRowEditor.Delete(input, TableRowEditor.Version(input), [2, 5]);
        Assert.Equal(2, deleted.Removed);
        using (var reader = new CompactBankReader(input.Path))
            Assert.Equal(new object?[] { 1d, 2d }, reader.Rows(reader.Table("Entrada")).Select(r => r[0]));
        TableRowEditor.Delete(input, TableRowEditor.Version(input), [2, 3]);
        Assert.Equal(0, TableStore.Page(input).Total);
    }

    [Fact]
    public void ChangedDataWithSameSchemaRejectsStaleDeletion()
    {
        var input = Input(4); var old = TableRowEditor.Version(input);
        string replacement = Path.Combine(folder, "replacement.dlb");
        using (var writer = new CompactBankWriter(replacement))
        {
            string id = writer.AddTable(["ID", "Valor"]);
            for (int i = 0; i < 4; i++) writer.Add(id, [(double)i, "Outro"]);
            writer.Complete();
        }
        CompactBankFile.ReplaceTable(input, replacement);
        byte[] before = File.ReadAllBytes(input.Path);
        Assert.Throws<InvalidOperationException>(() => TableRowEditor.Delete(input, old, [2]));
        Assert.Equal(before, File.ReadAllBytes(input.Path));
    }

    [Fact]
    public void RelocatedBlocksFromAnotherTableDoNotChangeSelectedRowMeaning()
    {
        var input = Input(4); var version = TableRowEditor.Version(input);
        string replacement = Path.Combine(folder, "other.dlb");
        using (var writer = new CompactBankWriter(replacement))
        {
            string id = writer.AddTable(["Texto"]);
            for (int i = 0; i < 5000; i++) writer.Add(id, ["Alterada " + i]);
            writer.Complete();
        }
        CompactBankFile.ReplaceTable(new(input.Path, "Outra"), replacement);
        Assert.Equal(1, TableRowEditor.Delete(input, version, [2]).Removed);
        Assert.Equal(1d, TableStore.Page(input).Data.Number(1, 0));
        Assert.Equal(5000, TableStore.Page(new(input.Path, "Outra")).Total);
    }

    [Fact]
    public void RemovingMultipleColumnsKeepsRowsAndOtherTablesAndRefusesAllColumns()
    {
        var input = Input(4); var version = TableRowEditor.Version(input);
        byte[] before = File.ReadAllBytes(input.Path);
        Assert.Throws<InvalidDataException>(() => TableRowEditor.RemoveColumns(input, version, ["ID", "Valor"]));
        Assert.Equal(before, File.ReadAllBytes(input.Path));
        Assert.Equal(4, TableRowEditor.RemoveColumns(input, version, ["Valor"]));
        var page = TableStore.Page(input); Assert.Equal(4, page.Total); Assert.Equal(1, page.Data.Width); Assert.Equal(3d, page.Data.Number(4, 0));
        Assert.Equal("Preservar", TableStore.Page(new(input.Path, "Outra")).Data.Text(1, 0));
    }

    [Fact]
    public void CancellationBeforePublicationLeavesBankIntact()
    {
        var input = Input(4); byte[] before = File.ReadAllBytes(input.Path);
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => TableRowEditor.Delete(input, TableRowEditor.Version(input), [2], cancellation.Token, new CancelProgress(cancellation)));
        Assert.Equal(before, File.ReadAllBytes(input.Path));
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<TableProgress>
    {
        public void Report(TableProgress value) => cancellation.Cancel();
    }
}
