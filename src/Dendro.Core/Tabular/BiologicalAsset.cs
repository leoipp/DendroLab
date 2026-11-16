using System.Globalization;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Tabular;

public sealed record BiologicalAssetCurve(string Id, string Formula, double[] Coefficients,
    string AgeUnit = "meses", double AgeFactor = 1, IReadOnlyDictionary<string, double>? FixedInputs = null);

public sealed record BiologicalAssetFallback(TableSource Source, string BaseKey, string ProcessingKey, string MeasurementDate,
    string VolumeColumn, IReadOnlyDictionary<string, string> FieldMap, string InventoryType = "", string Label = "", string CurveIdColumn = "");

public sealed record BiologicalAssetOptions(string BaseKey, string ProcessingKey, string MeasurementDate, string[] Columns,
    string Prefix = "INV_", string InventoryType = "", bool TrimKeys = true,
    string PlantingDate = "", string CurveId = "", string VolumeColumn = "", int ThroughYear = 0,
    BiologicalAssetCurve[]? Curves = null, int ProjectionMonth = 6, int ProjectionDay = 1, BiologicalAssetFallback[]? Fallbacks = null, bool UseModelWhenMissing = false);

public sealed record BiologicalAssetResult(long BaseRows, long Linked, long Missing, long Ambiguous, long InvalidDates,
    string Path, long ProjectedRows = 0, long ImaRows = 0, long MissingPlantingDates = 0,
    long MissingCurves = 0, long InvalidVolumes = 0, long FallbackLinked = 0, long ModelOnly = 0);

/// <summary>Left join by exact key, selecting the newest measurement. Conflicting latest ties never pick an arbitrary observation.</summary>
public static class BiologicalAsset
{
    private sealed record Latest(double Date, object?[] Values, bool Conflict, string CurveId = "");
    private sealed record PreparedCurve(BiologicalAssetCurve Curve, Formula Formula);

    private static double Date(object? value)
    {
        if (value is double d) return double.IsFinite(d) && d > 0 && d <= 2958465 ? Math.Floor(d) : double.NaN;
        string text = TableCursor.Text(value);
        double parsed = InventoryMath.Date(text);
        if (double.IsFinite(parsed)) return parsed > 0 && parsed <= 2958465 ? Math.Floor(parsed) : double.NaN;
        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)
            ? date.Date.ToOADate() : double.NaN;
    }

    private static double AgeYears(double planted, double measured) => Math.Floor(measured - planted) / 365.25;

    public static BiologicalAssetResult Join(TableSource basis, TableSource processed, BiologicalAssetOptions options, string output,
        CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
    {
        bool project = options.Curves is { Length: > 0 };
        if(options.UseModelWhenMissing && !project)
            throw new InvalidDataException("O modelo direto exige curvas de crescimento e produção.");
        if(project && (options.ProjectionMonth is < 1 or > 12 || options.ProjectionDay < 1
            || options.ProjectionDay > DateTime.DaysInMonth(2001, options.ProjectionMonth)))
            throw new InvalidDataException("Informe uma referência anual válida de dia e mês (sem 29/02).");
        if (options.Columns.Length == 0 || options.Columns.Distinct().Count() != options.Columns.Length)
            throw new InvalidDataException("Selecione colunas únicas para trazer do processamento.");
        if (options.InventoryType is not ("" or "IFC" or "IFPC"))
            throw new InvalidDataException("Selecione IFC, IFPC ou todos os tipos.");
        if (project && (options.PlantingDate.Length == 0 || options.CurveId.Length == 0
            || options.VolumeColumn.Length == 0 || options.ThroughYear is < 1800 or > 9999))
            throw new InvalidDataException("Informe a data de plantio, o ID da curva, o volume observado e o ano final da projeção.");
        if (File.Exists(output)) throw new IOException("A tabela de saída já existe. Escolha outro nome.");

        using var source = new TableCursor(processed);
        using var target = new TableCursor(basis);
        int key = Array.IndexOf(source.Columns, options.ProcessingKey);
        int dateCol = Array.IndexOf(source.Columns, options.MeasurementDate);
        int baseKey = Array.IndexOf(target.Columns, options.BaseKey);
        int plantingCol = project ? Array.IndexOf(target.Columns, options.PlantingDate) : -1;
        int curveIdCol = project ? Array.IndexOf(target.Columns, options.CurveId) : -1;
        int volumeCol = project ? Array.IndexOf(source.Columns, options.VolumeColumn) : -1;
        int type = Array.IndexOf(source.Columns, "Tipo_Inventario");
        string[] fields = project ? options.Columns.Append(options.VolumeColumn).Distinct(StringComparer.Ordinal).ToArray() : options.Columns;
        int[] fieldIndexes = fields.Select(c => Array.IndexOf(source.Columns, c)).ToArray();
        int volumeField = project ? Array.IndexOf(fields, options.VolumeColumn) : -1;
        if (key < 0 || dateCol < 0 || baseKey < 0 || fieldIndexes.Any(c => c < 0)
            || project && (plantingCol < 0 || curveIdCol < 0 || volumeCol < 0))
            throw new InvalidDataException("Revise as chaves, datas e colunas selecionadas.");
        if (options.InventoryType.Length > 0 && type < 0)
            throw new InvalidDataException("A tabela processada não contém Tipo_Inventario para aplicar a seleção IFC/IFPC.");

        var usedHeaders = target.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string UniqueHeader(string requested)
        {
            if (usedHeaders.Add(requested)) return requested;
            for (int suffix = 2; ; suffix++)
            {
                string candidate = $"{requested}_{suffix}";
                if (usedHeaders.Add(candidate)) return candidate;
            }
        }
        string[] outputFields = fields.Select(c => UniqueHeader(options.Prefix + c)).ToArray();
        string measurementOutput = UniqueHeader("AB_Data_Medicao");
        string linkOutput = UniqueHeader("AB_Vinculo");
        string originOutput = UniqueHeader("AB_Fonte");
        string methodOutput = UniqueHeader("AB_Metodo");
        string[] headers = [.. target.Columns, .. outputFields, measurementOutput, linkOutput, originOutput, methodOutput];

        var curves = new Dictionary<string, PreparedCurve>(StringComparer.OrdinalIgnoreCase);
        if (project)
        {
            foreach (var curve in options.Curves!)
            {
                if (curve.Id.Trim().Length == 0 || curve.AgeUnit is not ("anos" or "meses")
                    || curve.Coefficients.Length is < 1 or > 4 || curve.Coefficients.Any(c => !double.IsFinite(c))
                    || !double.IsFinite(curve.AgeFactor) || curve.AgeFactor == 0)
                    throw new InvalidDataException($"A curva ‘{curve.Id}’ tem unidade, coeficientes ou fator de idade inválidos.");
                var symbols = new[] { "x", "x1", "t" }.Concat(curve.FixedInputs?.Keys ?? [])
                    .Concat(Enumerable.Range(0, curve.Coefficients.Length).Select(i => "b" + i));
                var formula = new Formula(curve.Formula, symbols);
                if (!curves.TryAdd(curve.Id.Trim(), new(curve, formula)))
                    throw new InvalidDataException($"Há mais de uma curva ativa para o ID ‘{curve.Id}’.");
            }
        }

        string Key(object? value) { string s = TableCursor.Text(value); return options.TrimKeys ? s.Trim() : s; }
        var latest = new Dictionary<string, Latest>(StringComparer.Ordinal);
        long read = 0, invalid = 0;
        int parcel = Array.IndexOf(source.Columns, "Parcela"), stand = Array.IndexOf(source.Columns, "Talhão");
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        var mixedUnits = new HashSet<string>(StringComparer.Ordinal);
        var updates = new TableProgressReporter(progress);
        foreach (var row in source.Rows())
        {
            cancellation.ThrowIfCancellationRequested(); read++;
            if (options.InventoryType.Length > 0 && TableCursor.Text(row[type]).Trim().ToUpperInvariant() != options.InventoryType) continue;
            string id = Key(row[key]); if (id.Length == 0) continue;
            double date = Date(row[dateCol]); if (!double.IsFinite(date)) { invalid++; continue; }
            if (parcel >= 0 && TableCursor.Text(row[parcel]).Length > 0)
            {
                string unit = System.Text.Json.JsonSerializer.Serialize(new[]
                    { stand >= 0 ? TableCursor.Text(row[stand]) : "", TableCursor.Text(row[parcel]), type >= 0 ? TableCursor.Text(row[type]) : "" });
                if (units.TryGetValue(id, out string? previousUnit) && previousUnit != unit) mixedUnits.Add(id); else units[id] = unit;
            }
            var values = fieldIndexes.Select(c => row[c]).ToArray();
            if (!latest.TryGetValue(id, out var previous) || date > previous.Date) latest[id] = new(date, values, false);
            else if (date == previous.Date && !values.Select(TableCursor.Text).SequenceEqual(previous.Values.Select(TableCursor.Text)))
                latest[id] = previous with { Conflict = true };
            if ((read & 2047) == 0) updates.Report(source.TotalRows > 0 ? 35d * read / source.TotalRows : null,
                $"Identificando medições mais recentes: {read:N0} linhas", read);
        }

        var alternatives = new List<(int BaseKey, string Label, Dictionary<string, Latest> Latest, HashSet<string> Mixed)>();
        foreach (var fallback in options.Fallbacks ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            int baseIndex = Array.IndexOf(target.Columns, fallback.BaseKey);
            using var cursor = new TableCursor(fallback.Source);
            int sourceKey = Array.IndexOf(cursor.Columns, fallback.ProcessingKey);
            int sourceDate = Array.IndexOf(cursor.Columns, fallback.MeasurementDate);
            int sourceVolume = Array.IndexOf(cursor.Columns, fallback.VolumeColumn);
            int sourceType = Array.IndexOf(cursor.Columns, "Tipo_Inventario");
            int sourceCurve = Array.IndexOf(cursor.Columns, fallback.CurveIdColumn);
            if (baseIndex < 0 || sourceKey < 0 || sourceDate < 0 || project && sourceVolume < 0
                || fallback.InventoryType.Length > 0 && sourceType < 0
                || fallback.CurveIdColumn.Length > 0 && sourceCurve < 0)
                throw new InvalidDataException($"Revise as chaves, data, volume e ID da fonte alternativa {fallback.Label}.");
            int[] mapping = fields.Select(field => field == options.VolumeColumn && project ? sourceVolume
                : fallback.FieldMap.TryGetValue(field, out string? mapped) && mapped.Length > 0
                    ? Array.IndexOf(cursor.Columns, mapped) : -1).ToArray();
            foreach (var pair in fallback.FieldMap)
                if (pair.Value.Length > 0 && !cursor.Columns.Contains(pair.Value))
                    throw new InvalidDataException($"Coluna {pair.Value} ausente na fonte alternativa {fallback.Label}.");
            var items = new Dictionary<string, Latest>(StringComparer.Ordinal);
            var mixedKeys = new HashSet<string>(StringComparer.Ordinal);
            var sourceUnits = new Dictionary<string, string>(StringComparer.Ordinal);
            int parcelColumn = Array.IndexOf(cursor.Columns, "Parcela"), standColumn = Array.IndexOf(cursor.Columns, "Talhão");
            foreach (var row in cursor.Rows())
            {
                cancellation.ThrowIfCancellationRequested();
                if (fallback.InventoryType.Length > 0 && TableCursor.Text(row[sourceType]).Trim().ToUpperInvariant() != fallback.InventoryType) continue;
                string id = Key(row[sourceKey]);
                if (id.Length == 0) continue;
                double measured = Date(row[sourceDate]);
                if (!double.IsFinite(measured)) { invalid++; continue; }
                if (parcelColumn >= 0 && TableCursor.Text(row[parcelColumn]).Length > 0)
                {
                    string unit = System.Text.Json.JsonSerializer.Serialize(new[] { standColumn >= 0 ? Key(row[standColumn]) : "", Key(row[parcelColumn]), sourceType >= 0 ? Key(row[sourceType]) : "" });
                    if (sourceUnits.TryGetValue(id, out string? previousUnit) && previousUnit != unit) mixedKeys.Add(id);
                    else sourceUnits[id] = unit;
                }
                var values = mapping.Select(index => index < 0 ? null : row[index]).ToArray();
                string curveCode = sourceCurve >= 0 ? Key(row[sourceCurve]) : "";
                if (!items.TryGetValue(id, out var previous) || measured > previous.Date) items[id] = new(measured, values, false, curveCode);
                else if (measured == previous.Date && (curveCode != previous.CurveId || !values.Select(TableCursor.Text).SequenceEqual(previous.Values.Select(TableCursor.Text))))
                    items[id] = previous with { Conflict = true };
            }
            alternatives.Add((baseIndex, fallback.Label.Length > 0 ? fallback.Label : fallback.Source.Path + " / " + fallback.Source.Table, items, mixedKeys));
        }
        (Latest? Match, string Origin, bool Mixed, bool Alternative) Resolve(object?[] row)
        {
            string id = Key(row[baseKey]);
            latest.TryGetValue(id, out var primary);
            bool mixed = mixedUnits.Contains(id);
            bool Usable(Latest? match, bool isMixed) => match is { Conflict: false } && !isMixed
                && (!project || TryNumber(match.Values[volumeField], out double v) && v > 0);
            if (Usable(primary, mixed)) return (primary, processed.Path + " / " + processed.Table, false, false);
            foreach (var alternative in alternatives)
            {
                string alternateId = Key(row[alternative.BaseKey]);
                alternative.Latest.TryGetValue(alternateId, out var candidate);
                if (Usable(candidate, alternative.Mixed.Contains(alternateId))) return (candidate, alternative.Label, false, true);
            }
            return (primary, primary == null ? "" : processed.Path + " / " + processed.Table, mixed, false);
        }
        DateTime Reference(int year) => new(year, options.ProjectionMonth, options.ProjectionDay);
        var projectionYears = new SortedSet<int>();
        var imaAges = new SortedSet<int>();
        if (project)
        {
            using var schemaRows = new TableCursor(basis);
            foreach (var row in schemaRows.Rows())
            {
                cancellation.ThrowIfCancellationRequested();
                var resolved = Resolve(row);
                var match = resolved.Match;
                bool hasMeasurement = match is { Conflict: false } && !resolved.Mixed;
                if (!hasMeasurement && !options.UseModelWhenMissing) continue;
                double planted = Date(row[plantingCol]);
                if (!hasMeasurement && (!double.IsFinite(planted) || DateTime.FromOADate(planted).Year >= 9999)) continue;
                DateTime observation = hasMeasurement ? DateTime.FromOADate(match!.Date) : DateTime.FromOADate(planted).AddYears(1);
                if (!hasMeasurement && observation > Reference(options.ThroughYear)) continue;
                projectionYears.Add(observation.Year);
                for (int year = observation.Year + 1; year <= options.ThroughYear; year++)
                    projectionYears.Add(year);
                if (!double.IsFinite(planted) || planted >= observation.ToOADate()) continue;
                DateTime planting = DateTime.FromOADate(planted);
                DateTime end = Reference(options.ThroughYear);
                for (int age = 1; age <= end.Year - planting.Year; age++)
                {
                    DateTime anniversary = planting.AddYears(age);
                    if (anniversary <= end) imaAges.Add(age);
                }
            }
        }
        int[] years = projectionYears.ToArray();
        int[] ages = imaAges.ToArray();
        string statusHeader = project ? UniqueHeader("AB_Projecao_Status") : "";
        string[] volumeHeaders = years.Select(year => UniqueHeader("AB_Volume_m3_ha_" + year)).ToArray();
        string[] imaHeaders = ages.SelectMany(age => new[] { UniqueHeader($"AB_IMA_{age}_anos_m3_ha_ano"), UniqueHeader($"AB_IMA_{age}_anos_Data") }).ToArray();
        headers = [.. headers, .. (project ? new[] { statusHeader } : Array.Empty<string>()), .. volumeHeaders, .. imaHeaders,
            .. (project ? new[] { UniqueHeader("IMA7"), UniqueHeader("IMA7-Curva") } : Array.Empty<string>())];
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".ativo-" + Guid.NewGuid().ToString("N") + ".tmp");
        long count = 0, linked = 0, missing = 0, ambiguous = 0, projected = 0, imaRows = 0;
        long missingPlanting = 0, missingCurve = 0, invalidVolume = 0, fallbackLinked = 0, modelOnly = 0;
        try
        {
            using (var writer = new CompactBankWriter(temporary))
            {
                string table = writer.AddTable(headers, "Ativo biológico");
                foreach (var row in target.Rows())
                {
                    cancellation.ThrowIfCancellationRequested(); count++;
                    var resolved = Resolve(row);
                    var match = resolved.Match;
                    bool mixed = resolved.Mixed;
                    string state = match == null ? "Sem medição válida" : mixed ? "Chave reúne parcelas distintas"
                        : match.Conflict ? "Conflito na data mais recente" : "Vinculado";
                    bool valid = match is { Conflict: false } && !mixed;
                    if (match == null) missing++; else if (!valid) ambiguous++; else linked++;
                    if (valid && resolved.Alternative) fallbackLinked++;
                    bool model = project && options.UseModelWhenMissing && !valid;
                    object?[] result = [.. row, .. (valid ? match!.Values : new object?[fields.Length]),
                        match == null || model ? null : DateTime.FromOADate(match.Date).ToString("yyyy-MM-dd"), state,
                        model ? "Curva de crescimento e produção" : resolved.Origin, model ? "Modelo direto" : valid ? "Última medição real" : "Sem cálculo",
                        .. new object?[project ? 3 + years.Length + ages.Length * 2 : 0]];
                    int statusIndex = row.Length + fields.Length + 4;
                    if (project)
                    {
                        result[statusIndex] = state;
                        string curveCode = valid && match!.CurveId.Length > 0 ? match.CurveId : Key(row[curveIdCol]);
                        if (curves.TryGetValue(curveCode, out var summaryCurve))
                        {
                            double curveIma7 = Evaluate(summaryCurve, 7) / 7;
                            if (double.IsFinite(curveIma7) && curveIma7 >= 0) result[^1] = curveIma7;
                        }
                        if (valid || model) ProjectRow();
                    }
                    writer.Add(table, result);
                    if ((count & 2047) == 0) updates.Report(target.TotalRows > 0 ? 35 + 60d * count / target.TotalRows : null,
                        $"Vinculando e projetando ativo biológico: {count:N0} linhas", count);

                    void ProjectRow()
                    {
                        double planted = Date(row[plantingCol]);
                        if (!double.IsFinite(planted) || model && DateTime.FromOADate(planted).Year >= 9999)
                        { missingPlanting++; result[statusIndex] = "Data de plantio inválida"; return; }
                        DateTime observation = model ? DateTime.FromOADate(planted).AddYears(1) : DateTime.FromOADate(match!.Date);
                        if (!double.IsFinite(planted) || planted >= observation.ToOADate())
                        { missingPlanting++; result[statusIndex] = "Data de plantio/idade inválida"; return; }
                        string curveCode = valid && match!.CurveId.Length > 0 ? match.CurveId : Key(row[curveIdCol]);
                        if (!curves.TryGetValue(curveCode, out var curve))
                        { missingCurve++; result[statusIndex] = "ID sem curva vigente"; return; }
                        double y1 = 1;
                        if (!model && (!TryNumber(match!.Values[volumeField], out y1) || y1 <= 0))
                        { invalidVolume++; result[statusIndex] = "Volume observado inválido"; return; }
                        double f1 = model ? 1 : Evaluate(curve, AgeYears(planted, match!.Date));
                        if (!double.IsFinite(f1) || f1 <= 0)
                        { invalidVolume++; result[statusIndex] = "Curva inválida na idade de medição"; return; }
                        if (result[^1] is double curveIma7)
                        {
                            double ima7 = y1 * curveIma7 / f1;
                            if (double.IsFinite(ima7) && ima7 >= 0) result[^2] = ima7;
                        }
                        if (model) modelOnly++;
                        DateTime end = Reference(options.ThroughYear);
                        if (model && observation > end)
                        { result[statusIndex] = "Primeiro aniversário após a referência do ano final"; return; }
                        for (int index = 0; index < years.Length; index++)
                        {
                            if (years[index] < observation.Year || years[index] > options.ThroughYear && years[index] != observation.Year) continue;
                            double y = years[index] == observation.Year ? model ? Evaluate(curve, 1) : y1
                                : y1 * Evaluate(curve, AgeYears(planted, Reference(years[index]).ToOADate())) / f1;
                            if (!double.IsFinite(y) || y < 0) continue;
                            result[statusIndex + 1 + index] = y;
                            if (model || years[index] > observation.Year) projected++;
                        }
                        DateTime planting = DateTime.FromOADate(planted);
                        for (int index = 0; index < ages.Length; index++)
                        {
                            DateTime anniversary = planting.AddYears(ages[index]);
                            if (anniversary > end) continue;
                            double ima = y1 * Evaluate(curve, ages[index]) / f1 / ages[index];
                            if (!double.IsFinite(ima) || ima < 0) continue;
                            int column = statusIndex + 1 + years.Length + index * 2;
                            result[column] = ima;
                            result[column + 1] = anniversary.ToString("yyyy-MM-dd");
                            imaRows++;
                        }
                        result[statusIndex] = model ? "Calculado pelo modelo direto" : end <= observation ? "Ano final anterior à próxima referência anual" : "Calculado";
                    }
                }
                cancellation.ThrowIfCancellationRequested(); writer.Complete();
            }
            File.Move(temporary, output);
            updates.Report(100, $"Ativo biológico: {linked:N0} vinculados; {projected:N0} projeções; {imaRows:N0} valores de IMA.", count, true);
            return new(count, linked, missing, ambiguous, invalid, output, projected, imaRows, missingPlanting, missingCurve, invalidVolume, fallbackLinked, modelOnly);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool TryNumber(object? value, out double number)
    {
        if (value is double d && double.IsFinite(d)) { number = d; return true; }
        return double.TryParse(TableCursor.Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    private static double Evaluate(PreparedCurve curve, double ageYears)
    {
        double age = ageYears * (curve.Curve.AgeUnit == "meses" ? 12 : 1) * curve.Curve.AgeFactor;
        return curve.Formula.Evaluate(symbol =>
        {
            if (symbol is "x" or "x1" or "t") return age;
            if (curve.Curve.FixedInputs?.TryGetValue(symbol, out double fixedValue) == true) return fixedValue;
            if (symbol.Length >= 2 && symbol[0] == 'b' && int.TryParse(symbol.AsSpan(1), NumberStyles.None,
                CultureInfo.InvariantCulture, out int coefficient) && coefficient >= 0 && coefficient < curve.Curve.Coefficients.Length)
                return curve.Curve.Coefficients[coefficient];
            throw new InvalidDataException($"A entrada ‘{symbol}’ não está disponível na curva ‘{curve.Curve.Id}’.");
        });
    }
}
