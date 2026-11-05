using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

/// <summary>Low-memory path for the direct summary when no row-mutating feature is enabled.</summary>
internal static class StreamingDirectProcessor
{
    private sealed record RegistryEntry(double PlantingDate, string? InvalidPlantingText, double EffectiveArea);

    private sealed class Identity
    {
        private readonly struct PlotKeyValue : IEquatable<PlotKeyValue>
        {
            private readonly string context;
            private readonly string? a, b, c, d;
            public PlotKeyValue(string context, string? a, string? b, string? c, string? d)
                => (this.context, this.a, this.b, this.c, this.d) = (context, a, b, c, d);
            public bool Equals(PlotKeyValue other) => StringComparer.Ordinal.Equals(context, other.context)
                && StringComparer.Ordinal.Equals(a, other.a) && StringComparer.Ordinal.Equals(b, other.b)
                && StringComparer.Ordinal.Equals(c, other.c) && StringComparer.Ordinal.Equals(d, other.d);
            public override bool Equals(object? value) => value is PlotKeyValue other && Equals(other);
            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(context, StringComparer.Ordinal); hash.Add(a, StringComparer.Ordinal);
                hash.Add(b, StringComparer.Ordinal); hash.Add(c, StringComparer.Ordinal);
                hash.Add(d, StringComparer.Ordinal);
                return hash.ToHashCode();
            }
        }

        private static readonly InventoryContext Empty = new("", "", "");
        private readonly TableCursor input;
        private readonly JsonObject config;
        private readonly int[] plotColumns;
        private readonly int[] originColumns;
        private readonly int dateColumn;
        private readonly int typeColumn;
        private readonly string defaultType;
        private readonly Dictionary<(string Type, string Origin), InventoryContext> contexts = new();
        private readonly Dictionary<PlotKeyValue, string> plots = new();
        private readonly Dictionary<string, string> extendedPlots = new(StringComparer.Ordinal);
        private readonly Dictionary<double, string> dates = new();
        private readonly string[] columns;

        public Identity(TableCursor input, JsonObject config)
        {
            this.input = input; this.config = config; columns = input.Columns;
            plotColumns = ResolveColumns("parcela");
            originColumns = ResolveOrigins();
            dateColumn = Array.FindIndex(columns, name => name == config.Obj("arquivo_entrada").Obj("colunas").S("data_medicao", "Data"));
            typeColumn = ResolveTypeColumn();
            defaultType = config.Obj("processador").S("inventory_type");
        }

        private int[] ResolveColumns(string key) => config.Obj("identificacao").Arr(key).Select(field =>
        {
            string name = Resolve(field.Display());
            int column = Array.FindIndex(input.Columns, candidate => candidate == name);
            return column >= 0 ? column : throw new InvalidDataException($"Coluna obrigatória ausente: {name}.");
        }).ToArray();

        private string Resolve(string name)
        {
            var map = config.Obj("arquivo_entrada").Obj("colunas");
            return map[name] is JsonValue ? map.S(name) : name;
        }

        private int[] ResolveOrigins() => config.Obj("identificacao").Arr("colunas_origem").Select(field =>
        {
            string name = Resolve(field.Display());
            int index = Array.FindIndex(input.Columns, candidate => candidate == name);
            if (index < 0 && name != "Fonte") throw new InvalidDataException($"Coluna de origem ausente: {name}. Revise Configurações > Identificação.");
            return index;
        }).Where(index => index >= 0).ToArray();

        private int ResolveTypeColumn()
        {
            string name = config.Obj("identificacao").S("coluna_tipo_inventario");
            if (name.Length > 0)
            {
                int configured = Array.FindIndex(input.Columns, candidate => candidate == name);
                return configured >= 0 ? configured : throw new InvalidDataException($"Coluna obrigatória ausente: {name}.");
            }
            return new[] { "Tipo_Inventario", "Tipo de inventário", "Tipo_Inventario_Origem" }
                .Select(candidate => Array.FindIndex(input.Columns, name => name == candidate)).FirstOrDefault(index => index >= 0, -1);
        }

        public InventoryContext Context(object?[] row, long rowNumber)
        {
            if (originColumns.Length == 0 && typeColumn < 0 && defaultType.Length == 0) return Empty;
            string[] origins = originColumns.Length > 1 ? originColumns.Select(column => Text(row[column])).ToArray() : [];
            string origin = originColumns.Length == 1 ? Text(row[originColumns[0]]) : string.Join(" · ", origins);
            string type = typeColumn >= 0 ? Text(row[typeColumn]).Trim().ToUpperInvariant() : "";
            if (typeColumn >= 0 && type is not ("IFC" or "IFPC"))
                throw new InvalidDataException($"Linha {rowNumber}: tipo de inventário '{type}' inválido. A coluna configurada deve conter IFC ou IFPC.");
            if (typeColumn < 0)
            {
                bool ifpc = origin.Contains("IFPC", StringComparison.OrdinalIgnoreCase), ifc = origin.Contains("IFC", StringComparison.OrdinalIgnoreCase);
                if (ifpc && ifc) throw new InvalidDataException($"Linha {rowNumber}: a origem contém IFC e IFPC. Configure uma coluna de tipo de inventário por linha.");
                type = ifpc ? "IFPC" : ifc ? "IFC" : defaultType;
                if (type == "MISTO") throw new InvalidDataException($"Linha {rowNumber}: não foi possível identificar IFC/IFPC pela origem. Configure a coluna de tipo em Identificação.");
            }
            string keyOrigin = origins.Length > 1 ? JsonSerializer.Serialize(origins) : origin;
            var key = (type, keyOrigin);
            if (!contexts.TryGetValue(key, out var context))
            {
                string id = InventoryIdentity.Hash(JsonSerializer.Serialize(new[] { key.Item1, key.Item2 }));
                contexts[key] = context = new(id, origin, type);
            }
            return context;
        }

        public string PlotKey(object?[] row, long rowNumber, InventoryContext context)
        {
            if (plotColumns.Length > 4)
            {
                var values = new string[plotColumns.Length];
                for (int i = 0; i < plotColumns.Length; i++) values[i] = PlotValue(row, i);
                string rawKey = JsonSerializer.Serialize(new[] { context.Id }.Concat(values));
                if (!extendedPlots.TryGetValue(rawKey, out string? extended))
                    extendedPlots[rawKey] = extended = InventoryIdentity.Hash(rawKey);
                return extended;
            }

            string? a = null, b = null, c = null, d = null;
            for (int i = 0; i < plotColumns.Length; i++)
            {
                string value = PlotValue(row, i);
                switch (i) { case 0: a = value; break; case 1: b = value; break; case 2: c = value; break; case 3: d = value; break; }
            }
            var key = new PlotKeyValue(context.Id, a, b, c, d);
            if (plots.TryGetValue(key, out string? stored)) return stored;
            string[] serialized = [context.Id, .. new[] { a, b, c, d }.Take(plotColumns.Length).Select(value => value ?? "")];
            return plots[key] = InventoryIdentity.Hash(JsonSerializer.Serialize(serialized));

            string PlotValue(object?[] values, int index)
            {
                int column = plotColumns[index];
                if (column != dateColumn) return Text(values[column]);
                string? raw = null;
                double date = values[column] is double serial ? serial : InventoryMath.Date(raw = Text(values[column]));
                if (!double.IsFinite(date))
                {
                    raw ??= Text(values[column]);
                    string displayed = raw.Length <= 100 ? raw : raw[..100] + "…";
                    throw new InvalidDataException($"Linha {rowNumber}: data de medição inválida na coluna '{columns[column]}' (valor: '{displayed}').");
                }
                if (!dates.TryGetValue(date, out string? formatted))
                    dates[date] = formatted = DateTime.FromOADate(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return formatted;
            }
        }
    }

    private sealed record AreaIssue(string Stand, string Plot, string Lot, string Date, string Length,
        string Width, string SlopeLength, string SlopeWidth, string Source, object? DateValue,
        object? LengthValue, object? WidthValue, object? SlopeLengthValue, object? SlopeWidthValue,
        long First, long Count, double Area, string Reason);

    public static ProcessingResult? TryProcess(ProcessingRequest request, JsonObject config,
        Action<int, string>? progress, CancellationToken cancellation)
    {
        JsonObject processing = config.Obj("processador");
        if (processing.S("modo", "completo") != "resumo_direto"
            || processing.B("remover_duplicadas")
            || config.At("ajuste_altura").S("h_mode", "KEEP_FILE") != "KEEP_FILE"
            || config.At("cadastro.fallback_idade").B("ativo")) return null;
        var equations = config.Obj("volume").Arr("equacoes").OfType<JsonObject>().Where(e => e.S("tipo_volume", "VTCC") == "VTCC").ToArray();
        if (equations.Length == 0 || equations.Any(e => e["entradas"] is not null || e.S("formula").Length > 0)) return null;
        _ = SiteIndexCalculator.Create(config);

        string inputPath = Path.GetFullPath(request.Input), registryPath = Path.GetFullPath(
            request.Registry.Length > 0 ? request.Registry : config.At("cadastro").S("caminho"));
        string output = Path.GetFullPath(request.Output);
        foreach (string source in new[] { inputPath, registryPath, Path.GetFullPath(request.Config) })
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A saída não pode sobrescrever uma entrada.");

        string extension = Path.GetExtension(output);
        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return null; // XLSX output requires the full workbook report path.
        if (!extension.Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O resultado deve usar .dlb ou .xlsx.");
        if (!Path.GetExtension(inputPath).Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase))
            return null; // SpreadsheetReader preserves blank source rows; use its established path for those inputs.
        cancellation.ThrowIfCancellationRequested();
        progress?.Invoke(10, "Abrindo inventário em fluxo…");

        var inputOptions = ProjectStore.ExcelOptions(config, inputPath);
        var inputSource = new TableSource(inputPath, request.Sheet.Length > 0 ? request.Sheet : inputOptions.S("sheet"),
            (int)inputOptions.N("header_row", 1));
        string[] sourceHeaders;
        using (var schema = new TableCursor(inputSource)) sourceHeaders = schema.Columns;
        if (sourceHeaders.Length == 0 || sourceHeaders.Any(string.IsNullOrWhiteSpace)
            || sourceHeaders.Distinct(StringComparer.Ordinal).Count() != sourceHeaders.Length)
            return null; // Keep established header normalization for ambiguous sheets.
        var inputMap = config.Obj("arquivo_entrada").Obj("colunas");
        string ResolveInput(string key, string fallback) => inputMap[key] is JsonValue ? inputMap.S(key) : fallback;
        string ResolveIdentity(string key) => inputMap[key] is JsonValue ? inputMap.S(key) : key;
        string[] areaColumnNames = [
            ResolveInput("talhao", "Talhão"), ResolveInput("parcela", "Parcela"), ResolveInput("lote", "Lote"),
            ResolveInput("data_medicao", "Data"), ResolveInput("comprimento_parcela", "Compr"),
            ResolveInput("largura_parcela", "Larg"), ResolveInput("declinacao_comprimento", "Dec_Compr"),
            ResolveInput("declinacao_largura", "Dec_Larg")
        ];
        if (sourceHeaders.Contains("Fonte", StringComparer.Ordinal)) areaColumnNames = [.. areaColumnNames, "Fonte"];
        TableCursor OpenInput(IEnumerable<string> requestedColumns)
        {
            int[] selected = requestedColumns.Distinct(StringComparer.Ordinal)
                .Select(name => Array.IndexOf(sourceHeaders, name)).Where(index => index >= 0).ToArray();
            return selected.Length > 0 ? new TableCursor(inputSource, selected) : new TableCursor(inputSource);
        }
        string[] inputColumns = sourceHeaders;
        var columns = config.Obj("arquivo_entrada").Obj("colunas");
        int Find(string name) => Array.FindIndex(inputColumns, candidate => candidate == name);
        int Require(string name) => Find(name) is int index && index >= 0 ? index : throw new InvalidDataException($"Coluna obrigatória ausente: {name}.");
        int Map(string key, string fallback) => Require(columns.S(key, fallback));
        int standColumn, plotColumn, lotColumn, dapColumn, heightColumn, dateColumn, lengthColumn, widthColumn,
            slopeLengthColumn, slopeWidthColumn, registryKeyColumn;
        bool blankLengthZero = config.At("arquivo_entrada.declinacoes").B("comprimento_vazio_zero"),
            blankWidthZero = config.At("arquivo_entrada.declinacoes").B("largura_vazio_zero");
        var issues = new Dictionary<(string Stand, string Plot, string Lot, string Date, string Length, string Width,
            string SlopeLength, string SlopeWidth, string Source), AreaIssue>();
        long validatedRows = 0;
        int sourceColumn;
        using (var areaInput = OpenInput(areaColumnNames))
        {
            if (areaInput.TotalRows is 0) throw new InvalidDataException("O inventário não contém árvores.");
            inputColumns = areaInput.Columns;
            standColumn = Map("talhao", "Talhão"); plotColumn = Map("parcela", "Parcela"); lotColumn = Map("lote", "Lote");
            dateColumn = Map("data_medicao", "Data");
            lengthColumn = Map("comprimento_parcela", "Compr"); widthColumn = Map("largura_parcela", "Larg");
            slopeLengthColumn = Map("declinacao_comprimento", "Dec_Compr"); slopeWidthColumn = Map("declinacao_largura", "Dec_Larg");
            sourceColumn = Find("Fonte");
            foreach (var row in areaInput.Rows())
            {
                validatedRows++;
                if ((validatedRows & 8191) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Invoke(16, $"Verificando área das parcelas: {validatedRows:N0} de {areaInput.TotalRows?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} linhas…");
                }
                double sl = Number(row[slopeLengthColumn]), sw = Number(row[slopeWidthColumn]);
                object? slopeLengthValue = blankLengthZero && string.IsNullOrWhiteSpace(Text(row[slopeLengthColumn])) ? 0d : row[slopeLengthColumn];
                object? slopeWidthValue = blankWidthZero && string.IsNullOrWhiteSpace(Text(row[slopeWidthColumn])) ? 0d : row[slopeWidthColumn];
                if (blankLengthZero && string.IsNullOrWhiteSpace(Text(row[slopeLengthColumn]))) sl = 0;
                if (blankWidthZero && string.IsNullOrWhiteSpace(Text(row[slopeWidthColumn]))) sw = 0;
                double length = Number(row[lengthColumn]), width = Number(row[widthColumn]);
                double area = InventoryMath.Area(length, width, sl, sw);
                if (area > 0 && double.IsFinite(area)) continue;
                string stand = Text(row[standColumn]), plot = Text(row[plotColumn]), lot = Text(row[lotColumn]);
                var reasons = new List<string>();
                void Check(double value, string name, bool positive)
                {
                    if (!double.IsFinite(value)) reasons.Add(name + " vazio, não numérico ou fora do intervalo.");
                    else if (positive && value <= 0) reasons.Add(name + " menor ou igual a zero.");
                }
                Check(length, "Comprimento", true); Check(width, "Largura", true);
                Check(sl, "Declinação no comprimento", false); Check(sw, "Declinação na largura", false);
                if (reasons.Count == 0) reasons.Add(double.IsFinite(area) ? "A correção pelas declinações resultou em área nula ou negativa." : "O cálculo resultou em área não finita.");
                var key = (stand, plot, lot, Text(row[dateColumn]), Text(row[lengthColumn]), Text(row[widthColumn]),
                    Text(slopeLengthValue), Text(slopeWidthValue), sourceColumn >= 0 ? Text(row[sourceColumn]) : "");
                if (issues.TryGetValue(key, out var previous)) issues[key] = previous with { Count = previous.Count + 1 };
                else issues.Add(key, new(stand, plot, lot, key.Item4, key.Item5, key.Item6, key.Item7, key.Item8,
                    key.Item9, row[dateColumn], row[lengthColumn], row[widthColumn], slopeLengthValue, slopeWidthValue,
                    validatedRows + 1, 1, area, string.Join(" ", reasons)));
            }
            if (validatedRows == 0) throw new InvalidDataException("O inventário não contém árvores.");
            if (issues.Count > 0) throw AreaError(issues, inputColumns, lengthColumn, widthColumn, slopeLengthColumn, slopeWidthColumn);
        }
        if (sourceHeaders.Contains(RegistryAgeFallback.FlagColumn, StringComparer.Ordinal)
            || sourceHeaders.Contains(RegistryAgeFallback.DateColumn, StringComparer.Ordinal))
            return null; // Existing fallback columns are copied into the direct tree sheet.

        var cadastro = config.Obj("cadastro");
        string registryKeyName = cadastro.S("chave", "Talhão"), plantName = cadastro.At("colunas").S("data_plantio", "Data de Plantio"),
            effectiveName = cadastro.At("colunas").S("area_efetiva", "Área efetiva (ha)");
        var registryOptions = ProjectStore.ExcelOptions(config, registryPath);
        using var registryCursor = TableCursor.Projected(new(registryPath, registryOptions.S("sheet", cadastro.S("worksheet", "Data")),
            (int)registryOptions.N("header_row", 1)), [registryKeyName, plantName, effectiveName]);
        int regKey = Array.FindIndex(registryCursor.Columns, name => name == registryKeyName),
            regPlant = Array.FindIndex(registryCursor.Columns, name => name == plantName),
            regArea = Array.FindIndex(registryCursor.Columns, name => name == effectiveName);
        if (regKey < 0) throw new InvalidDataException($"Coluna obrigatória ausente no cadastro: {registryKeyName}.");
        if (regPlant < 0) throw new InvalidDataException($"Coluna obrigatória ausente no cadastro: {plantName}.");
        if (regArea < 0) throw new InvalidDataException($"Coluna obrigatória ausente no cadastro: {effectiveName}.");
        var registry = new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);
        foreach (var row in registryCursor.Rows())
        {
            string key = Text(row[regKey]);
            double planting = InventoryMath.Date(Text(row[regPlant]));
            string? invalidPlanting = double.IsFinite(planting) ? null : Text(row[regPlant]);
            if (key.Length > 0 && !registry.TryAdd(key, new(planting, invalidPlanting, Number(row[regArea]))))
                throw new InvalidDataException($"Cadastro com chave duplicada: {key}. Revise antes de processar.");
        }

        var processingColumns = areaColumnNames
            .Concat([ResolveInput("dap", "DAP"), ResolveInput("altura", "H"), config.Obj("arquivo_entrada").S("chave", "Talhão")])
            .Concat(config.Obj("identificacao").Arr("parcela").Select(field => ResolveIdentity(field.Display())))
            .Concat(config.Obj("identificacao").Arr("colunas_origem").Select(field => ResolveIdentity(field.Display())))
            .Append(config.At("identificacao").S("coluna_tipo_inventario"))
            .Concat(new[] { "Tipo_Inventario", "Tipo de inventário", "Tipo_Inventario_Origem" })
            .ToArray();
        using var input = OpenInput(processingColumns);
        inputColumns = input.Columns;
        standColumn = Map("talhao", "Talhão"); plotColumn = Map("parcela", "Parcela"); lotColumn = Map("lote", "Lote");
        dapColumn = Map("dap", "DAP"); heightColumn = Map("altura", "H"); dateColumn = Map("data_medicao", "Data");
        lengthColumn = Map("comprimento_parcela", "Compr"); widthColumn = Map("largura_parcela", "Larg");
        slopeLengthColumn = Map("declinacao_comprimento", "Dec_Compr"); slopeWidthColumn = Map("declinacao_largura", "Dec_Larg");
        registryKeyColumn = Require(config.Obj("arquivo_entrada").S("chave", "Talhão"));
        sourceColumn = Find("Fonte");
        var identity = new Identity(input, config);
        using var summary = new DirectInventorySummary(output, config);
        double dapScale = config.At("arquivo_entrada.unidades").S("dap", "mm") == "cm" ? 10 : 1;
        double heightScale = config.At("arquivo_entrada.unidades").S("altura", "dm") switch { "cm" => .1, "m" => 10, _ => 1 };
        double ageLimit = config.At("volume").N("idade_limite_equacao_geral", 15), factor = config.At("volume").N("fator", 1);
        var models = equations.ToDictionary(e => e.S("codigo"), e => e!, StringComparer.Ordinal);
        bool temporal = config.At("estatisticas").B("chave_temporal_ativa");
        long rowNumber = 1, rows = 0;
        foreach (var row in input.Rows())
        {
            rowNumber++;
            if ((rows & 8191) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(30, $"Processando inventário: {rows:N0} de {input.TotalRows?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} linhas…");
            }
            rows++;
            string stand = Text(row[standColumn]), plot = Text(row[plotColumn]), lot = Text(row[lotColumn]);
            if (stand.Length == 0 || plot.Length == 0 || lot.Length == 0)
                throw new InvalidDataException($"Linha {rowNumber}: talhão, parcela ou lote vazio.");
            double sl = Number(row[slopeLengthColumn]), sw = Number(row[slopeWidthColumn]);
            if (blankLengthZero && string.IsNullOrWhiteSpace(Text(row[slopeLengthColumn]))) sl = 0;
            if (blankWidthZero && string.IsNullOrWhiteSpace(Text(row[slopeWidthColumn]))) sw = 0;
            double area = InventoryMath.Area(Number(row[lengthColumn]), Number(row[widthColumn]), sl, sw);
            string registryKeyValue = Text(row[registryKeyColumn]);
            if (!registry.TryGetValue(registryKeyValue, out var registryEntry))
                throw new InvalidDataException($"Linha {rowNumber}: chave não encontrada no cadastro: {registryKeyValue}.");
            double planting = registryEntry.PlantingDate, measured = InventoryMath.Date(Text(row[dateColumn]));
            if (!double.IsFinite(planting) || !double.IsFinite(measured))
            {
                var invalid = new List<string>();
                if (!double.IsFinite(planting)) invalid.Add($"plantio na coluna '{plantName}' (valor: '{registryEntry.InvalidPlantingText}')");
                if (!double.IsFinite(measured)) invalid.Add($"medição na coluna '{input.Columns[dateColumn]}' (valor: '{Text(row[dateColumn])}')");
                throw new InvalidDataException($"Linha {rowNumber}: data inválida de {string.Join(" e ", invalid)}.");
            }
            double age = Math.Floor(measured - planting) / 365.25;
            string code = InventoryMath.EquationCode(lot, age, ageLimit), curve = InventoryMath.EquationCode(lot, 0);
            if (!models.TryGetValue(code, out var equation))
                throw new InvalidDataException($"Linha {rowNumber}: equação {code} não encontrada na configuração.");
            if (!(area > 0 && double.IsFinite(area))) throw new InvalidDataException($"Linha {rowNumber}: área corrigida inválida.");
            double dap = Number(row[dapColumn]) * dapScale, height = Number(row[heightColumn]) * heightScale,
                effective = registryEntry.EffectiveArea;
            double volume = InventoryMath.Volume(dap, height, equation.N("b0"), equation.N("b1"), equation.N("b2")) * factor;
            if (double.IsInfinity(volume)) throw new InvalidDataException($"Linha {rowNumber}: volume fora do intervalo numérico.");
            string measurement = DateTime.FromOADate(measured).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            InventoryContext context = identity.Context(row, rowNumber);
            string plotId = identity.PlotKey(row, rowNumber, context);
            summary.Add(stand, plot, lot, temporal ? InventoryMath.Year(measured) : 0, rowNumber, age,
                volume, dap > 0, area, effective, "", "", dap, height, plotId, context, measurement, curve);
        }
        cancellation.ThrowIfCancellationRequested();
        if (rows == 0) throw new InvalidDataException("O inventário não contém árvores.");
        progress?.Invoke(85, "Gravando somente os resumos selecionados…");
        ProcessingResult result = summary.Complete();
        progress?.Invoke(100, "Resumo direto concluído.");
        return result;
    }

    private static InvalidDataException AreaError(
        Dictionary<(string Stand, string Plot, string Lot, string Date, string Length, string Width, string SlopeLength, string SlopeWidth, string Source), AreaIssue> issues,
        string[] columns, int length, int width, int slopeLength, int slopeWidth)
    {
        var details = new DataTable(["Primeira linha processada", "Talhão", "Parcela", "Lote", "Data de medição", "Fonte",
            "Coluna de comprimento", "Comprimento original", "Coluna de largura", "Largura original", "Coluna de declinação comprimento", "Declinação comprimento original",
            "Coluna de declinação largura", "Declinação largura original", "Área corrigida (m²)", "Ocorrências", "Motivo"]);
        foreach (var issue in issues.Values.OrderBy(item => item.First))
            details.Add(issue.First.ToString(CultureInfo.InvariantCulture), issue.Stand, issue.Plot, issue.Lot, issue.DateValue, issue.Source,
                columns[length], issue.LengthValue, columns[width], issue.WidthValue, columns[slopeLength], issue.SlopeLengthValue,
                columns[slopeWidth], issue.SlopeWidthValue, double.IsFinite(issue.Area) ? issue.Area : null,
                issue.Count.ToString(CultureInfo.InvariantCulture), issue.Reason);
        var first = issues.First();
        string values = $"{columns[length]}='{first.Key.Length}', {columns[width]}='{first.Key.Width}', {columns[slopeLength]}='{first.Key.SlopeLength}', {columns[slopeWidth]}='{first.Key.SlopeWidth}'";
        var error = new InvalidDataException($"Área corrigida inválida: {issues.Count:N0} pendência(s), {issues.Values.Sum(issue => issue.Count):N0} linha(s). "
            + $"Primeira: linha {first.Value.First:N0}, talhão '{first.Key.Stand}', parcela '{first.Key.Plot}'. {values}. {first.Value.Reason}");
        error.Data[ParcelAreaValidation.DiagnosticsKey] = details;
        return error;
    }

    private static string Text(object? value) => value switch
    {
        double number when double.IsFinite(number) => number.ToString("G17", CultureInfo.InvariantCulture),
        float number => number.ToString("G9", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value?.ToString() ?? "",
    };

    private static double Number(object? value)
    {
        if (value is double number) return number;
        if (value is int integer) return integer;
        if (value is long longInteger) return longInteger;
        string text = Text(value).Trim();
        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed : double.NaN;
    }
}
