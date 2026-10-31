using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using Dendro.Core.Spreadsheets;
using ExcelDataReader;
using LiteDB;

namespace Dendro.Core.Tabular;

public sealed record TableSource(string Path, string Table = "", int HeaderRow = 1);

public sealed record BankTable(string Name, string Collection, string[] Columns, long Rows);

public sealed record TablePage(DataTable Data, long Total, long Offset);

/// <summary>Forward-only table cursor. Excel and database rows share the same cell types.</summary>
public sealed class TableCursor : IDisposable
{
    private readonly IExcelDataReader? excel;
    private readonly LiteDatabase? bank;
    private readonly BankTable? table;
    private readonly CompactBankReader? compact;
    private readonly CompactTable? compactTable;
    private readonly int compactHeaderSkip;
    public string[] Columns { get; }
    public long? TotalRows { get; }

    public TableCursor(TableSource source)
    {
        if (System.IO.Path.GetExtension(source.Path).Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase))
        {
            compact = new CompactBankReader(source.Path);
            try {
                compactTable = compact.Table(source.Table);
                Columns = compactTable.Columns;
                if (source.HeaderRow < 1 || source.HeaderRow > compactTable.Rows + 1)
                    throw new InvalidDataException("Linha de cabeçalho ausente no banco.");
                compactHeaderSkip = source.HeaderRow - 1;
                if (compactHeaderSkip > 0)
                    Columns = compact.Rows(compactTable, compactHeaderSkip - 1, 1).First().Select(Text).ToArray();
                TotalRows = compactTable.Rows - compactHeaderSkip;
            } catch { compact.Dispose(); throw; }
        }
        else if (TableStore.IsBank(source.Path))
        {
            bank = TableStore.OpenBank(source.Path);
            try
            {
                table =
                    TableStore
                        .Catalog(bank)
                        .FirstOrDefault(t =>
                            source.Table.Length == 0
                            || t.Name == source.Table
                            || t.Collection == source.Table
                        )
                    ?? throw new InvalidDataException("Tabela não encontrada no banco DENDRO.");
                Columns = table.Columns;
                TotalRows = table.Rows;
            }
            catch
            {
                bank.Dispose();
                throw;
            }
        }
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var stream = File.Open(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            try
            {
                excel = ExcelReaderFactory.CreateReader(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            try
            {
                if (source.Table.Length > 0)
                    while (excel.Name != source.Table)
                        if (!excel.NextResult())
                            throw new InvalidDataException(
                                $"Worksheet não encontrada: {source.Table}."
                            );
                if (source.HeaderRow < 1 || source.HeaderRow > 1048576)
                    throw new InvalidDataException("Cabeçalho inválido.");
                for (int r = 0; r < source.HeaderRow; r++)
                    if (!excel.Read())
                        throw new InvalidDataException("Linha de cabeçalho ausente.");
                Columns = Enumerable
                    .Range(0, excel.FieldCount)
                    .Select(c => Text(ConvertCell(excel.GetValue(c))))
                    .ToArray();
            }
            catch
            {
                excel.Dispose();
                throw;
            }
        }
    }

    public IEnumerable<object?[]> Rows(long offset = 0, int? limit = null)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (compact != null)
        {
            foreach (var row in compact.Rows(compactTable!, offset + compactHeaderSkip, limit)) yield return row;
        }
        else if (bank != null)
        {
            var collection = bank.GetCollection<BsonDocument>(table!.Collection);
            foreach (
                var doc in collection.Find(
                    Query.GT("_id", new BsonValue(offset)),
                    limit: limit ?? int.MaxValue
                )
            )
                yield return Enumerable
                    .Range(0, Columns.Length)
                    .Select(c => FromBson(doc["c" + c]))
                    .ToArray();
        }
        else
        {
            long skipped = 0;
            int emitted = 0;
            while (!limit.HasValue || emitted < limit.Value)
            {
                if (!excel!.Read()) yield break;
                bool capture = skipped >= offset;
                var row = capture ? new object?[Columns.Length] : null;
                bool blank = true;
                for (int c = 0; c < Columns.Length; c++)
                {
                    object? value = ConvertCell(excel.GetValue(c));
                    if (value is not null && value is not string { Length: 0 }) blank = false;
                    if (row != null) row[c] = value;
                }
                if (blank)
                    continue;
                if (skipped++ < offset)
                    continue;
                emitted++;
                yield return row!;
            }
        }
    }

    /// <summary>Reads one Excel page while counting nonblank rows in the same forward scan.</summary>
    internal TablePage PageAndCountExcel(long offset, int limit)
    {
        if (excel == null) throw new InvalidOperationException("A contagem em uma passagem só se aplica a planilhas.");
        if (offset < 0 || limit < 1) throw new ArgumentOutOfRangeException(nameof(offset));
        var page = new DataTable(Columns);
        long total = 0;
        while (excel.Read())
        {
            bool capture = total >= offset && total - offset < limit;
            var row = capture ? new object?[Columns.Length] : null;
            bool blank = true;
            for (int c = 0; c < Columns.Length; c++)
            {
                object? value = ConvertCell(excel.GetValue(c));
                if (value is not null && value is not string { Length: 0 }) blank = false;
                if (row != null) row[c] = value;
            }
            if (blank) continue;
            if (row != null) page.Add(row);
            total++;
        }
        return new(page, total, offset);
    }

    public static string Text(object? value) =>
        value is IFormattable f
            ? f.ToString(null, CultureInfo.InvariantCulture) ?? ""
            : value?.ToString() ?? "";

    private static object? ConvertCell(object? value) =>
        value switch
        {
            DateTime date => date.ToOADate(),
            int n => (double)n,
            bool b => b ? "TRUE" : "FALSE",
            var v => v,
        };

    private static object? FromBson(BsonValue v) =>
        v.IsNull ? null
        : v.IsNumber ? v.AsDouble
        : v.IsBoolean
            ? v.AsBoolean ? "TRUE"
                : "FALSE"
        : v.AsString;

    public void Dispose()
    {
        excel?.Dispose();
        bank?.Dispose();
        compact?.Dispose();
    }
}

public static class TableStore
{
    public const string Extension = ".dendrodb";
    private static readonly ConcurrentDictionary<string, long> ExcelRowCounts = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBank(string path) =>
        new[] { Extension, ".db", CompactBankReader.Extension }.Contains(
            System.IO.Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase
        );

    internal static LiteDatabase OpenBank(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Banco não encontrado.", path);
        var db = new LiteDatabase(
            new ConnectionString { Filename = System.IO.Path.GetFullPath(path), ReadOnly = true }
        );
        if (
            db.GetCollection<BsonDocument>("dendro_info").FindById("format")?["value"].AsString
            != "DENDRO_TABLES_1"
        )
        {
            db.Dispose();
            throw new InvalidDataException("Este arquivo não é um banco DENDRO compatível.");
        }
        return db;
    }

    internal static BankTable[] Catalog(LiteDatabase db) =>
        db.GetCollection<BsonDocument>("dendro_tables")
            .FindAll()
            .Select(d => new BankTable(
                d["name"].AsString,
                d["collection"].AsString,
                d["columns"].AsArray.Select(c => c.AsString).ToArray(),
                d["rows"].AsInt64
            ))
            .ToArray();

    public static string[] Tables(string path)
    {
        if (System.IO.Path.GetExtension(path).Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new CompactBankReader(path);
            return reader.Catalog.Tables.Select(t => t.Name)
                .Where(name => !name.StartsWith(Dendro.Core.Processing.ProcessingHistory.InternalPrefix, StringComparison.Ordinal)).ToArray();
        }
        if (!IsBank(path))
            return SpreadsheetReader.Sheets(path);
        using var db = OpenBank(path);
        return Catalog(db).Select(t => t.Name).ToArray();
    }

    public static TablePage Page(TableSource source, long offset = 0, int limit = 500)
    {
        if (limit is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var cursor = new TableCursor(source);
        long total = cursor.TotalRows ?? -1;
        string? key = null;
        if (total < 0)
        {
            key = ExcelCountKey(source);
            if (!ExcelRowCounts.TryGetValue(key, out total))
            {
                var countedPage = cursor.PageAndCountExcel(offset, limit);
                if (ExcelRowCounts.Count > 256) ExcelRowCounts.Clear();
                ExcelRowCounts[key] = countedPage.Total;
                return countedPage;
            }
        }
        var data = new DataTable(cursor.Columns);
        foreach (var row in cursor.Rows(offset, limit))
            data.Add(row);
        return new(data, total, offset);
    }

    private static string ExcelCountKey(TableSource source)
    {
        var info = new FileInfo(source.Path);
        return string.Join("|", Path.GetFullPath(source.Path), info.Length, info.LastWriteTimeUtc.Ticks,
            source.Table, source.HeaderRow.ToString(CultureInfo.InvariantCulture));
    }

    public static DataTable Read(TableSource source)
    {
        using var cursor = new TableCursor(source);
        var result = new DataTable(cursor.Columns);
        foreach (var row in cursor.Rows())
            result.Add(row);
        return result;
    }
}
