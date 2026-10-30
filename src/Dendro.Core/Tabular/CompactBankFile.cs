using System.Text;
using System.Text.Json;
using Dendro.Core.Configuration;

namespace Dendro.Core.Tabular;

/// <summary>One physical project bank. Dataset paths identify catalog scopes, never additional files.</summary>
public static class CompactBankFile
{
    public static (string File, string Dataset) Location(string path)
    {
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        if (parent != null && Path.GetExtension(parent).Equals(".dlb", StringComparison.OrdinalIgnoreCase) && File.Exists(parent))
            return (parent, Path.GetFileName(full));
        return (full, "");
    }

    public static bool Exists(string path)
    {
        var location = Location(path);
        if (!File.Exists(location.File)) return false;
        if (location.Dataset.Length == 0) return true;
        using var reader = new CompactBankReader(location.File);
        return reader.Catalog.Tables.Any(t => t.Dataset == location.Dataset);
    }

    public static bool RemoveDatasets(string path, IReadOnlyCollection<string> datasets)
    {
        var location = Location(path);
        var removed = datasets.Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        if (location.Dataset.Length > 0) removed.Add(location.Dataset);
        if (removed.Count == 0 || !File.Exists(location.File)) return false;
        return RemoveTables(location.File, table => removed.Contains(table.Dataset));
    }

    public static bool RemoveTable(TableSource source, IReadOnlyCollection<string>? descendantDatasets = null)
    {
        CompactTable selected;
        using (var reader = new CompactBankReader(source.Path)) selected = reader.Table(source.Table);
        var descendants = (descendantDatasets ?? []).ToHashSet(StringComparer.Ordinal);
        return RemoveTables(Location(source.Path).File, table => table.Id == selected.Id || descendants.Contains(table.Dataset));
    }

    private static bool RemoveTables(string path, Predicate<CompactTable> remove)
    {
        var location = Location(path);
        using var lease = SharedFileLock.Acquire(location.File + ".write.lock");

        var tables = new List<CompactTable>();
        using (var reader = new CompactBankReader(location.File))
            tables.AddRange(reader.Catalog.Tables);
        int originalCount = tables.Count;
        tables.RemoveAll(remove);
        if (tables.Count == originalCount) return false;
        if (tables.Count == 0)
        {
            File.Delete(location.File);
            return true;
        }

        string temporary = location.File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                output.Write(CompactBankReader.Magic);
                output.Write(new byte[8]);
                var rewritten = new List<CompactTable>(tables.Count);
                using var reader = new CompactBankReader(location.File);
                using var data = File.OpenRead(location.File);
                foreach (var table in tables)
                {
                    var blocks = new List<CompactBlock>();
                    foreach (var block in table.Blocks)
                    {
                        data.Position = block.Offset;
                        long offset = output.Position;
                        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                            System.Security.Cryptography.HashAlgorithmName.SHA256
                        );
                        byte[] buffer = new byte[Math.Min(block.Bytes, 65536)];
                        int remaining = block.Bytes;
                        while (remaining > 0)
                        {
                            int count = data.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                            if (count == 0) throw new EndOfStreamException("Bloco incompleto no banco.");
                            hash.AppendData(buffer, 0, count);
                            output.Write(buffer, 0, count);
                            remaining -= count;
                        }
                        if (Convert.ToHexString(hash.GetHashAndReset()) != block.Hash)
                            throw new InvalidDataException("Bloco corrompido no banco do projeto.");
                        blocks.Add(block with { Offset = offset });
                    }
                    rewritten.Add(table with { Blocks = blocks.ToArray() });
                }
                long index = output.Position;
                byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new CompactCatalog(2, rewritten.ToArray()));
                if (catalog.Length > 32 * 1024 * 1024) throw new InvalidDataException("Catálogo acima de 32 MB.");
                using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                writer.Write(catalog.Length);
                writer.Write(catalog);
                output.Position = 8;
                writer.Write(index);
                output.Flush(true);
            }
            File.Move(temporary, location.File, true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Compressed blocks are copied unchanged, with relocated offsets. No cell materialization/recompression.
    // Atomic replacement publishes all incoming datasets together and preserves the previous bank on failure.
    public static void ReplaceTable(TableSource target, string replacement, CompactTable? expected = null,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
    {
        var location = Location(target.Path);
        CompactTable selected;
        using (var reader = new CompactBankReader(target.Path)) selected = reader.Table(target.Table);
        if (expected != null && !SameVersion(selected, expected))
            throw new InvalidOperationException("A tabela foi alterada por outro usuário. Atualize a visualização antes de editar.");
        using (var reader = new CompactBankReader(replacement))
            if (reader.Catalog.Tables.Length != 1) throw new InvalidDataException("A substituição exige exatamente uma tabela.");
        ImportCore(location.File, [(replacement, selected.Dataset)], true, expected ?? selected, cancellation, progress);
    }

    internal static bool SameVersion(CompactTable first, CompactTable second) => first.Id == second.Id && first.Name == second.Name
        && first.Dataset == second.Dataset && first.Rows == second.Rows && first.Columns.SequenceEqual(second.Columns)
        && first.Blocks.Select(b => (b.Bytes, b.FirstRow, b.Rows, b.Hash)).SequenceEqual(second.Blocks.Select(b => (b.Bytes, b.FirstRow, b.Rows, b.Hash)));

    public static void Import(string destination, IReadOnlyList<(string Source, string Dataset)> incoming, bool replace = false,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
        => ImportCore(destination, incoming, replace, null, cancellation, progress);

    private static void ImportCore(string destination, IReadOnlyList<(string Source, string Dataset)> incoming, bool replace, CompactTable? replacedTable,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
    {
        destination = Path.GetFullPath(destination);
        if (incoming.Count == 0) return;
        using var lease = SharedFileLock.Acquire(destination + ".write.lock", cancellation);
        if (incoming.Any(i => (string.IsNullOrWhiteSpace(i.Dataset) && replacedTable == null) || Path.GetFileName(i.Dataset) != i.Dataset)
            || incoming.Select(i => i.Dataset).Distinct(StringComparer.Ordinal).Count() != incoming.Count)
            throw new InvalidDataException("Identificadores de tabelas inválidos ou repetidos.");
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                output.Write(CompactBankReader.Magic); output.Write(new byte[8]);
                var tables = new List<CompactTable>();
                var usedIds = new HashSet<string>(StringComparer.Ordinal);
                long totalBytes = 0, copiedBytes = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (File.Exists(destination))
                {
                    using var existing = new CompactBankReader(destination);
                    foreach (var table in existing.Catalog.Tables) usedIds.Add(table.Id);
                    totalBytes += existing.Catalog.Tables.Where(t => replacedTable != null ? t.Id != replacedTable.Id : !incoming.Any(i => i.Dataset == t.Dataset))
                        .Sum(t => t.Blocks.Sum(b => (long)b.Bytes));
                    if (replacedTable != null && !existing.Catalog.Tables.Any(t => SameVersion(t, replacedTable)))
                        throw new InvalidOperationException("A tabela foi alterada por outro usuário. Atualize antes de gravar.");
                }
                foreach (var item in incoming)
                {
                    using var reader = new CompactBankReader(item.Source);
                    totalBytes += reader.Catalog.Tables.Sum(t => t.Blocks.Sum(b => (long)b.Bytes));
                }
                progress?.Invoke(0, totalBytes);
                int nextId = 0;
                string NewId() { while (usedIds.Contains("t" + nextId)) nextId++; string id = "t" + nextId++; usedIds.Add(id); return id; }
                var datasets = incoming.Select(i => i.Dataset).ToHashSet(StringComparer.Ordinal);
                void Copy(string source, string? dataset)
                {
                    using var reader = new CompactBankReader(source);
                    using var data = File.OpenRead(Location(source).File);
                    foreach (var table in reader.Catalog.Tables)
                    {
                        if (dataset == null && (replacedTable != null ? table.Id == replacedTable.Id : datasets.Contains(table.Dataset))) {
                            if (!replace) throw new IOException("Já existe uma tabela com esse nome no banco do projeto.");
                            continue;
                        }
                        var blocks = new List<CompactBlock>();
                        foreach (var block in table.Blocks)
                        {
                            // Validate checksum while copying with a fixed-size buffer.
                            data.Position = block.Offset;
                            long offset = output.Position;
                            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                            byte[] buffer = new byte[Math.Min(block.Bytes, 65536)];
                            int remaining = block.Bytes;
                            while (remaining > 0) {
                                cancellation.ThrowIfCancellationRequested();
                                int count = data.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                                if (count == 0) throw new EndOfStreamException("Bloco incompleto no banco.");
                                hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count); remaining -= count;
                                copiedBytes += count;
                                if (watch.ElapsedMilliseconds >= 150) { progress?.Invoke(copiedBytes, totalBytes); watch.Restart(); }
                            }
                            if (Convert.ToHexString(hash.GetHashAndReset()) != block.Hash)
                                throw new InvalidDataException("Bloco corrompido no banco do projeto.");
                            blocks.Add(block with { Offset = offset });
                        }
                        tables.Add(table with { Id = dataset == null ? table.Id : replacedTable?.Id ?? NewId(), Dataset = dataset ?? table.Dataset,
                            Name = dataset != null && replacedTable != null ? replacedTable.Name : table.Name, Blocks = blocks.ToArray() });
                    }
                }
                if (File.Exists(destination)) Copy(destination, null);
                foreach (var item in incoming) Copy(item.Source, item.Dataset);
                cancellation.ThrowIfCancellationRequested();
                long index = output.Position;
                byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new CompactCatalog(2, tables.ToArray()));
                if (catalog.Length > 32 * 1024 * 1024) throw new InvalidDataException("Catálogo acima de 32 MB.");
                using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                writer.Write(catalog.Length); writer.Write(catalog); output.Position = 8; writer.Write(index); output.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
