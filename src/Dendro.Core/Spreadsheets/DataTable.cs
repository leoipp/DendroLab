using System.Globalization;
using System.Text.Json.Nodes;

namespace Dendro.Core.Spreadsheets;

/// <summary>Worksheet values retain their Excel numeric or text type; row zero is the header.</summary>
public sealed class DataTable
{
    private const int MaximumPooledStringsPerColumn = 16_384;
    private sealed class NumericColumn
    {
        public double[] Values = [];
        public bool[] Present = [];
        public void Ensure(int length, int preferredCapacity)
        {
            if (Values.Length >= length) return;
            int size = preferredCapacity >= length ? preferredCapacity
                : Math.Max(length, Values.Length == 0 ? 16 : checked(Values.Length * 2));
            Array.Resize(ref Values, size);
            Array.Resize(ref Present, size);
        }
    }

    internal readonly record struct NumericColumnView(double[] Values, bool[] Present)
    {
        public double Number(int row) => Present[row] ? Values[row] : double.NaN;
    }

    private readonly List<Dictionary<string, string>?> stringPools = [];
    private readonly HashSet<int> disabledStringPools = [];
    private readonly List<string?> lastStringValues = [];
    private readonly List<string?> lastInternedStrings = [];
    private readonly List<Dictionary<long, object>?> integerNumberPools = [];
    private readonly HashSet<int> disabledIntegerNumberPools = [];
    private readonly List<NumericColumn?> numericColumns = [];
    private readonly List<object?[]?> objectColumns = [];
    private bool columnarStorageEnabled;
    private int numericStorageCapacity;
    private int width;
    public List<object?[]> Rows { get; } = [];
    public int Count => Rows.Count;
    public int Width => width;

    internal NumericColumnView? NumericView(int column) =>
        columnarStorageEnabled && column >= 0 && column < numericColumns.Count
            && numericColumns[column] is { } numeric
                ? new NumericColumnView(numeric.Values, numeric.Present)
                : null;

    public DataTable() { }

    public DataTable(IEnumerable<string> columns) => Add(columns.Cast<object?>().ToArray());

    /// <summary>Stores projected values column-wise and leaves only the header in row storage.</summary>
    internal void EnableColumnarStorage(int capacity)
    {
        if (Rows.Count != 1 || columnarStorageEnabled || capacity < Rows.Count)
            throw new InvalidOperationException("O armazenamento colunar precisa ser ativado antes da leitura das linhas.");
        columnarStorageEnabled = true;
        numericStorageCapacity = capacity;
        EnsureColumnStores();
    }

    public void Add(params object?[] cells)
    {
        var row = new object?[cells.Length];
        Array.Copy(cells, row, cells.Length);
        AddOwned(row);
    }

    /// <summary>Transfers a disposable row array to this table without allocating a second row.</summary>
    public void AddOwned(object?[] row)
    {
        if (columnarStorageEnabled && Rows.Count > 0)
        {
            width = Math.Max(width, row.Length);
            EnsureColumnStores();
            if (row.Length < width) Array.Resize(ref row, width);
            int rowIndex = Rows.Count;
            for (int column = 0; column < row.Length; column++)
            {
                object? value = row[column];
                if (numericColumns[column] is { } numeric)
                {
                    if (TryNumber(value, out double number) || value == null)
                    {
                        if (value == null)
                        {
                            if (rowIndex < numeric.Present.Length)
                            {
                                numeric.Values[rowIndex] = 0;
                                numeric.Present[rowIndex] = false;
                            }
                        }
                        else
                        {
                            numeric.Ensure(rowIndex + 1, numericStorageCapacity);
                            numeric.Values[rowIndex] = number;
                            numeric.Present[rowIndex] = true;
                        }
                        row[column] = null;
                        continue;
                    }
                    PromoteNumericColumn(column, rowIndex);
                }
                EnsureObjectColumn(column, rowIndex + 1);
                objectColumns[column]![rowIndex] = Intern(column, value);
            }
            Rows.Add(Array.Empty<object?>());
            width = Math.Max(width, row.Length);
            return;
        }
        for (int column = 0; column < row.Length; column++) row[column] = Intern(column, row[column]);
        Rows.Add(row);
        width = Math.Max(width, row.Length);
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case double n: number = n; return true;
            case float n: number = n; return true;
            case int n: number = n; return true;
            case long n: number = n; return true;
            default: number = 0; return false;
        }
    }

    private void EnsureNumericColumns()
    {
        while (numericColumns.Count < width)
            numericColumns.Add(new NumericColumn());
    }

    private void EnsureColumnStores()
    {
        EnsureNumericColumns();
        while (objectColumns.Count < width) objectColumns.Add(null);
    }

    private void EnsureObjectColumn(int column, int length)
    {
        while (objectColumns.Count <= column) objectColumns.Add(null);
        if (objectColumns[column] is not { } values)
            objectColumns[column] = new object?[Math.Max(length, numericStorageCapacity)];
        else if (values.Length < length)
        {
            Array.Resize(ref values, Math.Max(length, numericStorageCapacity));
            objectColumns[column] = values;
        }
    }

    private void PromoteNumericColumn(int column, int priorRows)
    {
        var numeric = numericColumns[column];
        if (numeric == null) return;
        EnsureObjectColumn(column, priorRows);
        for (int row = 1; row < priorRows; row++)
        {
            if (row < numeric.Present.Length && numeric.Present[row]) objectColumns[column]![row] = numeric.Values[row];
        }
        numericColumns[column] = null;
    }

    private object? Intern(int column, object? value)
    {
        if (value is double number && double.IsFinite(number) && number == Math.Truncate(number))
            return InternIntegerNumber(column, number);
        if (value is not string text) return value;
        while (lastStringValues.Count <= column) lastStringValues.Add(null);
        while (lastInternedStrings.Count <= column) lastInternedStrings.Add(null);
        if (string.Equals(lastStringValues[column], text, StringComparison.Ordinal))
            return lastInternedStrings[column];
        if (disabledStringPools.Contains(column))
        {
            lastStringValues[column] = text;
            lastInternedStrings[column] = text;
            return text;
        }
        while (stringPools.Count <= column) stringPools.Add(null);
        Dictionary<string, string> pool = stringPools[column] ??= new(StringComparer.Ordinal);
        if (pool.TryGetValue(text, out string? existing))
        {
            lastStringValues[column] = text;
            lastInternedStrings[column] = existing;
            return existing;
        }
        if (pool.Count >= MaximumPooledStringsPerColumn)
        {
            // High-cardinality columns are cheaper without a lookup structure.
            // Drop the bounded pool once it stops finding repeated values.
            pool.Clear();
            stringPools[column] = null;
            disabledStringPools.Add(column);
            lastStringValues[column] = text;
            lastInternedStrings[column] = text;
            return text;
        }
        pool.Add(text, text);
        lastStringValues[column] = text;
        lastInternedStrings[column] = text;
        return text;
    }

    private object InternIntegerNumber(int column, double number)
    {
        if (disabledIntegerNumberPools.Contains(column)) return number;
        while (integerNumberPools.Count <= column) integerNumberPools.Add(null);
        Dictionary<long, object> pool = integerNumberPools[column] ??= new();
        long key = BitConverter.DoubleToInt64Bits(number);
        if (pool.TryGetValue(key, out object? existing)) return existing;
        if (pool.Count >= MaximumPooledStringsPerColumn)
        {
            pool.Clear();
            integerNumberPools[column] = null;
            disabledIntegerNumberPools.Add(column);
            return number;
        }
        object boxed = number;
        pool.Add(key, boxed);
        return boxed;
    }

    public object? Value(int row, int column)
    {
        if (row < 0 || row >= Rows.Count || column < 0 || column >= width) return null;
        if (row > 0 && columnarStorageEnabled)
        {
            if (column < numericColumns.Count && numericColumns[column] is { } numeric)
                return row < numeric.Present.Length && numeric.Present[row] ? numeric.Values[row] : null;
            return column < objectColumns.Count && objectColumns[column] is { } values && row < values.Length
                ? values[row] : null;
        }
        return column < Rows[row].Length ? Rows[row][column] : null;
    }

    public string Text(int row, int column)
    {
        if (row > 0 && columnarStorageEnabled && column >= 0 && column < numericColumns.Count
            && numericColumns[column] is { } numeric)
            return row < numeric.Present.Length && numeric.Present[row]
                ? numeric.Values[row].ToString("G17", CultureInfo.InvariantCulture)
                : "";
        return Value(row, column) switch
        {
            double n when double.IsFinite(n) => n.ToString("G17", CultureInfo.InvariantCulture),
            float n => n.ToString("G9", CultureInfo.InvariantCulture),
            IFormattable v => v.ToString(null, CultureInfo.InvariantCulture) ?? "",
            var v => v?.ToString() ?? "",
        };
    }

    public double Number(int row, int column)
    {
        if (row > 0 && columnarStorageEnabled && column >= 0 && column < numericColumns.Count
            && numericColumns[column] is { } numeric)
            return row < numeric.Present.Length && numeric.Present[row] ? numeric.Values[row] : double.NaN;
        var cell = Value(row, column);
        if (cell is double number) return number;
        if (cell is int integer) return integer;
        if (cell is long longInteger) return longInteger;
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
            : Array.FindIndex(Rows[0], c => string.Equals(c?.ToString(), name, StringComparison.Ordinal));

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
        if (value is double nonFinite && !double.IsFinite(nonFinite)) value = null;
        width = Math.Max(width, column + 1);
        if (columnarStorageEnabled) EnsureColumnStores();
        if (row > 0 && columnarStorageEnabled && column < numericColumns.Count
            && numericColumns[column] is { } numeric)
        {
            if (TryNumber(value, out double number) || value == null)
            {
                if (value == null)
                {
                    if (row < numeric.Present.Length)
                    {
                        numeric.Values[row] = 0;
                        numeric.Present[row] = false;
                    }
                }
                else
                {
                    numeric.Ensure(row + 1, numericStorageCapacity);
                    numeric.Values[row] = number;
                    numeric.Present[row] = true;
                }
                return;
            }
            PromoteNumericColumn(column, row);
        }
        if (row > 0 && columnarStorageEnabled)
        {
            EnsureObjectColumn(column, row + 1);
            objectColumns[column]![row] = Intern(column, value);
            return;
        }
        if (Rows[row].Length <= column)
        {
            var values = Rows[row];
            Array.Resize(ref values, column + 1);
            Rows[row] = values;
        }
        Rows[row][column] = Intern(column, value);
    }

    public void TrimWidth(int targetWidth)
    {
        if (targetWidth < 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        for (int row = 0; row < Rows.Count; row++)
            if (Rows[row].Length > targetWidth)
                Rows[row] = Rows[row][..targetWidth];
        if (numericColumns.Count > targetWidth)
            numericColumns.RemoveRange(targetWidth, numericColumns.Count - targetWidth);
        if (objectColumns.Count > targetWidth)
            objectColumns.RemoveRange(targetWidth, objectColumns.Count - targetWidth);
        width = Rows.Count == 0 ? 0 : Math.Min(width, targetWidth);
    }

    public void ReleaseRowValues(int row)
    {
        if (row <= 0 || row >= Rows.Count) throw new ArgumentOutOfRangeException(nameof(row));
        if (columnarStorageEnabled)
        {
            foreach (var numeric in numericColumns)
                if (numeric != null && row < numeric.Present.Length)
                {
                    numeric.Present[row] = false;
                    numeric.Values[row] = 0;
                }
            foreach (var column in objectColumns)
                if (column != null && row < column.Length) column[row] = null;
        }
        Rows[row] = Array.Empty<object?>();
    }

    public void ReleaseDataRows()
    {
        if (Rows.Count > 1) Rows.RemoveRange(1, Rows.Count - 1);
        stringPools.Clear();
        disabledStringPools.Clear();
        lastStringValues.Clear();
        lastInternedStrings.Clear();
        integerNumberPools.Clear();
        disabledIntegerNumberPools.Clear();
        numericColumns.Clear();
        objectColumns.Clear();
        columnarStorageEnabled = false;
        numericStorageCapacity = 0;
        width = Rows.Count == 0 ? 0 : Rows[0].Length;
    }

    /// <summary>Compacts data rows while keeping columnar storage aligned with the row index.</summary>
    internal int RetainRows(bool[] keepRows)
    {
        if (keepRows.Length != Rows.Count)
            throw new ArgumentException("A seleção de linhas precisa corresponder à tabela.", nameof(keepRows));
        int retained = 1;
        if (columnarStorageEnabled)
        {
            for (int row = 1; row < Rows.Count; row++)
            {
                if (!keepRows[row]) continue;
                if (row != retained)
                {
                    for (int column = 0; column < width; column++)
                    {
                        if (numericColumns[column] is { } numeric)
                        {
                            numeric.Values[retained] = numeric.Values[row];
                            numeric.Present[retained] = numeric.Present[row];
                        }
                        else if (objectColumns[column] is { } values)
                        {
                            values[retained] = values[row];
                            values[row] = null;
                        }
                    }
                }
                retained++;
            }
            foreach (var values in objectColumns)
                if (values != null && retained < values.Length)
                    Array.Clear(values, retained, values.Length - retained);
        }
        else
        {
            for (int row = 1; row < Rows.Count; row++)
                if (keepRows[row]) Rows[retained++] = Rows[row];
        }
        if (retained < Rows.Count) Rows.RemoveRange(retained, Rows.Count - retained);
        return retained;
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
        for (int row = 0; row < Count; row++)
            result.Add(Enumerable.Range(0, Width).Select(column => Value(row, column)).ToArray());
        return result;
    }
}
