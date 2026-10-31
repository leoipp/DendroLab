using System.Globalization;
using System.Text.Json;

namespace Dendro.Core.Tabular;

public sealed record TableResult(string Path, string Table, long Rows);

public enum FilterOperator
{
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    Contains,
    StartsWith,
    Empty,
    NotEmpty,
}

public sealed record TableFilter(string Column, FilterOperator Operator, string Value = "");

public sealed record Stratum(string Key, string Value, long Rows);

public sealed record StratumOutput(string Key, string Name);

public sealed record DerivedColumn(string Name, string[] SourceColumns, string Separator = " · ", string Expression = "");

/// <summary>Disk-backed table operations. Output publication never overwrites existing files.</summary>
public static class TableTools
{
    public const int ExcelRows = 1_048_576;
    public const int ExcelColumns = 16_384;

    public static bool FitsExcel(long rows, int columns) =>
        rows >= 0 && rows < ExcelRows && columns > 0 && columns <= ExcelColumns;

    private static void Validate(string[] columns)
    {
        if (
            columns.Length == 0
            || columns.Any(string.IsNullOrWhiteSpace)
            || columns.Distinct(StringComparer.Ordinal).Count() != columns.Length
        )
            throw new InvalidDataException(
                "O cabeçalho deve conter nomes de colunas únicos e preenchidos."
            );
    }

    public static string Key(object? value) =>
        value == null ? "null:" : (value is double ? "number:" : "text:") + TableCursor.Text(value);

    private sealed record DerivedPlan(string[] Columns, Func<object?[], object?>[] Calculations);

    private static DerivedPlan PlanDerived(string[] sourceColumns, IReadOnlyList<DerivedColumn>? derived)
    {
        Validate(sourceColumns);
        var columns = sourceColumns.ToList();
        var calculations = new List<Func<object?[], object?>>();
        foreach (var item in derived ?? [])
        {
            string name = item.Name.Trim();
            if (name.Length == 0 || columns.Contains(name, StringComparer.Ordinal))
                throw new InvalidDataException("O nome da coluna derivada deve ser novo e único.");
            if (!string.IsNullOrWhiteSpace(item.Expression))
            {
                var expression = new ColumnExpression(item.Expression, columns.ToArray());
                calculations.Add(expression.Evaluate);
                columns.Add(name);
                continue;
            }
            if (item.SourceColumns.Length == 0)
                throw new InvalidDataException($"Selecione colunas de origem para {name}.");
            int[] sources = item.SourceColumns.Select(source => columns.IndexOf(source)).ToArray();
            if (sources.Any(index => index < 0))
                throw new InvalidDataException($"Uma coluna de origem de {name} não existe.");
            columns.Add(name);
            calculations.Add(row => string.Join(item.Separator, sources.Select(index => TableCursor.Text(row[index]))));
        }
        return new(columns.ToArray(), calculations.ToArray());
    }

    private static object?[] ExpandRow(object?[] row, DerivedPlan plan)
    {
        if (plan.Calculations.Length == 0) return row;
        var expanded = new object?[row.Length + plan.Calculations.Length];
        Array.Copy(row, expanded, row.Length);
        for (int i = 0; i < plan.Calculations.Length; i++)
        {
            object? value = plan.Calculations[i](expanded);
            if (value is double number && !double.IsFinite(number))
                throw new InvalidDataException($"Resultado numérico inválido em {plan.Columns[row.Length + i]}.");
            expanded[row.Length + i] = value is bool b ? (b ? "Verdadeiro" : "Falso") : value;
        }
        return expanded;
    }

    public static string[] PreviewColumns(TableSource source, IReadOnlyList<DerivedColumn> derived)
    {
        using var cursor = new TableCursor(source);
        var plan = PlanDerived(cursor.Columns, derived);
        return cursor.Rows().Take(5).Select(row => {
            var expanded = ExpandRow(row, plan);
            return string.Join(" | ", Enumerable.Range(cursor.Columns.Length, derived.Count)
                .Select(i => $"{plan.Columns[i]} = {TableCursor.Text(expanded[i])}"));
        }).ToArray();
    }

    public static TableResult CalculateColumns(TableSource source, IReadOnlyList<DerivedColumn> derived,
        string folder, string name, CancellationToken cancellation = default)
    {
        if (derived.Count == 0) throw new InvalidDataException("Adicione pelo menos uma coluna calculada.");
        using var cursor = new TableCursor(source);
        var plan = PlanDerived(cursor.Columns, derived);
        using var job = new Job(folder);
        string id;
        using (var writer = new CompactBankWriter(job.Stage))
        {
            id = writer.AddTable(plan.Columns);
            long rowNumber = 1;
            foreach (var row in cursor.Rows())
            {
                cancellation.ThrowIfCancellationRequested(); rowNumber++;
                try { writer.Add(id, ExpandRow(row, plan)); }
                catch (InvalidDataException ex) { throw new InvalidDataException($"Linha {rowNumber}: {ex.Message}", ex); }
            }
            writer.Complete();
        }
        return job.Publish([(id, name)], false, cancellation, true)[0];
    }

    private static string CompositeKey(object?[] row, int[] indexes) =>
        indexes.Length == 1 ? Key(row[indexes[0]]) : JsonSerializer.Serialize(indexes.Select(index => Key(row[index])).ToArray());

    private static string StratumValue(object?[] row, int[] indexes, string[] columns) =>
        indexes.Length == 1
            ? row[indexes[0]] == null ? "(vazio)" : TableCursor.Text(row[indexes[0]])
            : string.Join(" | ", indexes.Select(index =>
                $"{columns[index]}={(row[index] == null ? "(vazio)" : TableCursor.Text(row[index]))}"));

    public static string FileName(string name)
    {
        name = name.Trim();
        if (
            name.Length == 0
            || name.Length > 120
            || name is "." or ".."
            || name.EndsWith('.')
            || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
        )
            throw new InvalidDataException(
                "Informe um nome de saída válido, sem caminhos ou extensões."
            );
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (
            new[]
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9",
            }.Contains(stem)
        )
            throw new InvalidDataException("O nome de saída é reservado pelo Windows.");
        return name;
    }

    public static string SuggestedName(string value)
    {
        var clean = new string(
            value
                .Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                .ToArray()
        )
            .Trim()
            .TrimEnd('.');
        if (clean.Length > 90)
            clean = clean[..90];
        try
        {
            return FileName(clean);
        }
        catch (InvalidDataException)
        {
            return "Estrato_" + (clean.Length == 0 ? "vazio" : clean);
        }
    }

    public static TableResult Merge(
        IReadOnlyList<TableSource> sources,
        string folder,
        string name,
        bool database = false,
        CancellationToken cancellation = default,
        bool compact = false,
        bool commonColumnsOnly = false,
        IProgress<TableProgress>? progress = null,
        IReadOnlyList<string>? sourceNames = null
    )
    {
        if (sources.Count < 2)
            throw new InvalidDataException("Selecione pelo menos duas tabelas para juntar.");
        if (sourceNames != null && (sourceNames.Count != sources.Count || sourceNames.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("Informe um nome de origem preenchido para cada tabela selecionada.");
        var updates = new TableProgressReporter(progress);
        updates.Report(null, "Verificando as tabelas de origem…", force: true);
        string[]? columns = null;
        var schemas = new List<string[]>();
        // Determine the complete output schema before streaming any data rows.
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            updates.Report(null,
                $"Cabeçalhos: {schemas.Count + 1}/{sources.Count} · {System.IO.Path.GetFileName(source.Path)}");
            using var cursor = new TableCursor(source);
            Validate(cursor.Columns);
            schemas.Add(cursor.Columns);
            if (columns == null) columns = cursor.Columns;
            else if (commonColumnsOnly)
            {
                var names = cursor.Columns.ToHashSet(StringComparer.Ordinal);
                columns = columns.Where(names.Contains).ToArray();
            }
            else if (columns.Length != cursor.Columns.Length || columns.Except(cursor.Columns, StringComparer.Ordinal).Any())
                throw new InvalidDataException(
                    $"As colunas de {System.IO.Path.GetFileName(source.Path)} não correspondem às da primeira tabela. Marque a opção de usar apenas colunas comuns para juntar estruturas diferentes.");
        }
        if (columns!.Length == 0)
            throw new InvalidDataException("Não há colunas com o mesmo nome em todas as tabelas selecionadas.");
        var usedNames = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputColumns = columns.Select(column =>
        {
            if (!column.Equals("Fonte", StringComparison.OrdinalIgnoreCase)) return column;
            string renamed = "Fonte_anterior";
            for (int suffix = 2; !usedNames.Add(renamed); suffix++) renamed = "Fonte_anterior_" + suffix;
            return renamed;
        }).Append("Fonte").ToArray();
        cancellation.ThrowIfCancellationRequested();
        updates.Report(10, $"Cabeçalhos verificados · {sources.Count} tabelas · {columns.Length} colunas.", force: true);
        using var job = new Job(folder);
        string id;
        long rows = 0;
        using (var writer = new CompactBankWriter(job.Stage))
        {
            id = writer.AddTable(outputColumns);
            for (int i = 0; i < sources.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                updates.Report(null, $"Abrindo tabela {i + 1}/{sources.Count} · {System.IO.Path.GetFileName(sources[i].Path)}", rows, i == 0);
                using var cursor = new TableCursor(sources[i]);
                if (!cursor.Columns.SequenceEqual(schemas[i], StringComparer.Ordinal))
                    throw new InvalidDataException("O cabeçalho de uma origem mudou durante a junção. Execute novamente.");
                var indexes = cursor.Columns.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
                var mapping = columns.Select(c => indexes[c]).ToArray();
                string sourceName = sourceNames?[i] ?? System.IO.Path.GetFileNameWithoutExtension(sources[i].Path)
                    + (sources[i].Table.Length > 0 ? " · " + sources[i].Table : "");
                long sourceRows = 0;
                void ReportRows() => updates.Report(cursor.TotalRows.HasValue
                    ? 10 + 80.0 * (i + (cursor.TotalRows > 0 ? Math.Min(1, sourceRows / (double)cursor.TotalRows.Value) : 1)) / sources.Count
                    : null, $"Juntando {i + 1}/{sources.Count} · {System.IO.Path.GetFileName(sources[i].Path)} · {rows:N0} linhas gravadas", rows);
                ReportRows();
                foreach (var row in cursor.Rows())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var values = new object?[mapping.Length + 1];
                    for (int c = 0; c < mapping.Length; c++) values[c] = row[mapping[c]];
                    values[^1] = sourceName;
                    writer.Add(id, values);
                    sourceRows++; rows++;
                    if ((sourceRows & 2047) == 0) ReportRows();
                }
                updates.Report(10 + 80.0 * (i + 1) / sources.Count,
                    $"Tabela {i + 1}/{sources.Count} concluída · {rows:N0} linhas gravadas", rows);
            }
            cancellation.ThrowIfCancellationRequested();
            updates.Report(90, $"Finalizando blocos comprimidos · {rows:N0} linhas…", rows, true);
            writer.Complete();
        }
        updates.Report(95, "Publicando resultado no banco…", rows, true);
        var result = job.Publish(new[] { (id, FileName(name)) }, database, cancellation, compact)[0];
        updates.Report(100, $"Junção concluída · {rows:N0} linhas.", rows, true);
        return result;
    }

    public static TableResult Import(TableSource source, string folder, string name, CancellationToken cancellation = default)
    {
        string path = System.IO.Path.Combine(folder, FileName(name) + CompactBankReader.Extension);
        using var cursor = new TableCursor(source);
        Validate(cursor.Columns);
        long rows = 0;
        Dendro.Core.Configuration.ProjectStore.AtomicWrite(path, temporary => {
            using var writer = new CompactBankWriter(temporary);
            string id = writer.AddTable(cursor.Columns);
            foreach (var row in cursor.Rows()) {
                cancellation.ThrowIfCancellationRequested(); writer.Add(id, row); rows++;
            }
            writer.Complete();
        });
        return new(path, "Dados", rows);
    }

    internal static Func<object?[], bool> Predicate(
        string[] columns,
        IReadOnlyList<TableFilter> filters
    )
    {
        var tests = filters
            .Select(f => (Index: Array.IndexOf(columns, f.Column), Filter: f))
            .ToArray();
        if (tests.Any(t => t.Index < 0))
            throw new InvalidDataException("Uma coluna de filtro não existe na tabela.");
        return row =>
            tests.All(t =>
            {
                var f = t.Filter;
                object? cell = row[t.Index];
                string text = TableCursor.Text(cell);
                bool empty = cell == null || text.Length == 0;
                int compare =
                    cell is double n
                    && double.TryParse(
                        f.Value.Replace(',', '.'),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number
                    )
                        ? n.CompareTo(number)
                        : string.Compare(text, f.Value, StringComparison.Ordinal);
                return f.Operator switch
                {
                    FilterOperator.Empty => empty,
                    FilterOperator.NotEmpty => !empty,
                    FilterOperator.Equal => compare == 0,
                    FilterOperator.NotEqual => compare != 0,
                    FilterOperator.Greater => !empty && compare > 0,
                    FilterOperator.GreaterOrEqual => !empty && compare >= 0,
                    FilterOperator.Less => !empty && compare < 0,
                    FilterOperator.LessOrEqual => !empty && compare <= 0,
                    FilterOperator.Contains => text.Contains(
                        f.Value,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    FilterOperator.StartsWith => text.StartsWith(
                        f.Value,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    _ => false,
                };
            });
    }

    public static Stratum[] Discover(
        TableSource source,
        string column,
        IReadOnlyList<TableFilter> filters,
        CancellationToken cancellation = default
    ) => Discover(source, [column], filters, null, cancellation);

    public static Stratum[] Discover(
        TableSource source,
        IReadOnlyList<string> columns,
        IReadOnlyList<TableFilter> filters,
        IReadOnlyList<DerivedColumn>? derivedColumns = null,
        CancellationToken cancellation = default
    )
    {
        using var cursor = new TableCursor(source);
        var plan = PlanDerived(cursor.Columns, derivedColumns);
        Validate(plan.Columns);
        int[] indexes = columns.Select(column => Array.IndexOf(plan.Columns, column)).ToArray();
        if (indexes.Length == 0 || indexes.Any(index => index < 0))
            throw new InvalidDataException("Selecione uma ou mais colunas para estratificar.");
        var matches = Predicate(plan.Columns, filters);
        var groups = new Dictionary<string, Stratum>();
        foreach (var sourceRow in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            var row = ExpandRow(sourceRow, plan);
            if (!matches(row))
                continue;
            string key = CompositeKey(row, indexes);
            if (groups.TryGetValue(key, out var group))
                groups[key] = group with { Rows = group.Rows + 1 };
            else
            {
                if (groups.Count >= 1000)
                    throw new InvalidDataException(
                        "Foram encontrados mais de 1.000 estratos. Refine os filtros ou escolha outra coluna."
                    );
                groups[key] = new(key, StratumValue(row, indexes, plan.Columns), 1);
            }
        }
        return groups.Values.OrderBy(s => s.Value, StringComparer.CurrentCulture).ToArray();
    }

    public static TableResult[] Stratify(
        TableSource source,
        string column,
        IReadOnlyList<TableFilter> filters,
        IReadOnlyList<StratumOutput> outputs,
        string folder,
        bool database = false,
        CancellationToken cancellation = default,
        bool compact = false
    ) => Stratify(source, [column], filters, outputs, folder, database, cancellation, compact, null);

    public static TableResult[] Stratify(
        TableSource source,
        IReadOnlyList<string> columns,
        IReadOnlyList<TableFilter> filters,
        IReadOnlyList<StratumOutput> outputs,
        string folder,
        bool database = false,
        CancellationToken cancellation = default,
        bool compact = false,
        IReadOnlyList<DerivedColumn>? derivedColumns = null
    )
    {
        if (outputs.Count == 0)
            throw new InvalidDataException("Selecione pelo menos um estrato.");
        if (outputs.Select(o => o.Key).Distinct().Count() != outputs.Count)
            throw new InvalidDataException("Estratos repetidos.");
        if (
            outputs.Select(o => FileName(o.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != outputs.Count
        )
            throw new InvalidDataException("Os nomes dos resultados devem ser diferentes.");
        using var cursor = new TableCursor(source);
        var plan = PlanDerived(cursor.Columns, derivedColumns);
        Validate(plan.Columns);
        int[] indexes = columns.Select(column => Array.IndexOf(plan.Columns, column)).ToArray();
        if (indexes.Length == 0 || indexes.Any(index => index < 0))
            throw new InvalidDataException("Selecione uma ou mais colunas para estratificar.");
        var matches = Predicate(plan.Columns, filters);
        using var job = new Job(folder);
        var ids = new Dictionary<string, string>();
        using (var writer = new CompactBankWriter(job.Stage))
        {
            foreach (var output in outputs)
                ids.Add(output.Key, writer.AddTable(plan.Columns));
            foreach (var sourceRow in cursor.Rows())
            {
                cancellation.ThrowIfCancellationRequested();
                var row = ExpandRow(sourceRow, plan);
                if (matches(row) && ids.TryGetValue(CompositeKey(row, indexes), out var id))
                    writer.Add(id, row);
            }
            writer.Complete();
        }
        return job.Publish(
            outputs.Select(o => (ids[o.Key], o.Name)).ToArray(),
            database,
            cancellation,
            compact
        );
    }

    private sealed class Job : IDisposable
    {
        private readonly string temp,
            folder;
        public string Stage => System.IO.Path.Combine(temp, "stage.dlb");

        public Job(string folder)
        {
            this.folder = System.IO.Path.GetFullPath(folder);
            if (!Directory.Exists(this.folder))
                throw new DirectoryNotFoundException("Selecione uma pasta de saída existente.");
            temp = System.IO.Path.Combine(this.folder, ".dendrolab-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
        }

        public TableResult[] Publish(
            (string Id, string Name)[] outputs,
            bool forceBank,
            CancellationToken cancellation,
            bool compact
        )
        {
            var pending = new List<(string Temp, TableResult Result)>();
            foreach (var output in outputs)
            {
                cancellation.ThrowIfCancellationRequested();
                using var cursor = new TableCursor(new(Stage, output.Id));
                long count =
                    cursor.TotalRows
                    ?? throw new InvalidDataException("Contagem de linhas ausente no banco.");
                bool bank = compact || forceBank || !FitsExcel(count, cursor.Columns.Length);
                string extension = compact ? CompactBankReader.Extension : bank ? TableStore.Extension : ".xlsx",
                    name = FileName(output.Name);
                string target = System.IO.Path.Combine(folder, name + extension),
                    staging = System.IO.Path.Combine(
                        temp,
                        Guid.NewGuid().ToString("N") + extension
                    );
                if (File.Exists(target))
                    throw new IOException($"O arquivo já existe: {target}. Escolha outro nome.");
                if (compact && outputs.Length == 1)
                {
                    // The stage is already the complete compressed output. Publish
                    // it directly instead of decompressing/recompressing every row.
                    cursor.Dispose();
                    File.Move(Stage, staging);
                }
                else if (compact)
                {
                    using var writer = new CompactBankWriter(staging);
                    string id = writer.AddTable(cursor.Columns);
                    foreach (var row in cursor.Rows()) {
                        cancellation.ThrowIfCancellationRequested(); writer.Add(id, row);
                    }
                    writer.Complete();
                }
                else if (bank)
                {
                    using var writer = new BankWriter(staging);
                    string id = writer.AddTable(cursor.Columns);
                    foreach (var row in cursor.Rows())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        writer.Add(id, row);
                    }
                    writer.Complete();
                }
                else
                    StreamingExcelWriter.Write(
                        staging,
                        cursor.Columns,
                        cursor.Rows(),
                        cancellation
                    );
                pending.Add((staging, new(target, "Dados", count)));
            }
            var published = new List<string>();
            try
            {
                foreach (var p in pending)
                {
                    cancellation.ThrowIfCancellationRequested();
                    File.Move(p.Temp, p.Result.Path);
                    published.Add(p.Result.Path);
                }
            }
            catch
            {
                foreach (var path in published)
                    File.Delete(path);
                throw;
            }
            return pending.Select(p => p.Result).ToArray();
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(temp))
                File.Delete(file);
            Directory.Delete(temp);
        }
    }
}
