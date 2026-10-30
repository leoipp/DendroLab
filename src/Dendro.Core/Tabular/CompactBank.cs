using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dendro.Core.Tabular;

public sealed record CompactBlock(long Offset, int Bytes, long FirstRow, int Rows, string Hash);
public sealed record CompactTable(string Id, string Name, string[] Columns, long Rows, CompactBlock[] Blocks, string Dataset = "");
public sealed record CompactCatalog(int Version, CompactTable[] Tables);

/// <summary>DendroLab column-oriented, independently compressed blocks with a seekable index.</summary>
public sealed class CompactBankReader : IDisposable
{
    public const string Extension = ".dlb";
    internal static readonly byte[] Magic = "DNDRDB02"u8.ToArray();
    private readonly FileStream stream;
    public CompactCatalog Catalog { get; }

    public CompactBankReader(string path)
    {
        var location = CompactBankFile.Location(path);
        stream = File.Open(location.File, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (!reader.ReadBytes(8).SequenceEqual(Magic)) throw new InvalidDataException("Formato de banco DendroLab inválido.");
            long index = reader.ReadInt64();
            if (index < 16 || index > stream.Length - 4) throw new InvalidDataException("Índice incompleto no banco.");
            stream.Position = index;
            int length = reader.ReadInt32();
            if (length < 1 || length > 32 * 1024 * 1024 || length != stream.Length - stream.Position)
                throw new InvalidDataException("Índice inválido no banco.");
            Catalog = JsonSerializer.Deserialize<CompactCatalog>(reader.ReadBytes(length))
                ?? throw new InvalidDataException("Catálogo ausente.");
            if (Catalog.Version != 2 || Catalog.Tables.Length == 0) throw new InvalidDataException("Versão de banco não suportada.");
            foreach (var table in Catalog.Tables)
            {
                if (table.Columns.Length is < 1 or > 65536 || table.Rows < 0) throw new InvalidDataException("Tabela inválida.");
                long rows = 0;
                foreach (var block in table.Blocks)
                {
                    if (block.FirstRow != rows || block.Rows is < 1 or > 2048 || (long)block.Rows * table.Columns.Length > 1_000_000
                        || block.Bytes < 1 || block.Bytes > 64 * 1024 * 1024
                        || block.Offset < 16 || block.Offset > index - block.Bytes)
                        throw new InvalidDataException("Bloco inválido no banco.");
                    rows += block.Rows;
                }
                if (rows != table.Rows) throw new InvalidDataException("Contagem de linhas inconsistente.");
            }
            if (location.Dataset.Length > 0)
            {
                Catalog = Catalog with { Tables = Catalog.Tables.Where(t => t.Dataset == location.Dataset).ToArray() };
                if (Catalog.Tables.Length == 0) throw new InvalidDataException("Tabela de origem não encontrada no banco do projeto.");
            }
        }
        catch { stream.Dispose(); throw; }
    }

    public CompactTable Table(string name) => Catalog.Tables.FirstOrDefault(t => name.Length == 0 || t.Name == name || t.Id == name)
        ?? throw new InvalidDataException("Tabela não encontrada no banco do projeto.");

    public IEnumerable<object?[]> Rows(CompactTable table, long offset = 0, int? limit = null)
    {
        if (offset < 0 || limit < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        long emitted = 0;
        foreach (var block in table.Blocks)
        {
            if (block.FirstRow + block.Rows <= offset) continue;
            if (limit.HasValue && emitted >= limit) yield break;
            stream.Position = block.Offset;
            var bytes = new byte[block.Bytes]; stream.ReadExactly(bytes);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != block.Hash)
                throw new InvalidDataException("Bloco corrompido no banco do projeto.");
            using var compressed = new MemoryStream(bytes, false);
            using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
            using var reader = new BinaryReader(brotli, Encoding.UTF8);
            var rows = new object?[block.Rows][];
            for (int i = 0; i < rows.Length; i++) rows[i] = new object?[table.Columns.Length];
            // Columns are adjacent before compression, improving repeated-value compression.
            for (int c = 0; c < table.Columns.Length; c++)
                for (int r = 0; r < rows.Length; r++)
                    rows[r][c] = reader.ReadByte() switch {
                        0 => null, 1 => reader.ReadDouble(), 2 => reader.ReadString(),
                        _ => throw new InvalidDataException("Tipo de célula inválido.") };
            if (brotli.ReadByte() != -1) throw new InvalidDataException("Tamanho de bloco inconsistente.");
            for (int i = 0; i < rows.Length; i++)
            {
                if (block.FirstRow + i < offset) continue;
                if (limit.HasValue && emitted >= limit) yield break;
                emitted++; yield return rows[i];
            }
        }
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
    public CompactBankWriter(string path)
    {
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.Write(CompactBankReader.Magic);
        stream.Write(new byte[8]);
    }
    public string AddTable(string[] columns, string name = "Dados")
    {
        if (complete || columns.Length is < 1 or > 65536) throw new InvalidOperationException("Tabela inválida ou banco concluído.");
        string id = "t" + tables.Count;
        tables.Add(id, new(id, name, columns.ToArray()));
        return id;
    }
    public void Add(string id, object?[] row)
    {
        if (complete) throw new InvalidOperationException("Banco concluído.");
        var table = tables[id];
        if (row.Length != table.Columns.Length) throw new InvalidDataException("Largura da linha difere do cabeçalho.");
        var copy = new object?[row.Length];
        long size = 24L + row.Length * 8L;
        for (int c = 0; c < row.Length; c++)
        {
            copy[c] = row[c] switch {
                null => null,
                double n => double.IsFinite(n) ? n : null,
                float n => float.IsFinite(n) ? (double)n : null,
                int n => (double)n, long n => (double)n,
                var v => TableCursor.Text(v),
            };
            size += copy[c] is string s ? 24L + s.Length * 2L : 16;
        }
        if (size > 16 * 1024 * 1024) throw new InvalidDataException("Linha acima do limite de 16 MB do banco.");
        if (pendingBytes + size > 4 * 1024 * 1024) Flush();
        table.Pending.Add(copy); table.Rows++; pendingBytes += size;
        if (table.Pending.Count >= 2048) Flush();
    }
    private void Flush()
    {
        foreach (var table in tables.Values.Where(t => t.Pending.Count > 0))
        {
            using var bytes = new MemoryStream();
            using (var brotli = new BrotliStream(bytes, CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(brotli, Encoding.UTF8))
                for (int c = 0; c < table.Columns.Length; c++)
                    foreach (var row in table.Pending)
                        switch (row[c]) {
                            case null: writer.Write((byte)0); break;
                            case double n: writer.Write((byte)1); writer.Write(n); break;
                            case string s: writer.Write((byte)2); writer.Write(s); break;
                        }
            byte[] block = bytes.ToArray();
            table.Blocks.Add(new(stream.Position, block.Length, table.Rows - table.Pending.Count,
                table.Pending.Count, Convert.ToHexString(SHA256.HashData(block))));
            stream.Write(block); table.Pending.Clear();
        }
        pendingBytes = 0;
    }
    public void Complete()
    {
        if (complete || tables.Count == 0) throw new InvalidOperationException("Banco vazio ou concluído.");
        Flush();
        long offset = stream.Position;
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new CompactCatalog(2, tables.Values.Select(t =>
            new CompactTable(t.Id, t.Name, t.Columns, t.Rows, t.Blocks.ToArray())).ToArray()));
        if (catalog.Length > 32 * 1024 * 1024) throw new InvalidDataException("Catálogo acima de 32 MB.");
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(catalog.Length); writer.Write(catalog); stream.Position = 8; writer.Write(offset);
        stream.Flush(true); complete = true;
    }
    public void Dispose() => stream.Dispose();
}
