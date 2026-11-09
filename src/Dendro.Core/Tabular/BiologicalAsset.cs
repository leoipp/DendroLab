using System.Globalization;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Tabular;

public sealed record BiologicalAssetCurve(string Id, string Formula, double[] Coefficients,
    string AgeUnit = "meses", double AgeFactor = 1, IReadOnlyDictionary<string, double>? FixedInputs = null);

public sealed record BiologicalAssetOptions(string BaseKey, string ProcessingKey, string MeasurementDate, string[] Columns,
    string Prefix = "INV_", string InventoryType = "", bool TrimKeys = true,
    string PlantingDate = "", string CurveId = "", string VolumeColumn = "", int ThroughYear = 0,
    BiologicalAssetCurve[]? Curves = null);

public sealed record BiologicalAssetResult(long BaseRows, long Linked, long Missing, long Ambiguous, long InvalidDates,
    string Path, long ProjectedRows = 0, long ImaRows = 0, long MissingPlantingDates = 0,
    long MissingCurves = 0, long InvalidVolumes = 0);

/// <summary>Left join by exact key, selecting the newest measurement. Conflicting latest ties never pick an arbitrary observation.</summary>
public static class BiologicalAsset
{
    private sealed record Latest(double Date, object?[] Values, bool Conflict);
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

        string[] headers = [.. target.Columns, .. fields.Select(c => options.Prefix + c), "AB_Data_Medicao", "AB_Vinculo"];
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
            throw new InvalidDataException("As colunas de saída já existem na base. Escolha outro prefixo ou outra tabela base.");

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

        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".ativo-" + Guid.NewGuid().ToString("N") + ".tmp");
        long count = 0, linked = 0, missing = 0, ambiguous = 0, projected = 0, imaRows = 0;
        long missingPlanting = 0, missingCurve = 0, invalidVolume = 0;
        try
        {
            using (var writer = new CompactBankWriter(temporary))
            {
                string table = writer.AddTable(headers, "Ativo biológico");
                string projections = "", ima = "";
                if (project)
                {
                    projections = writer.AddTable([options.BaseKey, "AB_ID_Curva", "AB_Data_Plantio", "AB_Data_Observacao",
                        "AB_Data_Projecao", "AB_Idade_anos", "AB_Volume_Observado_m3_ha", "AB_Volume_Projetado_m3_ha"], "Projeções");
                    ima = writer.AddTable([options.BaseKey, "AB_ID_Curva", "AB_Data_Plantio", "AB_Idade_anos", "AB_Data_Idade",
                        "AB_Volume_m3_ha", "AB_IMA_m3_ha_ano"], "IMA por idade");
                }
                var emitted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in target.Rows())
                {
                    cancellation.ThrowIfCancellationRequested(); count++;
                    string code = Key(row[baseKey]);
                    bool exists = code.Length > 0 && latest.TryGetValue(code, out _); var match = exists ? latest[code] : null;
                    bool mixed = mixedUnits.Contains(code);
                    string state = match == null ? "Sem medição válida" : mixed ? "Chave reúne parcelas distintas"
                        : match.Conflict ? "Conflito na data mais recente" : "Vinculado";
                    if (match == null) missing++; else if (match.Conflict || mixed) ambiguous++; else linked++;
                    writer.Add(table, [.. row, .. (match is { Conflict: false } && !mixed ? match.Values : new object?[fields.Length]),
                        match == null ? null : DateTime.FromOADate(match.Date).ToString("yyyy-MM-dd"), state]);

                    if (!project || match is not { Conflict: false } || mixed) continue;
                    double planted = Date(row[plantingCol]);
                    if (!double.IsFinite(planted)) { missingPlanting++; continue; }
                    string curveId = Key(row[curveIdCol]);
                    if (!curves.TryGetValue(curveId, out var curve)) { missingCurve++; continue; }
                    if (!TryNumber(match.Values[volumeField], out double y1) || !double.IsFinite(y1) || y1 <= 0)
                    { invalidVolume++; continue; }
                    double i1 = AgeYears(planted, match.Date);
                    if (!double.IsFinite(i1) || i1 <= 0) { missingPlanting++; continue; }
                    double f1 = Evaluate(curve, i1);
                    if (!double.IsFinite(f1) || f1 == 0) { invalidVolume++; continue; }

                    string standKey = System.Text.Json.JsonSerializer.Serialize(new[] { code, curveId, planted.ToString("G17", CultureInfo.InvariantCulture) });
                    if (!emitted.Add(standKey)) continue;
                    double observedVolume = y1;
                    DateTime observation = DateTime.FromOADate(match.Date);
                    DateTime lastProjectionDate = new(options.ThroughYear, 6, 1);
                    for (int year = observation.Year; year <= options.ThroughYear; year++)
                    {
                        DateTime projectionDate = new(year, 6, 1);
                        if (projectionDate <= observation) continue;
                        double i2 = AgeYears(planted, projectionDate.ToOADate());
                        if (i2 <= 0) continue;
                        double f2 = Evaluate(curve, i2);
                        double y2 = f2 / f1 * y1;
                        if (!double.IsFinite(y2) || y2 < 0) continue;
                        writer.Add(projections, [row[baseKey], curveId, DateTime.FromOADate(planted).ToString("yyyy-MM-dd"),
                            observation.ToString("yyyy-MM-dd"), projectionDate.ToString("yyyy-MM-dd"), i2, observedVolume, y2]);
                        projected++;
                    }

                    double endAge = AgeYears(planted, lastProjectionDate.ToOADate());
                    int maxAge = double.IsFinite(endAge) ? (int)Math.Floor(endAge) : 0;
                    for (int age = 1; age <= maxAge; age++)
                    {
                        double fAge = Evaluate(curve, age);
                        double volumeAtAge = fAge / f1 * y1;
                        double imaValue = volumeAtAge / age;
                        if (!double.IsFinite(volumeAtAge) || volumeAtAge < 0 || !double.IsFinite(imaValue)) continue;
                        DateTime ageDate = DateTime.FromOADate(planted).AddYears(age);
                        writer.Add(ima, [row[baseKey], curveId, DateTime.FromOADate(planted).ToString("yyyy-MM-dd"),
                            age, ageDate.ToString("yyyy-MM-dd"), volumeAtAge, imaValue]);
                        imaRows++;
                    }
                    if ((count & 2047) == 0) updates.Report(target.TotalRows > 0 ? 35 + 60d * count / target.TotalRows : null,
                        $"Vinculando e projetando ativo biológico: {count:N0} linhas", count);
                }

                string info = writer.AddTable(["Item", "Resultado"], "INFORMACOES");
                var details = new List<(string, object?)>
                {
                    ("Tabela base", basis.Path + " / " + basis.Table), ("Tabela processada", processed.Path + " / " + processed.Table),
                    ("Chaves", options.BaseKey + " = " + options.ProcessingKey), ("Data de medição", options.MeasurementDate),
                    ("Tipo de inventário", options.InventoryType.Length == 0 ? "Todos" : options.InventoryType),
                    ("Campos trazidos", string.Join("; ", fields)), ("Vinculados", linked), ("Sem medição válida", missing),
                    ("Conflitos na data mais recente", ambiguous), ("Linhas do processamento com data inválida", invalid),
                    ("Regra de vínculo", "Maior data de medição por chave. Empates com campos diferentes ficam sem valores vinculados."),
                };
                if (project)
                {
                    details.Add(("Coluna do ID da curva", options.CurveId)); details.Add(("Data de plantio", options.PlantingDate));
                    details.Add(("Volume observado", options.VolumeColumn)); details.Add(("Projetar até", new DateTime(options.ThroughYear, 6, 1).ToString("yyyy-MM-dd")));
                    details.Add(("Curvas ativas por ID", string.Join("; ", curves.Keys.Order(StringComparer.OrdinalIgnoreCase))));
                    details.Add(("Projeções anuais", projected)); details.Add(("Linhas de IMA", imaRows));
                    details.Add(("Sem data de plantio/idade válida", missingPlanting)); details.Add(("Sem curva vigente para o ID", missingCurve));
                    details.Add(("Volume ou curva inválidos", invalidVolume));
                    details.Add(("Regra de projeção", "Y2 = Y1 × F(I2)/F(I1); cada projeção anual usa 1º de junho e parte da última medição observada."));
                    details.Add(("Regra de IMA", "Para cada idade inteira até 1º de junho do ano final, V(idade) = Y1 × F(idade)/F(I1) e IMA = V(idade)/idade."));
                }
                foreach (var pair in details) writer.Add(info, [pair.Item1, pair.Item2]);
                cancellation.ThrowIfCancellationRequested(); writer.Complete();
            }
            File.Move(temporary, output);
            updates.Report(100, $"Ativo biológico: {linked:N0} vinculados; {projected:N0} projeções; {imaRows:N0} linhas de IMA.", count, true);
            return new(count, linked, missing, ambiguous, invalid, output, projected, imaRows, missingPlanting, missingCurve, invalidVolume);
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
