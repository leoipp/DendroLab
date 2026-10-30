using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tabular;

public sealed record FilteredPage(TablePage Page, long[] SourceRows);

/// <summary>Local, bounded-memory filtered preview. The source is never modified; subsequent pages use compressed block indexes.</summary>
public sealed class FilteredTable : IDisposable
{
    public TableSource Source { get; }
    public long SourceCount { get; }
    private readonly string indexPath;
    private FilteredTable(string path, string index, long count) { Source = new(path); indexPath = index; SourceCount = count; }

    public static FilteredTable Create(TableSource source, IReadOnlyList<TableFilter> filters, CancellationToken cancellation = default,
        IProgress<TableProgress>? progress = null, string? temporaryFolder = null, TableSort? sort = null)
    {
        cancellation.ThrowIfCancellationRequested();
        string path = Path.Combine(temporaryFolder ?? Path.GetTempPath(), "DendroLab-filter-" + Guid.NewGuid().ToString("N") + ".dlb");
        string indexPath = path + ".rows";
        try
        {
            if (sort != null)
            {
                long scannedRows = TableSorting.Create(source, filters, sort, path, indexPath, cancellation, progress);
                return new(path,indexPath,scannedRows);
            }
            using var cursor = new TableCursor(source);
            var matches = TableTools.Predicate(cursor.Columns, filters);
            var updates = new TableProgressReporter(progress);
            using var writer = new CompactBankWriter(path);
            using var index = new BinaryWriter(File.Create(indexPath));
            string id = writer.AddTable(cursor.Columns);
            long scanned = 0, matched = 0;
            updates.Report(0, "Filtrando a tabela completa…", force: true);
            foreach (var row in cursor.Rows())
            {
                cancellation.ThrowIfCancellationRequested(); scanned++;
                if (matches(row)) { writer.Add(id, row); index.Write(source.HeaderRow + scanned); matched++; }
                if ((scanned & 2047) == 0)
                    updates.Report(cursor.TotalRows > 0 ? 95d * scanned / cursor.TotalRows : null,
                        $"Filtrando: {scanned:N0} linhas verificadas · {matched:N0} encontradas", scanned);
            }
            cancellation.ThrowIfCancellationRequested();
            writer.Complete();
            updates.Report(100, $"Filtro concluído: {matched:N0} de {scanned:N0} linhas.", scanned, true);
            return new(path, indexPath, scanned);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(indexPath)) File.Delete(indexPath);
            throw;
        }
    }

    public FilteredPage Page(long offset = 0, int limit = 500)
    {
        var page = TableStore.Page(Source, offset, limit);
        var rows = new long[page.Data.Count - 1];
        using var index = new BinaryReader(File.OpenRead(indexPath));
        index.BaseStream.Position = offset * sizeof(long);
        for (int i = 0; i < rows.Length; i++) rows[i] = index.ReadInt64();
        return new(page, rows);
    }

    public IEnumerable<long> SourceRowNumbers()
    {
        using var reader = new BinaryReader(File.OpenRead(indexPath));
        while (reader.BaseStream.Position < reader.BaseStream.Length) yield return reader.ReadInt64();
    }

    public void Dispose()
    {
        if (File.Exists(Source.Path)) File.Delete(Source.Path);
        if (File.Exists(indexPath)) File.Delete(indexPath);
    }
}
