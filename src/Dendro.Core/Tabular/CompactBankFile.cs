using System.Text;
using Dendro.Core.Configuration;

namespace Dendro.Core.Tabular;

/// <summary>One physical project bank. Dataset paths identify catalog scopes, never additional files.</summary>
public static class CompactBankFile
{
    public static bool IsCompactBank(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
        Span<byte> magic = stackalloc byte[8];
        try
        {
            stream.ReadExactly(magic);
            return magic.SequenceEqual(CompactBankFormat.LegacyMagic)
                || magic.SequenceEqual(CompactBankFormat.PreviousMagic)
                || magic.SequenceEqual(CompactBankFormat.Magic);
        }
        catch (EndOfStreamException) { return false; }
    }

    public static (string File, string Dataset) Location(string path)
    {
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        if (parent != null && IsCompactBank(parent))
            return (parent, Path.GetFileName(full));
        return (full, "");
    }

    public static string? ReadTextDataset(string path, string dataset)
    {
        if (!File.Exists(path)) return null;
        using var reader = new CompactBankReader(path);
        var table = reader.Catalog.Tables.SingleOrDefault(t => t.Dataset == dataset);
        if (table == null) return null;
        if (table.Columns.Length != 1 || table.Rows != 1 || table.Columns[0] != "Value")
            throw new InvalidDataException($"Metadados internos inválidos: {dataset}.");
        return reader.Rows(table).Single()[0]?.ToString()
            ?? throw new InvalidDataException($"Metadados internos vazios: {dataset}.");
    }

    public static string? UpdateTextDataset(string destination, string dataset, Func<string?, string> update,
        IReadOnlyList<(string Source, string Dataset)>? incoming = null, IReadOnlyCollection<string>? removeDatasets = null,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
    {
        destination = Path.GetFullPath(destination);
        using var lease = SharedFileLock.Acquire(destination + ".write.lock", cancellation);
        string? next = update(ReadTextDataset(destination, dataset));
        string temporary = Path.Combine(Path.GetTempPath(), "DendroLab-meta-" + Guid.NewGuid().ToString("N") + ".dlb");
        try
        {
            using (var writer = new CompactBankWriter(temporary))
            {
                string table = writer.AddTable(["Value"], dataset);
                writer.Add(table, [next]);
                writer.Complete();
            }
            var batch = (incoming ?? []).Append((temporary, dataset)).ToArray();
            ImportCore(destination, batch, true, null, cancellation, progress, removeDatasets, true);
            return next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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

    public static (long FileBytes, long ActiveBlockBytes) StorageUsage(string path)
    {
        string bank = Location(path).File;
        using var reader = new CompactBankReader(bank);
        long activeBytes = reader.Catalog.Tables.Sum(table => table.Blocks.Sum(block => (long)block.Bytes));
        return (new FileInfo(bank).Length, activeBytes);
    }

    /// <summary>Rewrites only live blocks into a fresh v4 bank and atomically replaces the old file.</summary>
    public static void Compact(string path, CancellationToken cancellation = default, Action<long, long>? progress = null)
    {
        string bank = Location(path).File;
        using var lease = SharedFileLock.Acquire(bank + ".write.lock", cancellation);
        CompactLocked(bank, cancellation, progress);
    }

    private static void CompactLocked(string bank, CancellationToken cancellation, Action<long, long>? progress)
    {
        CompactTable[] tables;
        using (var reader = new CompactBankReader(bank)) tables = reader.Catalog.Tables;
        string temporary = bank + ".compact-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            long total = tables.Sum(table => table.Blocks.Sum(block => (long)block.Bytes));
            long copied = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var input = new FileStream(bank, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                CompactBankFormat.WriteHeader(output);
                byte[] buffer = new byte[65536];
                var rewritten = new List<CompactTable>(tables.Length);
                foreach (var table in tables)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var blocks = new List<CompactBlock>(table.Blocks.Length);
                    foreach (var block in table.Blocks)
                    {
                        input.Position = block.Offset;
                        long offset = output.Position;
                        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                            System.Security.Cryptography.HashAlgorithmName.SHA256);
                        int remaining = block.Bytes;
                        while (remaining > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int count = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                            if (count == 0) throw new EndOfStreamException("Bloco incompleto no banco.");
                            hash.AppendData(buffer, 0, count);
                            output.Write(buffer, 0, count);
                            remaining -= count;
                            copied += count;
                            if (watch.ElapsedMilliseconds >= 150)
                            {
                                progress?.Invoke(copied, total);
                                watch.Restart();
                            }
                        }
                        if (Convert.ToHexString(hash.GetHashAndReset()) != block.Hash)
                            throw new InvalidDataException("Bloco corrompido no banco do projeto.");
                        blocks.Add(block with { Offset = offset });
                    }
                    rewritten.Add(table with { Blocks = blocks.ToArray() });
                }
                cancellation.ThrowIfCancellationRequested();
                CompactBankFormat.PublishCatalog(output, 0, new CompactCatalog(2, rewritten.ToArray()));
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, bank, true);
            CompactBankReader.InvalidateCatalog(bank);
            progress?.Invoke(total, total);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool RemoveTable(TableSource source, IReadOnlyCollection<string>? descendantDatasets = null)
    {
        CompactTable selected;
        using (var reader = new CompactBankReader(source.Path)) selected = reader.Table(source.Table);
        var descendants = (descendantDatasets ?? []).ToHashSet(StringComparer.Ordinal);
        return RemoveTables(Location(source.Path).File, table => table.Id == selected.Id || descendants.Contains(table.Dataset));
    }

    public static bool RemoveTables(string path, IReadOnlyCollection<string> tableIds, IReadOnlyCollection<string>? datasets = null)
    {
        var ids = tableIds.ToHashSet(StringComparer.Ordinal);
        var datasetIds = (datasets ?? []).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 && datasetIds.Count == 0) return false;
        return RemoveTables(path, table => ids.Contains(table.Id) || datasetIds.Contains(table.Dataset));
    }

    private static bool RemoveTables(string path, Predicate<CompactTable> remove)
    {
        var location = Location(path);
        using var lease = SharedFileLock.Acquire(location.File + ".write.lock");

        CompactTable[] sourceTables;
        long generation;
        using (var reader = new CompactBankReader(location.File))
        {
            sourceTables = reader.Catalog.Tables;
            generation = reader.Generation;
        }
        var tables = sourceTables.ToList();
        int originalCount = tables.Count;
        tables.RemoveAll(remove);
        if (tables.Count == originalCount) return false;
        if (tables.Count == 0)
        {
            File.Delete(location.File);
            return true;
        }

        if (CompactBankFormat.IsAppendable(location.File))
        {
            AppendCatalog(location.File, new CompactCatalog(2, tables.ToArray()), generation + 1);
            return true;
        }

        string temporary = location.File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                CompactBankFormat.WriteHeader(output);
                var rewritten = new List<CompactTable>(tables.Count);
                using var data = File.OpenRead(location.File);
                byte[] buffer = new byte[65536];
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
                CompactBankFormat.PublishCatalog(output, 0, new CompactCatalog(2, rewritten.ToArray()));
            }
            File.Move(temporary, location.File, true);
            CompactBankReader.InvalidateCatalog(location.File);
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
        && first.Blocks.Length == second.Blocks.Length
        && first.Blocks.Zip(second.Blocks).All(pair => SameBlock(pair.First, pair.Second));

    private static bool SameBlock(CompactBlock first, CompactBlock second)
    {
        if ((first.Bytes, first.FirstRow, first.Rows, first.Hash) != (second.Bytes, second.FirstRow, second.Rows, second.Hash))
            return false;
        if (first.Segments == null || second.Segments == null)
            return first.Segments == null && second.Segments == null;
        return first.Segments.Select(segment => (segment.FirstColumn, segment.ColumnCount, segment.Offset, segment.Bytes, segment.Hash))
            .SequenceEqual(second.Segments.Select(segment => (segment.FirstColumn, segment.ColumnCount, segment.Offset, segment.Bytes, segment.Hash)));
    }

    public static void Import(string destination, IReadOnlyList<(string Source, string Dataset)> incoming, bool replace = false,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
        => ImportCore(destination, incoming, replace, null, cancellation, progress);

    private static void AppendCatalog(string path, CompactCatalog catalog, long generation)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write | FileShare.Delete))
            CompactBankFormat.PublishAppend(stream, generation, catalog);
        CompactBankReader.InvalidateCatalog(path);
        CompactIfWasteful(path);
    }

    // Append-only writes keep old blocks so a crash can never leave the active catalog half-written.
    // Reclaim that space once it is materially larger than the live data, rather than rewriting
    // large project banks after every small edit.
    private static void CompactIfWasteful(string path)
    {
        long fileBytes = new FileInfo(path).Length;
        long liveBytes;
        using (var reader = new CompactBankReader(path))
            liveBytes = reader.Catalog.Tables.Sum(table => table.Blocks.Sum(block => (long)block.Bytes));

        long reclaimable = Math.Max(0, fileBytes - liveBytes - CompactBankFormat.HeaderBytes);
        if (reclaimable < 8L * 1024 * 1024 || reclaimable < fileBytes / 10) return;
        CompactLocked(path, CancellationToken.None, null);
    }

    private static void ImportCore(string destination, IReadOnlyList<(string Source, string Dataset)> incoming, bool replace, CompactTable? replacedTable,
        CancellationToken cancellation = default, Action<long, long>? progress = null, IReadOnlyCollection<string>? removeDatasets = null, bool lockHeld = false)
    {
        destination = Path.GetFullPath(destination);
        if (incoming.Count == 0) return;
        using var lease = lockHeld ? null : SharedFileLock.Acquire(destination + ".write.lock", cancellation);
        if (incoming.Any(i => (string.IsNullOrWhiteSpace(i.Dataset) && replacedTable == null) || Path.GetFileName(i.Dataset) != i.Dataset)
            || incoming.Select(i => i.Dataset).Distinct(StringComparer.Ordinal).Count() != incoming.Count)
            throw new InvalidDataException("Identificadores de tabelas inválidos ou repetidos.");
        if (CompactBankFormat.IsAppendable(destination))
        {
            AppendImport(destination, incoming, replace, replacedTable, cancellation, progress, removeDatasets);
            return;
        }
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var catalogs = new Dictionary<string, CompactTable[]>(StringComparer.OrdinalIgnoreCase);
            string CatalogKey(string path)
            {
                var location = Location(path);
                return location.File + "\0" + location.Dataset;
            }
            CompactTable[] Catalog(string path)
            {
                string key = CatalogKey(path);
                if (catalogs.TryGetValue(key, out var cached)) return cached;
                using var reader = new CompactBankReader(path);
                return catalogs[key] = reader.Catalog.Tables;
            }
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                CompactBankFormat.WriteHeader(output);
                var tables = new List<CompactTable>();
                var usedIds = new HashSet<string>(StringComparer.Ordinal);
                var removedDatasets = (removeDatasets ?? []).ToHashSet(StringComparer.Ordinal);
                long totalBytes = 0, copiedBytes = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (File.Exists(destination))
                {
                    var existing = Catalog(destination);
                    foreach (var table in existing) usedIds.Add(table.Id);
                    totalBytes += existing.Where(t => replacedTable != null
                            ? t.Id != replacedTable.Id && !removedDatasets.Contains(t.Dataset)
                            : !incoming.Any(i => i.Dataset == t.Dataset) && !removedDatasets.Contains(t.Dataset))
                        .Sum(t => t.Blocks.Sum(b => (long)b.Bytes));
                    if (replacedTable != null && !existing.Any(t => SameVersion(t, replacedTable)))
                        throw new InvalidOperationException("A tabela foi alterada por outro usuário. Atualize antes de gravar.");
                }
                foreach (var item in incoming)
                    totalBytes += Catalog(item.Source).Sum(t => t.Blocks.Sum(b => (long)b.Bytes));
                progress?.Invoke(0, totalBytes);
                int nextId = 0;
                string NewId() { while (usedIds.Contains("t" + nextId)) nextId++; string id = "t" + nextId++; usedIds.Add(id); return id; }
                var datasets = incoming.Select(i => i.Dataset).ToHashSet(StringComparer.Ordinal);
                void Copy(string source, string? dataset)
                {
                    using var data = File.OpenRead(Location(source).File);
                    byte[] buffer = new byte[65536];
                    foreach (var table in Catalog(source))
                    {
                        bool removeExisting = replacedTable != null
                            ? table.Id == replacedTable.Id || removedDatasets.Contains(table.Dataset)
                            : datasets.Contains(table.Dataset) || removedDatasets.Contains(table.Dataset);
                        if (dataset == null && removeExisting) {
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
                CompactBankFormat.PublishCatalog(output, 0, new CompactCatalog(2, tables.ToArray()));
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
            CompactBankReader.InvalidateCatalog(destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void AppendImport(string destination, IReadOnlyList<(string Source, string Dataset)> incoming,
        bool replace, CompactTable? replacedTable, CancellationToken cancellation, Action<long, long>? progress,
        IReadOnlyCollection<string>? removeDatasets)
    {
        CompactTable[] existing;
        long generation;
        using (var reader = new CompactBankReader(destination))
        {
            existing = reader.Catalog.Tables;
            generation = reader.Generation;
        }
        var incomingDatasets = incoming.Select(item => item.Dataset).ToHashSet(StringComparer.Ordinal);
        if (replacedTable == null && !replace && existing.Any(table => incomingDatasets.Contains(table.Dataset)))
            throw new IOException("Já existe uma tabela com esse nome no banco do projeto.");
        if (replacedTable != null && !existing.Any(table => SameVersion(table, replacedTable)))
            throw new InvalidOperationException("A tabela foi alterada por outro usuário. Atualize antes de gravar.");

        var ids = existing.Select(table => table.Id).ToHashSet(StringComparer.Ordinal);
        int nextId = 0;
        string NewId()
        {
            while (ids.Contains("t" + nextId)) nextId++;
            string id = "t" + nextId++;
            ids.Add(id);
            return id;
        }
        var removedDatasets = (removeDatasets ?? []).ToHashSet(StringComparer.Ordinal);
        var tables = existing.Where(table =>
        {
            if (removedDatasets.Contains(table.Dataset)) return false;
            if (replacedTable != null) return table.Id != replacedTable.Id;
            return !replace || !incomingDatasets.Contains(table.Dataset);
        }).ToList();

        var sourceCatalogs = new List<(string Source, string Dataset, CompactTable[] Tables)>();
        long totalBytes = 0;
        foreach (var item in incoming)
        {
            using var reader = new CompactBankReader(item.Source);
            var selected = reader.Catalog.Tables;
            sourceCatalogs.Add((item.Source, item.Dataset, selected));
            totalBytes += selected.Sum(table => table.Blocks.Sum(block => (long)block.Bytes));
        }
        progress?.Invoke(0, totalBytes);

        using var output = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        output.Position = output.Length;
        byte[] buffer = new byte[65536];
        long copied = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var item in sourceCatalogs)
        {
            cancellation.ThrowIfCancellationRequested();
            var location = Location(item.Source);
            using var source = new FileStream(location.File, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete);
            foreach (var table in item.Tables)
            {
                var blocks = new List<CompactBlock>(table.Blocks.Length);
                foreach (var block in table.Blocks)
                {
                    source.Position = block.Offset;
                    long offset = output.Position;
                    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                        System.Security.Cryptography.HashAlgorithmName.SHA256);
                    int remaining = block.Bytes;
                    while (remaining > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new EndOfStreamException("Bloco incompleto no banco.");
                        hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                        remaining -= read;
                        copied += read;
                        if (watch.ElapsedMilliseconds >= 150)
                        {
                            progress?.Invoke(copied, totalBytes);
                            watch.Restart();
                        }
                    }
                    if (Convert.ToHexString(hash.GetHashAndReset()) != block.Hash)
                        throw new InvalidDataException("Bloco corrompido no banco de origem.");
                    blocks.Add(block with { Offset = offset });
                }
                string id = replacedTable != null ? replacedTable.Id : NewId();
                tables.Add(table with
                {
                    Id = id,
                    Dataset = item.Dataset,
                    Name = replacedTable != null ? replacedTable.Name : table.Name,
                    Blocks = blocks.ToArray(),
                });
            }
        }
        cancellation.ThrowIfCancellationRequested();
        output.Flush(true);
        CompactBankFormat.PublishAppend(output, generation + 1, new CompactCatalog(2, tables.ToArray()));
        output.Dispose();
        CompactBankReader.InvalidateCatalog(destination);
        progress?.Invoke(totalBytes, totalBytes);
        CompactIfWasteful(destination);
    }
}
