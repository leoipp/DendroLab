using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class TableFilterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-filter-test-" + Guid.NewGuid().ToString("N"));
    public TableFilterTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private string Input()
    {
        string path = Path.Combine(folder, "input.dlb");
        using var writer = new CompactBankWriter(path); string id = writer.AddTable(["Código", "Valor", "Grupo"]);
        for (int i = 0; i < 6000; i++) writer.Add(id, [i % 2 == 0 ? "0" : "Talhão A", (double)i, i % 3 == 0 ? null : "Ativo"]);
        writer.Complete(); return path;
    }

    [Fact]
    public void GlobalFilterPagesKeepOriginalRowNumbersAndDoNotMutateSource()
    {
        string input = Input(); byte[] before = File.ReadAllBytes(input);
        string preview;
        using (var filtered = FilteredTable.Create(new(input), [new("Código", FilterOperator.Equal, "0")], temporaryFolder: folder))
        {
            preview = filtered.Source.Path;
            var page = filtered.Page(500);
            Assert.Equal(6000, filtered.SourceCount); Assert.Equal(3000, page.Page.Total);
            Assert.Equal(501, page.Page.Data.Count);
            Assert.Equal(1000d, page.Page.Data.Number(1, 1)); Assert.Equal(1002, page.SourceRows[0]);
            Assert.Equal(2000, page.SourceRows[^1]);
        }
        Assert.False(File.Exists(preview)); Assert.False(File.Exists(preview + ".rows"));
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Fact]
    public void FiltersCombineNumbersTextAndEmptyValuesAcrossAllRows()
    {
        string input = Input();
        using var filtered = FilteredTable.Create(new(input), [new("Código", FilterOperator.Contains, "talhão"),
            new("Valor", FilterOperator.GreaterOrEqual, "5000,0"), new("Grupo", FilterOperator.Empty)], temporaryFolder: folder);
        var page = filtered.Page();
        Assert.Equal(167, page.Page.Total);
        Assert.All(page.Page.Data.Rows.Skip(1), r => { Assert.Equal("Talhão A", r[0]); Assert.True((double)r[1]! >= 5000); Assert.Null(r[2]); });
        using var none = FilteredTable.Create(new(input), [new("Valor", FilterOperator.Equal, "-1")], temporaryFolder: folder);
        Assert.Equal(0, none.Page().Page.Total); Assert.Empty(none.Page().SourceRows);
    }

    [Fact]
    public void CancelledFilteringCleansTemporaryFiles()
    {
        string input = Input(); using var cancellation = new CancellationTokenSource();
        var progress = new CancelProgress(cancellation);
        Assert.Throws<OperationCanceledException>(() => FilteredTable.Create(new(input), [], cancellation.Token, progress, folder));
        Assert.Empty(Directory.GetFiles(folder, "DendroLab-filter-*"));
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<TableProgress>
    {
        public void Report(TableProgress value) => cancellation.Cancel();
    }
}
