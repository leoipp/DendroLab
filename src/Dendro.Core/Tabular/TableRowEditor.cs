namespace Dendro.Core.Tabular;

public sealed record RowDeletion(long Removed, long Remaining);

public static class TableRowEditor
{
    public static long RemoveColumns(TableSource source, CompactTable expected, IReadOnlyCollection<string> columns,
        CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
    {
        var removed = columns.ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0 || removed.Any(c => !expected.Columns.Contains(c, StringComparer.Ordinal)))
            throw new InvalidDataException("Selecione colunas existentes para remover.");
        if (removed.Count == expected.Columns.Length) throw new InvalidDataException("A tabela precisa manter pelo menos uma coluna.");
        string stage = Path.Combine(Path.GetTempPath(), "DendroLab-columns-" + Guid.NewGuid().ToString("N") + ".dlb");
        var updates = new TableProgressReporter(progress);
        try
        {
            using (var reader = new CompactBankReader(source.Path))
            using (var writer = new CompactBankWriter(stage))
            {
                var current = reader.Table(source.Table);
                if (source.HeaderRow != 1 || !CompactBankFile.SameVersion(current, expected))
                    throw new InvalidOperationException("A tabela mudou desde a visualização. Reabra a tabela antes de remover colunas.");
                int[] mapping = Enumerable.Range(0, expected.Columns.Length).Where(i => !removed.Contains(expected.Columns[i])).ToArray();
                string id = writer.AddTable(mapping.Select(i => expected.Columns[i]).ToArray(), expected.Name);
                long count = 0;
                foreach (var row in reader.Rows(current))
                {
                    cancellation.ThrowIfCancellationRequested(); writer.Add(id, mapping.Select(i => row[i]).ToArray()); count++;
                    if ((count & 2047) == 0) updates.Report(85d * count / Math.Max(1, expected.Rows), $"Removendo colunas: {count:N0} linhas preservadas", count);
                }
                cancellation.ThrowIfCancellationRequested(); writer.Complete();
            }
            updates.Report(90, "Gravando tabela sem as colunas removidas…", force: true);
            CompactBankFile.ReplaceTable(source, stage, expected, cancellation,
                (copied, total) => updates.Report(90 + 9d * copied / Math.Max(1, total), $"Gravando banco: {copied / 1048576d:N1} de {total / 1048576d:N1} MB…"));
            return expected.Rows;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public static CompactTable Version(TableSource source)
    {
        if (source.HeaderRow != 1) throw new InvalidOperationException("A edição exige uma tabela DLB com cabeçalho na primeira linha.");
        using var reader = new CompactBankReader(source.Path);
        return reader.Table(source.Table);
    }

    public static RowDeletion Delete(TableSource source, CompactTable expected, IEnumerable<long> sourceRows,
        CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        string stage = Path.Combine(Path.GetTempPath(), "DendroLab-delete-" + Guid.NewGuid().ToString("N") + ".dlb");
        var updates = new TableProgressReporter(progress);
        long removed = 0, remaining = 0;
        try
        {
            using (var reader = new CompactBankReader(source.Path))
            using (var writer = new CompactBankWriter(stage))
            using (var rowsToDelete = sourceRows.GetEnumerator())
            {
                var current = reader.Table(source.Table);
                if (source.HeaderRow != 1 || !CompactBankFile.SameVersion(current, expected))
                    throw new InvalidOperationException("A tabela mudou desde a visualização. Reabra a tabela antes de excluir linhas.");
                bool hasNext = rowsToDelete.MoveNext();
                if (!hasNext) throw new InvalidOperationException("Selecione pelo menos uma linha para excluir.");
                long target = rowsToDelete.Current, rowNumber = 1;
                string id = writer.AddTable(expected.Columns, expected.Name);
                foreach (var row in reader.Rows(current))
                {
                    cancellation.ThrowIfCancellationRequested(); rowNumber++;
                    if (hasNext && target < rowNumber) throw new InvalidDataException("As linhas para excluir devem ser válidas, únicas e ordenadas.");
                    if (hasNext && target == rowNumber)
                    {
                        removed++;
                        hasNext = rowsToDelete.MoveNext();
                        if (hasNext) target = rowsToDelete.Current;
                    }
                    else { writer.Add(id, row); remaining++; }
                    if ((rowNumber & 2047) == 0) updates.Report(85d * (rowNumber - 1) / Math.Max(1, expected.Rows),
                        $"Preparando exclusão: {removed:N0} removidas · {remaining:N0} preservadas", rowNumber - 1);
                }
                if (hasNext) throw new InvalidDataException("Uma linha selecionada não existe mais. Atualize a tabela.");
                cancellation.ThrowIfCancellationRequested(); writer.Complete();
            }
            updates.Report(90, "Gravando tabela atualizada no banco…", force: true);
            CompactBankFile.ReplaceTable(source, stage, expected, cancellation,
                (copied, total) => updates.Report(90 + 9d * copied / Math.Max(1, total), $"Gravando banco: {copied / 1048576d:N1} de {total / 1048576d:N1} MB…"));
            return new(removed, remaining);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
