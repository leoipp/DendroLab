using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers.Binary;

namespace Dendro.Core.Tabular;

public sealed record CompactBlock(long Offset, int Bytes, long FirstRow, int Rows, string Hash, CompactSegment[]? Segments = null);
public sealed record CompactSegment(int FirstColumn, int ColumnCount, int Offset, int Bytes, string Hash);
public sealed record CompactTable(string Id, string Name, string[] Columns, long Rows, CompactBlock[] Blocks, string Dataset = "");
public sealed record CompactCatalog(int Version, CompactTable[] Tables);

internal readonly record struct CompactRoot(long Generation, long CatalogOffset, int CatalogBytes, byte[] CatalogHash);

internal static class CompactBankFormat
{
    internal static readonly byte[] LegacyMagic = "DNDRDB02"u8.ToArray();
    internal static readonly byte[] PreviousMagic = "DNDRDB03"u8.ToArray();
    internal static readonly byte[] Magic = "DNDRDB04"u8.ToArray();
    internal const int HeaderBytes = 184;
    private const int RootBytes = 88;
    private const int RootSlotsOffset = 8;

    internal static bool IsAppendable(string path)
    {
        if (!File.Exists(path)) return false;
        Span<byte> magic = stackalloc byte[8];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
        try { stream.ReadExactly(magic); return magic.SequenceEqual(PreviousMagic) || magic.SequenceEqual(Magic); }
        catch (EndOfStreamException) { return false; }
    }

    internal static void WriteHeader(Stream stream)
    {
        stream.Write(Magic);
        stream.Write(new byte[HeaderBytes - Magic.Length]);
    }

    internal static CompactRoot PublishCatalog(FileStream stream, long generation, CompactCatalog catalog)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(catalog);
        if (json.Length is < 1 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Catálogo acima de 32 MB.");
        long offset = stream.Length;
        stream.Position = offset;
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(json.Length);
            writer.Write(json);
            writer.Flush();
        }
        stream.Flush(true);
        byte[] catalogHash = SHA256.HashData(json);
        PublishRoot(stream, generation, offset, json.Length, catalogHash);
        return new(generation, offset, json.Length, catalogHash);
    }

    internal static CompactRoot PublishAppend(FileStream stream, long generation, CompactCatalog catalog)
    {
        bool hasSegmentedBlocks = catalog.Tables.Any(table => table.Blocks.Any(block => block.Segments is { Length: > 0 }));
        if (hasSegmentedBlocks)
        {
            Span<byte> magic = stackalloc byte[8];
            stream.Position = 0;
            stream.ReadExactly(magic);
            if (magic.SequenceEqual(PreviousMagic))
            {
                stream.Position = 0;
                stream.Write(Magic);
                stream.Flush(true);
            }
        }
        return PublishCatalog(stream, generation, catalog);
    }

    internal static void PublishRoot(FileStream stream, long generation, long offset, int catalogBytes, ReadOnlySpan<byte> catalogHash)
    {
        if (catalogHash.Length != 32) throw new ArgumentException("SHA-256 do catálogo inválido.", nameof(catalogHash));
        byte[] root = new byte[RootBytes];
        BinaryPrimitives.WriteInt64LittleEndian(root.AsSpan(0, 8), generation);
        BinaryPrimitives.WriteInt64LittleEndian(root.AsSpan(8, 8), offset);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(16, 4), catalogBytes);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(20, 4), 0);
        catalogHash.CopyTo(root.AsSpan(24, 32));
        SHA256.HashData(root.AsSpan(0, 56)).CopyTo(root.AsSpan(56, 32));
        int slot = (int)(generation & 1);
        stream.Position = RootSlotsOffset + (long)slot * RootBytes;
        stream.Write(root);
        stream.Flush(true);
    }

    internal static (CompactCatalog Catalog, CompactRoot Root) ReadLatestCatalog(FileStream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var candidates = new List<CompactRoot>(2);
        for (int slot = 0; slot < 2; slot++)
        {
            byte[] bytes = new byte[RootBytes];
            stream.Position = RootSlotsOffset + (long)slot * RootBytes;
            try { stream.ReadExactly(bytes); }
            catch (EndOfStreamException) { continue; }
            if (!SHA256.HashData(bytes.AsSpan(0, 56)).AsSpan().SequenceEqual(bytes.AsSpan(56, 32))) continue;
            long generation = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, 8));
            long offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8, 8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4));
            if (generation < 0 || offset < HeaderBytes || length is < 1 or > 32 * 1024 * 1024
                || offset > stream.Length - 4 - length) continue;
            candidates.Add(new(generation, offset, length, bytes.AsSpan(24, 32).ToArray()));
        }

        foreach (var root in candidates.OrderByDescending(candidate => candidate.Generation))
        {
            try
            {
                stream.Position = root.CatalogOffset;
                if (reader.ReadInt32() != root.CatalogBytes) continue;
                byte[] json = reader.ReadBytes(root.CatalogBytes);
                if (json.Length != root.CatalogBytes) continue;
                if (!SHA256.HashData(json).AsSpan().SequenceEqual(root.CatalogHash)) continue;
                var catalog = JsonSerializer.Deserialize<CompactCatalog>(json);
                if (catalog == null || catalog.Version != 2 || catalog.Tables is not { Length: > 0 }) continue;
                CompactBankReader.ValidateCatalog(catalog, root.CatalogOffset, HeaderBytes);
                return (catalog, root);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or OverflowException or NullReferenceException)
            {
                // A partially written newest catalog/root falls back to the prior valid generation.
            }
        }
        throw new InvalidDataException("O banco DendroLab não contém um índice válido recuperável.");
    }
}

/// <summary>DendroLab column-oriented, independently compressed blocks with a seekable index.</summary>
public sealed class CompactBankReader : IDisposable
{
    public const string Extension = ".dlb";
    public const int MaximumBlockBytes = 320 * 1024 * 1024;
    public const long MaximumRowBytes = 256L * 1024 * 1024;
    internal static readonly byte[] Magic = CompactBankFormat.Magic;
    private sealed record CachedCatalog(long Length, long LastWriteUtcTicks, CompactCatalog Catalog, long Generation, long EstimatedBytes, long CacheId);
    private static readonly ConcurrentDictionary<string, CachedCatalog> CatalogCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CatalogCacheLock = new();
    private static readonly Queue<(string Path, long CacheId)> CatalogCacheOrder = new();
    private static long catalogCacheBytes;
    private static long catalogCacheSequence;
    private const int MaximumCachedCatalogs = 128;
    private const long MaximumCatalogCacheBytes = 256L * 1024 * 1024;
    private readonly FileStream stream;
    /// <summary>Immutable catalog snapshot. Nested arrays are shared with the bounded catalog cache.</summary>
    public CompactCatalog Catalog { get; }
    internal long Generation { get; }

    private sealed class HashedBlockStream : Stream
    {
        private readonly FileStream source;
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash? aggregateHash;
        private int remaining;
        public HashedBlockStream(FileStream source, int length, IncrementalHash? aggregateHash = null)
        {
            this.source = source;
            remaining = length;
            this.aggregateHash = aggregateHash;
        }
        public byte[] Finish()
        {
            if (remaining != 0) throw new EndOfStreamException("Bloco incompleto no banco.");
            return hash.GetHashAndReset();
        }
        public void Drain()
        {
            Span<byte> buffer = stackalloc byte[8192];
            while (remaining > 0)
            {
                int read = Read(buffer);
                if (read == 0) throw new EndOfStreamException("Bloco incompleto no banco.");
            }
        }
        public override int Read(Span<byte> buffer)
        {
            if (remaining == 0 || buffer.Length == 0) return 0;
            int read = source.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            if (read == 0) throw new EndOfStreamException("Bloco incompleto no banco.");
            hash.AppendData(buffer[..read]);
            aggregateHash?.AppendData(buffer[..read]);
            remaining -= read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static void InvalidateCatalog(string path)
    {
        string key = Path.GetFullPath(path);
        lock (CatalogCacheLock)
            if (CatalogCache.TryRemove(key, out var removed)) catalogCacheBytes -= removed.EstimatedBytes;
    }

    private static long EstimateCatalogBytes(CompactCatalog catalog)
    {
        long bytes = 256;
        foreach (var table in catalog.Tables)
        {
            bytes += 128L + table.Blocks.LongLength * 112L;
            foreach (string column in table.Columns) bytes += 40L + column.Length * 2L;
            foreach (var block in table.Blocks) bytes += (block.Segments?.LongLength ?? 0) * 80L;
        }
        return bytes;
    }

    public CompactBankReader(string path)
    {
        var location = CompactBankFile.Location(path);
        stream = File.Open(location.File, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
        try
        {
            string catalogPath = Path.GetFullPath(location.File);
            var file = new FileInfo(catalogPath);
            long length = file.Length, lastWrite = file.LastWriteTimeUtc.Ticks;
            CompactCatalog? fullCatalog = null;
            long generation = 0;
            if (CatalogCache.TryGetValue(catalogPath, out var cached)
                && cached.Length == length && cached.LastWriteUtcTicks == lastWrite)
            {
                fullCatalog = cached.Catalog;
                generation = cached.Generation;
            }
            else
            {
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                byte[] magic = reader.ReadBytes(8);
                if (magic.SequenceEqual(CompactBankFormat.LegacyMagic))
                {
                    long index = reader.ReadInt64();
                    if (index < 16 || index > stream.Length - 4) throw new InvalidDataException("Índice incompleto no banco.");
                    stream.Position = index;
                    int indexLength = reader.ReadInt32();
                    if (indexLength < 1 || indexLength > 32 * 1024 * 1024 || indexLength != stream.Length - stream.Position)
                        throw new InvalidDataException("Índice inválido no banco.");
                    fullCatalog = JsonSerializer.Deserialize<CompactCatalog>(reader.ReadBytes(indexLength))
                        ?? throw new InvalidDataException("Catálogo ausente.");
                    ValidateCatalog(fullCatalog, index, 16);
                }
                else if (magic.SequenceEqual(CompactBankFormat.PreviousMagic)
                    || magic.SequenceEqual(CompactBankFormat.Magic))
                {
                    var snapshot = CompactBankFormat.ReadLatestCatalog(stream);
                    fullCatalog = snapshot.Catalog;
                    generation = snapshot.Root.Generation;
                }
                else throw new InvalidDataException("Formato de banco DendroLab inválido.");
                long estimatedBytes = EstimateCatalogBytes(fullCatalog);
                lock (CatalogCacheLock)
                {
                    if (CatalogCache.TryRemove(catalogPath, out var previous))
                        catalogCacheBytes -= previous.EstimatedBytes;
                    if (estimatedBytes <= MaximumCatalogCacheBytes)
                    {
                        var entry = new CachedCatalog(length, lastWrite, fullCatalog, generation, estimatedBytes,
                            ++catalogCacheSequence);
                        CatalogCache[catalogPath] = entry;
                        CatalogCacheOrder.Enqueue((catalogPath, entry.CacheId));
                        catalogCacheBytes += estimatedBytes;
                        while (CatalogCache.Count > MaximumCachedCatalogs || catalogCacheBytes > MaximumCatalogCacheBytes)
                        {
                            if (CatalogCacheOrder.Count == 0) break;
                            var oldest = CatalogCacheOrder.Dequeue();
                            if (CatalogCache.TryGetValue(oldest.Path, out var current)
                                && current.CacheId == oldest.CacheId
                                && CatalogCache.TryRemove(oldest.Path, out var removed))
                                catalogCacheBytes -= removed.EstimatedBytes;
                        }
                    }
                }
            }
            Generation = generation;
            // Catalogs can contain tens of thousands of block descriptors for very large tables.
            // They are immutable after parsing, so share the cached snapshot instead of copying
            // every table, column, block, and segment each time a reader is opened.
            Catalog = fullCatalog;
            if (location.Dataset.Length > 0)
            {
                Catalog = Catalog with { Tables = Catalog.Tables.Where(t => t.Dataset == location.Dataset).ToArray() };
                if (Catalog.Tables.Length == 0) throw new InvalidDataException("Tabela de origem não encontrada no banco do projeto.");
            }
        }
        catch { stream.Dispose(); throw; }
    }

    internal static void ValidateCatalog(CompactCatalog catalog, long index, long minimumOffset)
    {
        if (catalog.Version != 2 || catalog.Tables is not { Length: > 0 }) throw new InvalidDataException("Versão de banco não suportada.");
        foreach (var table in catalog.Tables)
        {
            if (table == null || table.Columns is not { Length: >= 1 and <= 65536 } || table.Blocks == null || table.Rows < 0)
                throw new InvalidDataException("Tabela inválida.");
            long rows = 0;
            foreach (var block in table.Blocks)
            {
                if (block == null || block.FirstRow != rows || block.Rows is < 1 or > 2048 || (long)block.Rows * table.Columns.Length > 1_000_000
                    || block.Bytes < 1 || block.Bytes > MaximumBlockBytes
                    || block.Offset < minimumOffset || block.Offset > index - block.Bytes)
                    throw new InvalidDataException("Bloco inválido no banco.");
                if (block.Segments is { } segments)
                {
                    int expectedColumn = 0, expectedOffset = 0;
                    if (segments.Length == 0) throw new InvalidDataException("Segmentos de coluna ausentes no bloco.");
                    foreach (var segment in segments)
                    {
                        if (segment == null || segment.FirstColumn != expectedColumn || segment.ColumnCount < 1
                            || segment.FirstColumn > table.Columns.Length - segment.ColumnCount
                            || segment.Offset != expectedOffset || segment.Bytes < 1
                            || segment.Offset > block.Bytes - segment.Bytes
                            || segment.Hash.Length != 64 || !segment.Hash.All(Uri.IsHexDigit))
                            throw new InvalidDataException("Segmento de coluna inválido no banco.");
                        expectedColumn += segment.ColumnCount;
                        expectedOffset += segment.Bytes;
                    }
                    if (expectedColumn != table.Columns.Length || expectedOffset != block.Bytes)
                        throw new InvalidDataException("Cobertura de segmentos inconsistente no banco.");
                }
                rows += block.Rows;
            }
            if (rows != table.Rows) throw new InvalidDataException("Contagem de linhas inconsistente.");
        }
    }

    public CompactTable Table(string name) => Catalog.Tables.FirstOrDefault(t => name.Length == 0 || t.Name == name || t.Id == name)
        ?? throw new InvalidDataException("Tabela não encontrada no banco do projeto.");

    public IEnumerable<object?[]> Rows(CompactTable table, long offset = 0, int? limit = null)
        => ProjectedRows(table, null, offset, limit);

    public IEnumerable<object?[]> ProjectedRows(
        CompactTable table,
        IReadOnlyList<int>? selectedColumns,
        long offset = 0,
        int? limit = null
    )
    {
        if (offset < 0 || limit < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        int[] sourceColumns = selectedColumns?.ToArray() ?? Enumerable.Range(0, table.Columns.Length).ToArray();
        if (sourceColumns.Length == 0 || sourceColumns.Any(column => column < 0 || column >= table.Columns.Length)
            || sourceColumns.Distinct().Count() != sourceColumns.Length)
            throw new InvalidDataException("Projeção de colunas inválida para o banco.");
        int[] outputColumn = new int[table.Columns.Length];
        Array.Fill(outputColumn, -1);
        for (int i = 0; i < sourceColumns.Length; i++) outputColumn[sourceColumns[i]] = i;
        byte[] skipBuffer = new byte[8192];
        long emitted = 0;
        foreach (var block in table.Blocks)
        {
            if (block.FirstRow + block.Rows <= offset) continue;
            if (limit.HasValue && emitted >= limit) yield break;
            stream.Position = block.Offset;
            using var compressed = new HashedBlockStream(stream, block.Bytes);
            using var brotli = new BrotliStream(compressed, CompressionMode.Decompress, true);
            using var reader = new BinaryReader(brotli, Encoding.UTF8, true);
            int firstRequested = (int)Math.Max(0, offset - block.FirstRow);
            int requested = block.Rows - firstRequested;
            if (limit.HasValue) requested = Math.Min(requested, limit.Value - (int)emitted);
            var rows = new object?[requested][];
            for (int i = 0; i < rows.Length; i++) rows[i] = new object?[sourceColumns.Length];
            if (block.Segments is { } segments)
            {
                using var aggregateHash = sourceColumns.Length == table.Columns.Length
                    ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                    : null;
                foreach (var segment in segments)
                {
                    bool needed = false;
                    for (int column = segment.FirstColumn; column < segment.FirstColumn + segment.ColumnCount; column++)
                        if (outputColumn[column] >= 0) { needed = true; break; }
                    if (!needed) continue;
                    stream.Position = block.Offset + segment.Offset;
                    using var segmentStream = new HashedBlockStream(stream, segment.Bytes, aggregateHash);
                    using var segmentBrotli = new BrotliStream(segmentStream, CompressionMode.Decompress, true);
                    using var segmentReader = new BinaryReader(segmentBrotli, Encoding.UTF8, true);
                    int lastColumn = segment.FirstColumn + segment.ColumnCount;
                    for (int column = segment.FirstColumn; column < lastColumn; column++)
                    {
                        int targetColumn = outputColumn[column];
                        for (int row = 0; row < block.Rows; row++)
                        {
                            int type = segmentReader.ReadByte();
                            object? value = type switch {
                                0 => null,
                                1 => segmentReader.ReadDouble(),
                                2 when targetColumn >= 0 => segmentReader.ReadString(),
                                2 => SkipString(segmentReader, skipBuffer),
                                _ => throw new InvalidDataException("Tipo de célula inválido.") };
                            if (targetColumn >= 0 && row >= firstRequested && row < firstRequested + requested)
                                rows[row - firstRequested][targetColumn] = value;
                        }
                    }
                    if (segmentBrotli.ReadByte() != -1) throw new InvalidDataException("Tamanho de segmento inconsistente.");
                    segmentStream.Drain();
                    if (!CryptographicOperations.FixedTimeEquals(segmentStream.Finish(), Convert.FromHexString(segment.Hash)))
                        throw new InvalidDataException("Segmento de coluna corrompido no banco do projeto.");
                }
                if (aggregateHash != null
                    && !CryptographicOperations.FixedTimeEquals(aggregateHash.GetHashAndReset(), Convert.FromHexString(block.Hash)))
                    throw new InvalidDataException("Bloco corrompido no banco do projeto.");
                for (int i = 0; i < requested; i++)
                {
                    emitted++; yield return rows[i];
                }
                continue;
            }
            // Columns are adjacent before compression, improving repeated-value compression.
            for (int c = 0; c < table.Columns.Length; c++)
                for (int r = 0; r < block.Rows; r++)
                {
                    int type = reader.ReadByte();
                    int targetColumn = outputColumn[c];
                    object? value = type switch {
                        0 => null,
                        1 => reader.ReadDouble(),
                        2 when targetColumn >= 0 => reader.ReadString(),
                        2 => SkipString(reader, skipBuffer),
                        _ => throw new InvalidDataException("Tipo de célula inválido.") };
                    if (targetColumn >= 0 && r >= firstRequested && r < firstRequested + requested)
                        rows[r - firstRequested][targetColumn] = value;
                }
            if (brotli.ReadByte() != -1) throw new InvalidDataException("Tamanho de bloco inconsistente.");
            compressed.Drain();
            if (!CryptographicOperations.FixedTimeEquals(compressed.Finish(), Convert.FromHexString(block.Hash)))
                throw new InvalidDataException("Bloco corrompido no banco do projeto.");
            for (int i = 0; i < requested; i++)
            {
                emitted++; yield return rows[i];
            }
        }
    }

    private static object? SkipString(BinaryReader reader, byte[] buffer)
    {
        int remaining = reader.Read7BitEncodedInt();
        if (remaining < 0 || remaining > MaximumRowBytes)
            throw new InvalidDataException("Texto inválido no bloco do banco.");
        while (remaining > 0)
        {
            int read = reader.BaseStream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("Texto incompleto no bloco do banco.");
            remaining -= read;
        }
        return null;
    }
    public void Dispose() => stream.Dispose();
}

public sealed class CompactBankWriter : IDisposable
{
    private sealed class State(string id, string name, string[] columns)
    {
        public readonly string Id = id, Name = name;
        public readonly string[] Columns = columns;
        public long Rows;
        public readonly List<object?[]> Pending = [];
        public readonly List<CompactBlock> Blocks = [];
    }
    private readonly FileStream stream;
    private readonly Dictionary<string, State> tables = new();
    private long pendingBytes;
    private bool complete;
    private const int ColumnsPerSegment = 8;
    public CompactBankWriter(string path)
    {
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        CompactBankFormat.WriteHeader(stream);
    }
    public string AddTable(string[] columns, string name = "Dados")
    {
        if (complete || columns.Length is < 1 or > 65536) throw new InvalidOperationException("Tabela inválida ou banco concluído.");
        string id = "t" + tables.Count;
        tables.Add(id, new(id, name, columns.ToArray()));
        return id;
    }
    public void Add(string id, object?[] row)
        => AddCore(id, row, takeOwnership: false);

    /// <summary>Transfers a disposable row array to the writer without allocating a second row.</summary>
    public void AddOwned(string id, object?[] row)
        => AddCore(id, row, takeOwnership: true);

    private void AddCore(string id, object?[] row, bool takeOwnership)
    {
        if (complete) throw new InvalidOperationException("Banco concluído.");
        var table = tables[id];
        if (row.Length != table.Columns.Length) throw new InvalidDataException("Largura da linha difere do cabeçalho.");
        object?[] stored = takeOwnership ? row : new object?[row.Length];
        long size = 24L + row.Length * 8L;
        int oversizedColumn = -1;
        for (int c = 0; c < row.Length; c++)
        {
            object? value = row[c] switch {
                null => null,
                double n => double.IsFinite(n) ? n : null,
                float n => float.IsFinite(n) ? (double)n : null,
                int n => (double)n, long n => (double)n,
                var v => TableCursor.Text(v),
            };
            stored[c] = value;
            if (value is string s)
            {
                size += 16L + Encoding.UTF8.GetByteCount(s);
                if (size > CompactBankReader.MaximumRowBytes) oversizedColumn = c;
            }
            else size += 16;
        }
        if (size > CompactBankReader.MaximumRowBytes)
            throw new InvalidDataException($"A linha excede o limite técnico de 256 MiB do DLB (coluna '{table.Columns[Math.Max(0, oversizedColumn)]}'). Reduza o conteúdo dessa coluna ou divida o registro.");
        if (pendingBytes + size > 4 * 1024 * 1024) Flush();
        table.Pending.Add(stored); table.Rows++; pendingBytes += size;
        int blockRowLimit = Math.Min(2048, Math.Max(1, 1_000_000 / table.Columns.Length));
        if (table.Pending.Count >= blockRowLimit) Flush();
    }
    private void Flush()
    {
        foreach (var table in tables.Values.Where(t => t.Pending.Count > 0))
        {
            long blockOffset = stream.Position;
            using var blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int segmentCount = (table.Columns.Length + ColumnsPerSegment - 1) / ColumnsPerSegment;
            var payloads = new byte[segmentCount][];
            var hashes = new string[segmentCount];
            Parallel.For(0, segmentCount, segmentIndex =>
            {
                int firstColumn = segmentIndex * ColumnsPerSegment;
                int columnCount = Math.Min(ColumnsPerSegment, table.Columns.Length - firstColumn);
                using var compressed = new MemoryStream();
                using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, true))
                using (var writer = new BinaryWriter(brotli, Encoding.UTF8, true))
                    for (int column = firstColumn; column < firstColumn + columnCount; column++)
                        foreach (var row in table.Pending)
                            switch (row[column]) {
                                case null: writer.Write((byte)0); break;
                                case double n: writer.Write((byte)1); writer.Write(n); break;
                                case string s: writer.Write((byte)2); writer.Write(s); break;
                            }
                byte[] bytes = compressed.ToArray();
                payloads[segmentIndex] = bytes;
                hashes[segmentIndex] = Convert.ToHexString(SHA256.HashData(bytes));
            });
            var segments = new List<CompactSegment>(segmentCount);
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                int firstColumn = segmentIndex * ColumnsPerSegment;
                int columnCount = Math.Min(ColumnsPerSegment, table.Columns.Length - firstColumn);
                int segmentOffset = checked((int)(stream.Position - blockOffset));
                stream.Write(payloads[segmentIndex]);
                blockHash.AppendData(payloads[segmentIndex]);
                segments.Add(new(firstColumn, columnCount, segmentOffset, payloads[segmentIndex].Length,
                    hashes[segmentIndex]));
            }
            int blockBytes = checked((int)(stream.Position - blockOffset));
            table.Blocks.Add(new(blockOffset, blockBytes, table.Rows - table.Pending.Count,
                table.Pending.Count, Convert.ToHexString(blockHash.GetHashAndReset()), segments.ToArray()));
            table.Pending.Clear();
        }
        pendingBytes = 0;
    }
    public void Complete()
    {
        if (complete || tables.Count == 0) throw new InvalidOperationException("Banco vazio ou concluído.");
        Flush();
        var catalog = new CompactCatalog(2, tables.Values.Select(t =>
            new CompactTable(t.Id, t.Name, t.Columns, t.Rows, t.Blocks.ToArray())).ToArray());
        CompactBankFormat.PublishCatalog(stream, 0, catalog);
        complete = true;
    }
    public void Dispose() => stream.Dispose();
}
