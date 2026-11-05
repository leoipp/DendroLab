using System.Globalization;
using System.Text.Json;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Tabular;

public sealed record PairedMeasurementOptions(
    string DateColumn,
    string[] ValueColumns,
    string[] TreeKeys,
    string[] PlotKeys,
    string[] Levels,
    string Aggregation,
    string Format = "wide",
    string Pairing = "sequential"
);

public sealed record PairedMeasurementsResult(
    TableResult[] Tables,
    long ReadRows,
    long InvalidDates,
    long EmptyKeys,
    long DuplicateKeyYears
);

/// <summary>Pairs repeated measurements in wide or pairwise longitudinal form.</summary>
public static class PairedMeasurements
{
    private sealed class Cell
    {
        public int Values;
        public object? First;
        public double Sum;
        public double Minimum = double.PositiveInfinity;
        public double Maximum = double.NegativeInfinity;
    }

    private sealed class YearData(int fields)
    {
        public int Rows;
        public Cell[] Values { get; } = Enumerable.Range(0, fields).Select(_ => new Cell()).ToArray();
    }

    private sealed record Entity(object?[] Keys, Dictionary<int, YearData> Years);

    private sealed class LevelState(string name, string[] keyNames, int[] keyIndexes)
    {
        public string Name { get; } = name;
        public string[] KeyNames { get; } = keyNames;
        public int[] KeyIndexes { get; } = keyIndexes;
        public Dictionary<string, Entity> Entities { get; } = new(StringComparer.Ordinal);
    }

    public static PairedMeasurementsResult Create(
        TableSource source,
        string folder,
        string name,
        PairedMeasurementOptions options,
        CancellationToken cancellation = default,
        IProgress<TableProgress>? progress = null
    )
    {
        if (options.Levels.Length is < 1 or > 2 || options.Levels.Any(l => l is not ("arvore" or "parcela"))
            || options.Levels.Distinct(StringComparer.Ordinal).Count() != options.Levels.Length)
            throw new InvalidDataException("Selecione árvore, parcela ou ambos os níveis.");
        if (options.Aggregation is not ("sum" or "mean" or "max" or "min" or "unique"))
            throw new InvalidDataException("Selecione uma regra válida para valores repetidos.");
        if (options.Format is not ("wide" or "longitudinal"))
            throw new InvalidDataException("Selecione o formato amplo ou longitudinal.");
        if (options.Pairing is not ("sequential" or "all"))
            throw new InvalidDataException("Selecione medições sequenciais ou todos os pares.");
        if (options.ValueColumns.Length == 0 || options.ValueColumns.Distinct(StringComparer.Ordinal).Count() != options.ValueColumns.Length)
            throw new InvalidDataException("Selecione um ou mais campos únicos para comparar ao longo do tempo.");
        using var cursor = new TableCursor(source);
        int Require(string column)
        {
            int index = Array.IndexOf(cursor.Columns, column);
            return index >= 0 ? index : throw new InvalidDataException($"Coluna não encontrada na origem: {column}.");
        }
        int dateColumn = Require(options.DateColumn);
        int[] valueColumns = options.ValueColumns.Select(Require).ToArray();
        var levels = new List<LevelState>();
        foreach (string level in options.Levels)
        {
            string[] names = (level == "arvore" ? options.TreeKeys : options.PlotKeys)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length == 0)
                throw new InvalidDataException(level == "arvore"
                    ? "Não há chaves de árvore configuradas. Confira Projeto > Configurações > Identificação."
                    : "Não há chaves de parcela configuradas. Confira Projeto > Configurações > Identificação.");
            int[] indexes = names.Select(Require).ToArray();
            if (indexes.Contains(dateColumn))
                throw new InvalidDataException("A data de medição não pode fazer parte da chave usada para parear.");
            if (indexes.Intersect(valueColumns).Any())
                throw new InvalidDataException("Os campos trazidos não podem fazer parte das chaves de identificação.");
            levels.Add(new(level, names, indexes));
        }

        var updates = new TableProgressReporter(progress);
        var years = new SortedSet<int>();
        long read = 0,
            invalidDates = 0,
            emptyKeys = 0,
            duplicateCells = 0;
        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            read++;
            if (!TryYear(row[dateColumn], out int year))
            {
                invalidDates++;
                continue;
            }
            years.Add(year);
            foreach (var level in levels)
            {
                string[] keyValues = level.KeyIndexes.Select(i => KeyValue(row[i])).ToArray();
                if (keyValues.Any(string.IsNullOrEmpty))
                {
                    emptyKeys++;
                    continue;
                }
                string key = JsonSerializer.Serialize(keyValues);
                if (!level.Entities.TryGetValue(key, out var entity))
                    level.Entities.Add(key, entity = new(level.KeyIndexes.Select(i => row[i]).ToArray(), []));
                if (!entity.Years.TryGetValue(year, out var yearData))
                    entity.Years.Add(year, yearData = new(valueColumns.Length));
                if (yearData.Rows == 1) duplicateCells++;
                yearData.Rows++;
                if (options.Aggregation == "unique" && yearData.Rows > 1)
                    throw new InvalidDataException($"Há mais de um registro para a mesma chave e ano ({year}). Escolha uma agregação ou refine a chave.");
                for (int field = 0; field < valueColumns.Length; field++)
                {
                    var cell = yearData.Values[field];
                    object? raw = row[valueColumns[field]];
                    if (raw == null || string.IsNullOrWhiteSpace(TableCursor.Text(raw)))
                        continue;
                    if (options.Aggregation == "unique")
                    {
                        cell.First = raw;
                        cell.Values++;
                        continue;
                    }
                    if (!TryNumber(raw, out double value))
                        throw new InvalidDataException($"A coluna {options.ValueColumns[field]} tem um valor não numérico na linha {read + 1:N0}; a agregação selecionada exige números.");
                    cell.Sum += value;
                    cell.Minimum = Math.Min(cell.Minimum, value);
                    cell.Maximum = Math.Max(cell.Maximum, value);
                    cell.Values++;
                }
            }
            if ((read & 2047) == 0)
                updates.Report(cursor.TotalRows > 0 ? 75d * read / cursor.TotalRows.Value : null,
                    $"Pareando medições: {read:N0} linhas lidas", read);
        }
        if (years.Count == 0)
            throw new InvalidDataException("Nenhuma linha possui ano ou data de medição válido.");

        string cleanName = TableTools.SuggestedName(name);
        var results = new List<TableResult>();
        var createdFiles = new List<string>();
        try
        {
            foreach (var level in levels)
            {
                cancellation.ThrowIfCancellationRequested();
                string label = level.Name == "arvore" ? "Árvores" : "Parcelas";
                string tableName = TableTools.SuggestedName($"{cleanName} · {label}"
                    + (options.Format == "longitudinal" ? " · Longitudinal" : ""));
                string[] keyNames = level.KeyNames.Select((field, index) =>
                    VisibleHeader(field, $"Chave_{index + 1}")).ToArray();
                string[] valueNames = options.ValueColumns.Select((field, index) =>
                    VisibleHeader(field, $"Valor_{index + 1}")).ToArray();
                string[] columns = options.Format == "wide"
                    ? [.. keyNames,
                        .. years.SelectMany(year => new[] { $"N_{year}" }.Concat(valueNames.Select(field => $"{field}_{year}")))]
                    : [.. keyNames, "N_individuo",
                        "Ano_i1", "N_i1", .. valueNames.Select(field => $"{field}_i1"),
                        "Ano_i2", "N_i2", .. valueNames.Select(field => $"{field}_i2")];
                columns = EnsureUniqueHeaders(columns);
                string output = Path.Combine(folder, $"{Guid.NewGuid():N}.dlb");
                createdFiles.Add(output);
                long outputRows = 0;
                using (var writer = new CompactBankWriter(output))
                {
                    string table = writer.AddTable(columns, tableName);
                    foreach (var entity in level.Entities.Values)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        object? Value(Cell? cell) => cell is { Values: > 0 }
                            ? options.Aggregation switch
                            {
                                "unique" => cell.First,
                                "sum" => cell.Sum,
                                "mean" => cell.Sum / cell.Values,
                                "max" => cell.Maximum,
                                "min" => cell.Minimum,
                                _ => null,
                            }
                            : null;
                        if (options.Format == "wide")
                        {
                            var values = new object?[columns.Length];
                            Array.Copy(entity.Keys, values, entity.Keys.Length);
                            int index = level.KeyNames.Length;
                            foreach (int year in years)
                            {
                                entity.Years.TryGetValue(year, out var yearData);
                                values[index++] = yearData?.Rows ?? 0;
                                for (int field = 0; field < options.ValueColumns.Length; field++)
                                    values[index++] = Value(yearData?.Values[field]);
                            }
                            writer.AddOwned(table, values);
                            outputRows++;
                            continue;
                        }

                        int[] entityYears = entity.Years.Keys.Order().ToArray();
                        int totalN = entity.Years.Values.Sum(data => data.Rows);
                        var pairs = options.Pairing == "sequential"
                            ? Enumerable.Range(0, Math.Max(0, entityYears.Length - 1)).Select(i => (First: i, Second: i + 1))
                            : (from first in Enumerable.Range(0, entityYears.Length)
                               from second in Enumerable.Range(first + 1, entityYears.Length - first - 1)
                               select (First: first, Second: second));
                        foreach (var pair in pairs)
                        {
                            var firstYear = entityYears[pair.First];
                            var secondYear = entityYears[pair.Second];
                            var firstData = entity.Years[firstYear];
                            var secondData = entity.Years[secondYear];
                            var values = new object?[columns.Length];
                            Array.Copy(entity.Keys, values, entity.Keys.Length);
                            int index = entity.Keys.Length;
                            values[index++] = totalN;
                            values[index++] = firstYear;
                            values[index++] = firstData.Rows;
                            foreach (var cell in firstData.Values) values[index++] = Value(cell);
                            values[index++] = secondYear;
                            values[index++] = secondData.Rows;
                            foreach (var cell in secondData.Values) values[index++] = Value(cell);
                            writer.AddOwned(table, values);
                            outputRows++;
                        }
                    }
                    writer.Complete();
                }
                if (options.Format == "longitudinal" && outputRows == 0)
                    throw new InvalidDataException($"Nenhuma chave do nível {label} possui pelo menos duas ocasiões válidas para formar pares.");
                results.Add(new(output, tableName, outputRows));
            }
        }
        catch
        {
            foreach (string file in createdFiles)
                if (File.Exists(file)) File.Delete(file);
            throw;
        }
        updates.Report(100,
            $"Pareamento concluído · {results.Sum(r => r.Rows):N0} linhas por nível · {years.Count:N0} anos.",
            read, true);
        return new(results.ToArray(), read, invalidDates, emptyKeys, duplicateCells);
    }

    private static string KeyValue(object? value) =>
        value == null || string.IsNullOrWhiteSpace(TableCursor.Text(value))
            ? ""
            : TableTools.Key(value);

    private static string VisibleHeader(string? value, string fallback)
    {
        string visible = new(value?.Where(character =>
        {
            var category = char.GetUnicodeCategory(character);
            return !char.IsControl(character) && category != UnicodeCategory.Format;
        }).ToArray() ?? []);
        visible = visible.Trim();
        return visible.Length > 0 ? visible : fallback;
    }

    private static string[] EnsureUniqueHeaders(string[] columns)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return columns.Select((column, index) =>
        {
            string visible = VisibleHeader(column, $"Campo_{index + 1}");
            string candidate = visible;
            for (int suffix = 2; !used.Add(candidate); suffix++)
                candidate = $"{visible}_{suffix}";
            return candidate;
        }).ToArray();
    }

    private static bool TryNumber(object value, out double number)
    {
        if (value is double d && double.IsFinite(d)) { number = d; return true; }
        string text = TableCursor.Text(value).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number)
                && double.IsFinite(number);
    }

    private static bool TryYear(object? value, out int year)
    {
        year = 0;
        if (value == null) return false;
        if (value is double number)
        {
            if (number >= 1800 && number <= 9999 && number == Math.Truncate(number))
            {
                year = (int)number;
                return true;
            }
            if (number > 0 && number <= 2958465)
            {
                year = DateTime.FromOADate(number).Year;
                return true;
            }
        }
        string text = TableCursor.Text(value).Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericText)
            && numericText >= 1800 && numericText <= 9999 && numericText == Math.Truncate(numericText))
        {
            year = (int)numericText;
            return true;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawYear)
            && rawYear is >= 1800 and <= 9999)
        {
            year = rawYear;
            return true;
        }
        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            year = date.Year;
            return true;
        }
        double serial = InventoryMath.Date(text);
        if (double.IsFinite(serial) && serial > 0 && serial <= 2958465)
        {
            year = DateTime.FromOADate(serial).Year;
            return true;
        }
        return false;
    }
}
