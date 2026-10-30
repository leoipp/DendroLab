using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public class MergeProgressTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-merge-progress-" + Guid.NewGuid().ToString("N"));
    public MergeProgressTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private sealed class ImmediateProgress(Action<TableProgress> report) : IProgress<TableProgress>
    { public void Report(TableProgress value) => report(value); }
    private TableSource Input()
    {
        string path = Path.Combine(folder, "input.dlb");
        using var writer = new CompactBankWriter(path);
        string id = writer.AddTable(["ID", "Grupo", "Volume"]);
        for (int i = 0; i < 10_000; i++) writer.Add(id, [(double)i, "A", (double)i / 10]);
        writer.Complete(); return new(path);
    }

    [Fact]
    public void MergeReportsStagesRowsAndCompletionWithoutFloodingUpdates()
    {
        var input = Input(); var reports = new List<TableProgress>();
        var result = TableTools.Merge([input, input], folder, "resultado", compact: true,
            progress: new ImmediateProgress(reports.Add));
        Assert.Equal(20_000, result.Rows);
        Assert.Null(reports[0].Percent);
        Assert.Contains(reports, r => r.Percent == 10);
        Assert.Contains(reports, r => r.Percent == 90 && r.Rows == 20_000);
        Assert.Contains(reports, r => r.Percent == 95);
        Assert.Equal(100, reports[^1].Percent);
        Assert.True(reports.Count < 100);
        Assert.Equal(20_000, TableStore.Page(new(result.Path)).Total);
    }

    [Fact]
    public void CancellationBeforePublicationLeavesNoOutputOrTemporaryFiles()
    {
        var input = Input(); using var cancellation = new CancellationTokenSource();
        var reports = new List<TableProgress>();
        var progress = new ImmediateProgress(update => {
            reports.Add(update);
            if (update.Percent == 95) cancellation.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() => TableTools.Merge([input, input], folder, "cancelado",
            cancellation: cancellation.Token, compact: true, progress: progress));
        Assert.False(File.Exists(Path.Combine(folder, "cancelado.dlb")));
        Assert.Empty(Directory.GetDirectories(folder, ".dendro-*"));
        Assert.DoesNotContain(reports, r => r.Percent == 100);
        Assert.Equal(10_000, TableStore.Page(input).Total);
    }
}
