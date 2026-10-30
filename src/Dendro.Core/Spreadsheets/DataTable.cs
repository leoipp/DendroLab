using System.Globalization;
using System.Text.Json.Nodes;

namespace Dendro.Core.Spreadsheets;

/// <summary>Worksheet values retain their Excel numeric or text type; row zero is the header.</summary>
public sealed class DataTable
{
    public List<List<object?>> Rows { get; } = [];
    public int Count => Rows.Count;
    public int Width => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    public DataTable() { }

    public DataTable(IEnumerable<string> columns) => Add(columns.Cast<object?>().ToArray());

    public void Add(params object?[] cells) => Rows.Add([.. cells]);

    public object? Value(int row, int column) =>
        row >= 0 && row < Rows.Count && column >= 0 && column < Rows[row].Count
            ? Rows[row][column]
            : null;

    public string Text(int row, int column) =>
        Value(row, column) switch
        {
            double n when double.IsFinite(n) => n.ToString("G17", CultureInfo.InvariantCulture),
            float n => n.ToString("G9", CultureInfo.InvariantCulture),
            IFormattable v => v.ToString(null, CultureInfo.InvariantCulture) ?? "",
            var v => v?.ToString() ?? "",
        };

    public double Number(int row, int column)
    {
        var text = Text(row, column).Trim();
        return double.TryParse(
            text.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value
        )
            ? value
            : double.NaN;
    }

    public int Column(string name) =>
        Rows.Count == 0
            ? -1
            : Rows[0].FindIndex(c => string.Equals(c?.ToString(), name, StringComparison.Ordinal));

    public int Require(string name)
    {
        var c = Column(name);
        return c >= 0 ? c : throw new InvalidDataException($"Coluna obrigatória ausente: {name}.");
    }

    public int Ensure(string name)
    {
        var c = Column(name);
        if (c >= 0)
            return c;
        if (Count == 0)
            Add();
        c = Width;
        Set(0, c, name);
        return c;
    }

    public void Set(int row, int column, object? value)
    {
        while (Rows.Count <= row)
            Add();
        while (Rows[row].Count <= column)
            Rows[row].Add(null);
        Rows[row][column] = value is double n && !double.IsFinite(n) ? null : value;
    }

    public void UseHeader(int headerRow)
    {
        if (headerRow < 1 || headerRow > Count)
            throw new InvalidDataException("Linha de cabeçalho inválida.");
        Rows.RemoveRange(0, headerRow - 1);
    }

    public JsonObject ToJson(int limit = 0)
    {
        var columns = new JsonArray();
        var rows = new JsonArray();
        int width = Width;
        for (int c = 0; c < width; c++)
            columns.Add(Text(0, c));
        for (int r = 1; r < Count && (limit <= 0 || r <= limit); r++)
        {
            var row = new JsonArray();
            for (int c = 0; c < width; c++)
                row.Add(
                    Value(r, c) is double n
                        ? JsonExtensions.Finite(n)
                        : JsonValue.Create(Text(r, c))
                );
            rows.Add(row);
        }
        return new()
        {
            ["columns"] = columns,
            ["rows"] = rows,
            ["total"] = Math.Max(0, Count - 1),
        };
    }

    public DataTable Copy()
    {
        var result = new DataTable();
        foreach (var row in Rows)
            result.Rows.Add([.. row]);
        return result;
    }
}
