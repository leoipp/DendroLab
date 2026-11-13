using System.Text.Json.Nodes;
using System.Globalization;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.NeuralNetworks;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

public sealed record ProcessingRequest(
    string Input,
    string Config,
    string Registry,
    string Output,
    string Sheet = ""
);

public sealed record ProcessingResult(int Trees, int Plots, int Stands, double Volume, int RemovedDuplicates = 0, string[]? InventoryTypes = null);

public sealed class InventoryProcessor
{
    private sealed record ProjectedInput(
        DataTable Data,
        int SourceWidth,
        string[] SourceColumns,
        Dendro.Core.Tabular.CompactBankReader Reader,
        Dendro.Core.Tabular.CompactTable Table
    );

    private sealed class CompactTableSpool : IDisposable
    {
        private readonly string path;
        private readonly string tableId;
        private Dendro.Core.Tabular.CompactBankWriter? writer;

        public CompactTableSpool(string directory, string[] columns, string name)
        {
            path = Path.Combine(directory, ".dendrolab-spool-" + Guid.NewGuid().ToString("N") + ".dlb");
            writer = new(path);
            tableId = writer.AddTable(columns, name);
        }

        public void AddOwned(object?[] row) => (writer ?? throw new InvalidOperationException("Spool já concluído.")).AddOwned(tableId, row);

        public Dendro.Core.Tabular.CompactBankReader Complete()
        {
            var activeWriter = writer ?? throw new InvalidOperationException("Spool já concluído.");
            activeWriter.Complete();
            activeWriter.Dispose();
            writer = null;
            return new(path);
        }

        public Dendro.Core.Tabular.CompactTable Table(Dendro.Core.Tabular.CompactBankReader reader) => reader.Table(tableId);

        public void Dispose()
        {
            writer?.Dispose();
            writer = null;
            Dendro.Core.Tabular.CompactBankReader.InvalidateCatalog(path);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private readonly record struct TreeRow(int InputRow, int RegistryRow);

    private readonly record struct Tree(
        int Row,
        int RegistryRow,
        string Stand,
        int Year,
        string Plot,
        string Lot,
        double Dap,
        double Height,
        double Age,
        double Area,
        double Effective,
        string Code,
        string Curve,
        string Measurement,
        string PlotIdentity,
        InventoryContext Inventory,
        double Volume
    );

    private sealed class PlotAggregate(Tree first, int dominantTreesPerPlot)
    {
        public Tree First { get; } = first;
        public DominantTreeMetrics Dominant { get; } = new(dominantTreesPerPlot);
        public int Rows, Stems, DapCount, HeightCount, EffectiveCount, AreaCount, AgeCount, VolumeCount;
        public double Volume, VolumeSum, AreaSum, AgeSum, EffectiveSum, DapSum, HeightSum;

        public void Add(Tree tree)
        {
            if (First.Stand != tree.Stand || First.Lot != tree.Lot || First.Plot != tree.Plot)
                throw new InvalidDataException("A chave de parcela está agrupando talhões, lotes ou números de parcela diferentes. Revise as colunas da chave em Configurações > Identificação.");
            Rows++;
            if (!double.IsNaN(tree.Volume)) { Volume += tree.Volume; VolumeSum += tree.Volume; VolumeCount++; }
            if (!double.IsNaN(tree.Area)) { AreaSum += tree.Area; AreaCount++; }
            if (!double.IsNaN(tree.Age)) { AgeSum += tree.Age; AgeCount++; }
            if (!double.IsNaN(tree.Effective)) { EffectiveSum += tree.Effective; EffectiveCount++; }
            if (!double.IsNaN(tree.Dap)) { DapSum += tree.Dap; DapCount++; }
            if (!double.IsNaN(tree.Height)) { HeightSum += tree.Height; HeightCount++; }
            if (tree.Dap > 0) Stems++;
            Dominant.Add(tree.Dap, tree.Height);
        }

        public double MeanArea => AreaCount > 0 ? AreaSum / AreaCount : double.NaN;
        public double MeanAge => AgeCount > 0 ? AgeSum / AgeCount : double.NaN;
        public double MeanEffective => EffectiveCount > 0 ? EffectiveSum / EffectiveCount : double.NaN;
        public double MeanDap => DapCount > 0 ? DapSum / DapCount : double.NaN;
        public double MeanHeight => HeightCount > 0 ? HeightSum / HeightCount : double.NaN;
        public double MeanVolume => VolumeCount > 0 ? VolumeSum / VolumeCount : double.NaN;
    }

    private static readonly string[] ColumnKeys =
    [
        "talhao",
        "parcela",
        "lote",
        "dap",
        "altura",
        "data_medicao",
        "comprimento_parcela",
        "largura_parcela",
        "declinacao_comprimento",
        "declinacao_largura",
    ];
    private static readonly string[] ColumnDefaults =
    [
        "Talhão",
        "Parcela",
        "Lote",
        "DAP",
        "H",
        "Data",
        "Compr",
        "Larg",
        "Dec_Compr",
        "Dec_Larg",
    ];

    public ProcessingResult Process(ProcessingRequest request, Action<int, string>? progress = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string output = Path.GetFullPath(request.Output),
            inputPath = Path.GetFullPath(request.Input);
        if (File.Exists(output))
            throw new IOException(
                "A saída já existe. Escolha outro nome para preservar o relatório anterior."
            );
        bool compact = output.EndsWith(Dendro.Core.Tabular.CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase);
        if (!compact && !output.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O resultado deve usar .dlb ou .xlsx.");
        progress?.Invoke(3, "Lendo configuração do inventário…");
        var cfg = Dendro.Core.Tabular.CompactBankFile.IsCompactBank(request.Config)
            ? ProjectStore.Read(request.Config)
            : JsonNode.Parse(File.ReadAllText(request.Config))?.AsObject()
                ?? throw new InvalidDataException("Arquivo de configuração do processamento vazio ou inválido.");
        ProjectStore.Validate(cfg);
        ProcessingResult? streamed = StreamingDirectProcessor.TryProcess(request, cfg, progress, cancellation);
        if (streamed != null) return streamed;
        var height = cfg["ajuste_altura"]!;
        int dominantTreesPerPlot = (int)cfg.At("processador").N("n_dominantes_parcela", 5);
        var siteIndex = SiteIndexCalculator.Create(cfg);
        string mode = height.S("h_mode", "KEEP_FILE");
        bool useRna = mode is "MODEL_GENERAL" or "FINETUNE_MODEL";
        string registryPath = Path.GetFullPath(request.Registry.Length > 0
            ? request.Registry : cfg.At("cadastro").S("caminho"));
        if (mode != "KEEP_FILE" && !useRna)
            throw new InvalidDataException(
                $"O modo {mode} ainda não está disponível. Use KEEP_FILE, MODEL_GENERAL ou FINETUNE_MODEL."
            );
        var equations = cfg["volume"]!["equacoes"]!.AsArray().Where(e => e.S("tipo_volume", "VTCC") == "VTCC").ToArray();
        if (equations.Length < 1)
            throw new InvalidDataException("Configure os modelos volumétricos VTCC antes de processar.");
        progress?.Invoke(10, "Lendo inventário…");
        var options = ProjectStore.ExcelOptions(cfg, inputPath);
        string selectedSheet = request.Sheet.Length > 0 ? request.Sheet : options.S("sheet");
        NeuralModel? heightModel = useRna ? NeuralModel.Load(height.At("modelo_geral").S("caminho")) : null;
        ProjectedInput? projectedInput = ReadProjectedCompactInput(inputPath, selectedSheet, cfg, heightModel, registryPath, compact);
        using var projectedReader = projectedInput?.Reader;
        var input = projectedInput?.Data ?? (Dendro.Core.Tabular.TableStore.IsBank(inputPath)
            ? Dendro.Core.Tabular.TableStore.Read(new(inputPath, selectedSheet))
            : SpreadsheetReader.Read(
            inputPath,
            selectedSheet,
            (int)options.N("header_row", 1)
        ));
        if (input.Count < 2)
            throw new InvalidDataException("O inventário não contém árvores.");
        bool streamAdjustedInput = projectedInput != null && compact
            && cfg.At("saida.sheets").B("ajustado", true);
        int[]? originalSourceRows = null;
        var colCfg = cfg["arquivo_entrada"]?["colunas"];
        int[] c = ColumnKeys
            .Select(
                (key, i) =>
                    i == 4 && useRna
                        ? input.Ensure(colCfg.S(key, ColumnDefaults[i]))
                        : input.Require(colCfg.S(key, ColumnDefaults[i]))
            )
            .ToArray();
        NormalizeInputUnits(input, c[3], c[4], cfg);
        int original = projectedInput?.SourceWidth ?? input.Width;
        bool directMode = cfg["processador"].S("modo", "completo") == "resumo_direto";
        using var direct = directMode ? new DirectInventorySummary(output, cfg) : null;
        bool versioned = !directMode && ProcessingHistory.Enabled(cfg);
        var areaColumns = cfg.At("arquivo_entrada.colunas");
        int lengthColumn = input.Require(areaColumns.S("comprimento_parcela", "Compr")),
            widthColumn = input.Require(areaColumns.S("largura_parcela", "Larg")),
            slopeLengthColumn = input.Require(areaColumns.S("declinacao_comprimento", "Dec_Compr")),
            slopeWidthColumn = input.Require(areaColumns.S("declinacao_largura", "Dec_Larg"));
        bool removeDuplicateRows = cfg["processador"].B("remover_duplicadas");
        var identification = new InventoryIdentity(input, cfg, versioned || removeDuplicateRows);
        int[] identityColumns = versioned ? new[] { "cova", "fuste", "fila" }.Select(key => input.Column(colCfg.S(key, key == "cova" ? "Cova" : key == "fuste" ? "Fuste" : "Fila"))).ToArray() : [];
        string TreeIdentity(int row) => identification.TreeKey(row);
        int removedDuplicates = 0;
        if (versioned || removeDuplicateRows)
        {
            var identities = new Dictionary<InventoryKeyFingerprint, int>();
            bool[]? keepRows = removeDuplicateRows ? new bool[input.Count] : null;
            bool remove = removeDuplicateRows;
            int received = input.Count - 1;
            int areaConflicts = 0, dapConflicts = 0, heightConflicts = 0;
            double worstAreaRatio = 1;
            string worstAreaExample = "";
            static bool ConflictingMeasurement(double left, double right)
            {
                bool hasLeft = double.IsFinite(left) && left > 0;
                bool hasRight = double.IsFinite(right) && right > 0;
                if (hasLeft != hasRight) return true;
                return hasLeft && Math.Abs(left - right) > Math.Max(1e-9, Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-9);
            }
            double PlotAreaAt(int row)
            {
                double sl = input.Number(row, slopeLengthColumn), sw = input.Number(row, slopeWidthColumn);
                if (!double.IsFinite(sl) && cfg.At("arquivo_entrada.declinacoes").B("comprimento_vazio_zero")) sl = 0;
                if (!double.IsFinite(sw) && cfg.At("arquivo_entrada.declinacoes").B("largura_vazio_zero")) sw = 0;
                return InventoryMath.Area(input.Number(row, lengthColumn), input.Number(row, widthColumn), sl, sw);
            }
            for (int row = 1; row < input.Count; row++)
            {
                if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                var identity = identification.TreeFingerprint(row);
                if (identities.TryGetValue(identity, out int firstRow))
                {
                    if (!remove) throw new InvalidDataException($"Linha {row + 1}: identificação de árvore duplicada conforme Configurações > Identificação. Para processar mantendo a primeira ocorrência, marque Remover árvores duplicadas no Processador.");
                    removedDuplicates++;
                    double firstArea = PlotAreaAt(firstRow), rowArea = PlotAreaAt(row);
                    if (ConflictingMeasurement(firstArea, rowArea))
                    {
                        areaConflicts++;
                        double ratio = double.IsFinite(firstArea) && double.IsFinite(rowArea) && firstArea > 0 && rowArea > 0
                            ? Math.Max(firstArea, rowArea) / Math.Min(firstArea, rowArea)
                            : double.PositiveInfinity;
                        if (ratio > worstAreaRatio)
                        {
                            worstAreaRatio = ratio;
                            worstAreaExample = $"linhas {firstRow + 1:N0} e {row + 1:N0}: Compr {input.Text(firstRow, lengthColumn)} × Larg {input.Text(firstRow, widthColumn)} (área {firstArea.ToString("G", CultureInfo.GetCultureInfo("pt-BR"))} m²) versus Compr {input.Text(row, lengthColumn)} × Larg {input.Text(row, widthColumn)} (área {rowArea.ToString("G", CultureInfo.GetCultureInfo("pt-BR"))} m²), talhão '{input.Text(row, c[0])}', parcela '{input.Text(row, c[1])}', lote '{input.Text(row, c[2])}'.";
                        }
                    }
                    if (ConflictingMeasurement(input.Number(firstRow, c[3]), input.Number(row, c[3]))) dapConflicts++;
                    if (ConflictingMeasurement(input.Number(firstRow, c[4]), input.Number(row, c[4]))) heightConflicts++;
                }
                else
                {
                    identities.Add(identity, row);
                    if (keepRows != null) keepRows[row] = true;
                }
            }
            if (remove && areaConflicts > 0)
            {
                string areaDetail = worstAreaExample.Length > 0 ? " Maior divergência de área: " + worstAreaExample : "";
                throw new InvalidDataException($"Não processei: {removedDuplicates:N0} linhas repetem uma chave de árvore, mas há dados conflitantes entre as ocorrências ({areaConflicts:N0} com área da parcela diferente, {dapConflicts:N0} com DAP diferente e {heightConflicts:N0} com H diferente). Remover duplicadas manteria a primeira linha e poderia distorcer volume e área por hectare.{areaDetail} Revise essas linhas na base de entrada e processe novamente.");
            }
            if (keepRows != null)
            {
                if (streamAdjustedInput)
                {
                    int retainedCount = 1;
                    for (int row = 1; row < keepRows.Length; row++) if (keepRows[row]) retainedCount++;
                    originalSourceRows = new int[retainedCount];
                }
                if (originalSourceRows != null)
                {
                    int retained = 1;
                    for (int row = 1; row < keepRows.Length; row++)
                        if (keepRows[row]) originalSourceRows[retained++] = row;
                }
                input.RetainRows(keepRows);
            }
            if (versioned)
            {
                var run = cfg.Obj("processador").Obj("execucao");
                run["input_rows"] = received;
                run["removed_duplicates"] = removedDuplicates;
                run["duplicate_policy"] = remove ? "keep_first" : "reject";
            }
            if (removedDuplicates > 0) progress?.Invoke(15, $"Duplicadas removidas: {removedDuplicates:N0}. Mantida a primeira ocorrência de cada árvore e data de medição.");
        }
        progress?.Invoke(16, "Verificando dimensões e áreas das parcelas…");
        // Validation already scans every row; keep no second full-size area array.
        // The normalized slope cells are written during validation, so each value
        // can be recomputed cheaply during the processing pass.
        _ = ParcelAreaValidation.Compute(input, cfg, cancellation, progress, retainAreas: false);
        var cad = cfg["cadastro"];
        var cadCols = cad?["colunas"];
        foreach (
            string source in new[] { inputPath, registryPath, Path.GetFullPath(request.Config) }
        )
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A saída não pode sobrescrever uma entrada.");
        progress?.Invoke(20, "Lendo cadastro florestal…");
        var regOptions = ProjectStore.ExcelOptions(cfg, registryPath);
        var registry = Dendro.Core.Tabular.TableStore.IsBank(registryPath)
            ? Dendro.Core.Tabular.TableStore.Read(new(registryPath, regOptions.S("sheet", cad.S("worksheet", "Data"))))
            : SpreadsheetReader.Read(
            registryPath,
            regOptions.S("sheet", cad.S("worksheet", "Data")),
            (int)regOptions.N("header_row", 1)
        );
        int inferredPlantings = RegistryAgeFallback.Apply(input, registry, cfg, cancellation, progress);
        if (inferredPlantings > 0) progress?.Invoke(22, $"Fallback de idade: {inferredPlantings} chave(s) com plantio inferido pelo código.");
        var index = FeaturePreparation.RegistryIndex(registry, cad.S("chave", "Talhão"));
        int ik = input.Require(cfg["arquivo_entrada"].S("chave", "Talhão")),
            cp = registry.Require(cadCols.S("data_plantio", "Data de Plantio")),
            ca = registry.Require(cadCols.S("area_efetiva", "Área efetiva (ha)"));
        int RegistryRow(int row) =>
            index.TryGetValue(input.Text(row, ik), out int rr)
                ? rr
                : throw new InvalidDataException(
                    $"Linha {row + 1}: chave não encontrada no cadastro: {input.Text(row, ik)}."
                );
        if (useRna)
        {
            progress?.Invoke(
                25,
                "Executando RNA: variáveis, categorias, normalização e camadas Dense…"
            );
            HeightProcessor.Apply(input, registry, cfg, heightModel, cancellation, progress);
            string areaName = cadCols.S("area_efetiva", "Área efetiva (ha)");
            if (input.Column(areaName) < 0)
            {
                int area = input.Ensure(areaName);
                for (int row = 1; row < input.Count; row++)
                    input.Set(row, area, registry.Value(RegistryRow(row), ca));
            }
        }
        else
        {
            string plantedName = cadCols.S("data_plantio", "Data de Plantio"),
                areaName = cadCols.S("area_efetiva", "Área efetiva (ha)");
            bool plantedMissing = input.Column(plantedName) < 0,
                areaMissing = input.Column(areaName) < 0;
            int planted = input.Ensure(plantedName),
                area = input.Ensure(areaName),
                obs = input.Ensure("H_obs"),
                est = input.Ensure("H_est"),
                year = input.Ensure("Ano_Medicao");
            for (int r = 1; r < input.Count; r++)
            {
                int rr = RegistryRow(r);
                if (plantedMissing)
                    input.Set(r, planted, registry.Value(rr, cp));
                if (areaMissing)
                    input.Set(r, area, registry.Value(rr, ca));
                input.Set(r, obs, input.Number(r, c[4]));
                input.Set(r, est, null);
                input.Set(
                    r,
                    year,
                    (double)InventoryMath.Year(InventoryMath.Date(input.Text(r, c[5])))
                );
            }
            foreach (var v in height["variaveis_customizadas"] as JsonArray ?? [])
            {
                string type = v.S("tipo"),
                    name = v.S("nome");
                double divisor = type switch
                {
                    "data_diff_anos" => 365.25,
                    "data_diff_meses" => 30.4375,
                    "data_diff_dias" => 1,
                    _ => 0,
                };
                if (divisor == 0 || name.Length == 0 || input.Column(name) >= 0)
                    continue;
                int a = input.Require(colCfg.S(v.S("col_data1"))),
                    b = input.Require(cadCols.S(v.S("col_data2"))),
                    dest = input.Ensure(name);
                for (int r = 1; r < input.Count; r++)
                    input.Set(
                        r,
                        dest,
                        Math.Floor(
                            InventoryMath.Date(input.Text(r, a))
                                - InventoryMath.Date(input.Text(r, b))
                        ) / divisor
                    );
            }
        }
        progress?.Invoke(30, "Calculando idade, área e volume por árvore…");
        var curves = cfg["curvas_crescimento"]?["equacoes"] as JsonArray ?? [];
        bool useCurves = curves.Count > 0;
        bool retainCalculationRows = !compact
            || useCurves
            || cfg.At("saida.sheets").B("resultado_arvore", true)
            || height.B("debug")
            || ProcessingHistory.Enabled(cfg);
        // Every non-direct input row produces one Tree. Reserve its final capacity
        // up front only when growth curves need per-tree model inputs. Without curves,
        // retain compact per-plot sums rather than a record for every tree.
        bool aggregatePlots = !directMode && !useCurves && ShouldAggregateByPlot(input, cfg);
        var trees = new List<Tree>(directMode || aggregatePlots || useCurves ? 0 : input.Count - 1);
        var plotRows = !directMode && useCurves
            ? new Dictionary<string, List<TreeRow>>(StringComparer.Ordinal)
            : null;
        var plotAggregates = aggregatePlots
            ? new Dictionary<string, PlotAggregate>(StringComparer.Ordinal)
            : null;
        int processedTrees = 0;
        double processedVolume = 0;
        var calc = retainCalculationRows ? new DataTable([
            "Talhão",
            "Parcela",
            "Lote",
            "DAP",
            "AS",
            "H",
            "Idade_anos",
            "EQ_COD",
            "area_corr",
            "VTCC",
            "CURVA_COD",
            "b0",
            "b1",
            "b2",
            "b3",
            "Fator_Volume",
        ]) : new DataTable(["EQ_COD"]);
        bool temporal = cfg["estatisticas"].B("chave_temporal_ativa");
        int fallbackFlag = input.Column(RegistryAgeFallback.FlagColumn), fallbackDate = input.Column(RegistryAgeFallback.DateColumn);
        bool spoolCalculationRows = compact && !directMode && retainCalculationRows && !useCurves
            && !height.B("debug") && !ProcessingHistory.Enabled(cfg)
            && fallbackFlag < 0 && fallbackDate < 0
            && cfg.At("saida.sheets").B("resultado_arvore", true);
        if (spoolCalculationRows && identification.HasContext)
        {
            calc.Ensure("Contexto_ID");
            calc.Ensure("Base_Origem");
            calc.Ensure("Tipo_Inventario");
        }
        using var calcSpool = spoolCalculationRows
            ? new CompactTableSpool(Path.GetTempPath(), Enumerable.Range(0, calc.Width).Select(column => calc.Text(0, column)).ToArray(), "RESULTADO - ARVORES")
            : null;
        var usedEquationCodes = retainCalculationRows && !spoolCalculationRows
            ? null
            : new HashSet<string>(StringComparer.Ordinal);
        int calcFallbackFlag = retainCalculationRows && fallbackFlag >= 0 ? calc.Ensure(RegistryAgeFallback.FlagColumn) : -1;
        int calcFallbackDate = retainCalculationRows && fallbackFlag >= 0 ? calc.Ensure(RegistryAgeFallback.DateColumn) : -1;
        double factor = versioned ? 1 : cfg["volume"].N("fator", 1),
            ageLimit = cfg["volume"].N("idade_limite_equacao_geral", 15);
        var models = equations.ToDictionary(e => e.S("codigo"), e => e!, StringComparer.Ordinal);
        var programs = new Dictionary<JsonNode, ModelProgram>();
        ModelProgram Program(JsonNode eq, bool growth)
        {
            if (!programs.TryGetValue(eq, out var program))
            {
                program = new(eq, input, registry, growth);
                programs.Add(eq, program);
            }
            return program;
        }
        ModelContext Context(Tree t, double volumeHa = double.NaN, double stemsHa = double.NaN) =>
            new(
                input,
                registry,
                t.Row,
                t.RegistryRow,
                t.Age,
                t.Dap,
                t.Height,
                t.Area,
                t.Effective,
                volumeHa,
                stemsHa
            );
        Tree FromCalcRow(TreeRow source)
        {
            int row = source.InputRow, registryRow = source.RegistryRow;
            double measured = InventoryMath.Date(input.Text(row, c[5]));
            string measurement = DateTime.FromOADate(measured).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new(row, registryRow, calc.Text(row, 0), temporal ? InventoryMath.Year(measured) : 0,
                calc.Text(row, 1), calc.Text(row, 2), calc.Number(row, 3), calc.Number(row, 5),
                calc.Number(row, 6), calc.Number(row, 8), registry.Number(registryRow, ca), calc.Text(row, 7),
                calc.Text(row, 10), measurement, identification.PlotKey(row), identification.Context(row), calc.Number(row, 9));
        }
        bool releaseInputRowsAsProcessed = !useCurves
            && !ProcessingHistory.Enabled(cfg)
            && !cfg.At("saida.sheets").B("ajustado", true)
            && !height.B("debug")
            && (mode == "KEEP_FILE" || !cfg.At("saida.sheets").B("h_ajuste", true));
        int keepFileHeightObserved = mode == "KEEP_FILE" && !useRna ? input.Column("H_obs") : -1;
        int keepFileHeightFinal = mode == "KEEP_FILE" && !useRna ? input.Column("H_final") : -1;
        int keepFileFilledHeightCount = 0;
        for (int r = 1; r < input.Count; r++)
        {
            if (r % 2048 == 0) cancellation.ThrowIfCancellationRequested();
            if (keepFileHeightObserved >= 0 && keepFileHeightFinal >= 0
                && !(input.Number(r, keepFileHeightObserved) > 0)
                && input.Number(r, keepFileHeightFinal) > 0)
                keepFileFilledHeightCount++;
            int rr = RegistryRow(r);
            string stand = input.Text(r, c[0]),
                plot = input.Text(r, c[1]),
                lot = input.Text(r, c[2]);
            if (stand.Length == 0 || plot.Length == 0 || lot.Length == 0)
                throw new InvalidDataException($"Linha {r + 1}: talhão, parcela ou lote vazio.");
            double planted = InventoryMath.Date(registry.Text(rr, cp)),
                measured = InventoryMath.Date(input.Text(r, c[5]));
            if (!double.IsFinite(planted) || !double.IsFinite(measured))
            {
                var invalidDates = new List<string>();
                if (!double.IsFinite(planted))
                    invalidDates.Add($"plantio na coluna '{registry.Text(0, cp)}' (valor: '{registry.Text(rr, cp)}')");
                if (!double.IsFinite(measured))
                    invalidDates.Add($"medição na coluna '{input.Text(0, c[5])}' (valor: '{input.Text(r, c[5])}')");
                throw new InvalidDataException($"Linha {r + 1}: data inválida de {string.Join(" e ", invalidDates)}.");
            }
            double age = Math.Floor(measured - planted) / 365.25,
                area = InventoryMath.Area(input.Number(r, lengthColumn), input.Number(r, widthColumn),
                    input.Number(r, slopeLengthColumn), input.Number(r, slopeWidthColumn)),
                dap = input.Number(r, c[3]),
                h = input.Number(r, c[4]),
                effective = registry.Number(rr, ca);
            string code = InventoryMath.EquationCode(lot, age, ageLimit),
                curve = InventoryMath.EquationCode(lot, 0);
            if (!models.TryGetValue(code, out var eq))
                throw new InvalidDataException(
                    $"Linha {r + 1}: equação {code} não encontrada na configuração."
                );
            var t = new Tree(
                r,
                rr,
                stand,
                temporal ? InventoryMath.Year(measured) : 0,
                plot,
                lot,
                dap,
                h,
                age,
                area,
                effective,
                code,
                curve,
                DateTime.FromOADate(measured).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                identification.PlotKey(r),
                identification.Context(r),
                0
            );
            bool custom = eq["entradas"] is not null || eq.S("formula").Length > 0;
            double volume = custom
                ? Program(eq, false).Evaluate(Program(eq, false).Values(Context(t)))
                : InventoryMath.Volume(dap, h, eq.N("b0"), eq.N("b1"), eq.N("b2"));
            if (custom && !double.IsFinite(volume))
                throw new InvalidDataException(
                    $"Modelo volumétrico {code}: resultado inválido na linha {r + 1}."
                );
            volume *= factor;
            if (double.IsInfinity(volume))
                throw new InvalidDataException(
                    $"Linha {r + 1}: volume fora do intervalo numérico."
                );
            if (direct != null)
            {
                direct.Add(stand, plot, lot, t.Year, r + 1, age, volume, dap > 0, area, effective,
                    input.Text(r, fallbackFlag), input.Text(r, fallbackDate), dap, h, t.PlotIdentity, t.Inventory, t.Measurement, t.Curve);
                if (releaseInputRowsAsProcessed) input.ReleaseRowValues(r);
                continue;
            }
            processedTrees++;
            if (!double.IsNaN(volume)) processedVolume += volume;
            var measuredTree = t with { Volume = volume };
            if (plotAggregates != null)
            {
                if (!plotAggregates.TryGetValue(t.PlotIdentity, out var aggregate))
                    plotAggregates.Add(t.PlotIdentity, aggregate = new(t, dominantTreesPerPlot));
                aggregate.Add(measuredTree);
            }
            else if (plotRows != null)
            {
                if (!plotRows.TryGetValue(t.PlotIdentity, out var rows)) plotRows.Add(t.PlotIdentity, rows = []);
                rows.Add(new(r, rr));
            }
            else trees.Add(measuredTree);
            if (retainCalculationRows && !spoolCalculationRows)
            {
                calc.Add(
                    stand,
                    plot,
                    lot,
                    dap,
                    Math.PI * Math.Pow(dap / 10, 2) / 40000,
                    h,
                    age,
                    code,
                    area,
                    volume,
                    curve,
                    eq.N("b0"),
                    eq.N("b1"),
                    eq.N("b2"),
                    0d,
                    factor
                );
                if (fallbackFlag >= 0) {
                    calc.Set(calc.Count - 1, calcFallbackFlag, input.Value(r, fallbackFlag));
                    calc.Set(calc.Count - 1, calcFallbackDate, input.Value(r, fallbackDate));
                }
                if (versioned)
                {
                    int row = calc.Count - 1;
                    string[] parts = [stand, plot, input.Text(r, identityColumns[0]), input.Text(r, identityColumns[1]), input.Text(r, identityColumns[2]), t.Measurement];
                    string identity = TreeIdentity(r);
                    foreach (var (name, value) in new[] { ("Cova", parts[2]), ("Fuste", parts[3]), ("Fila", parts[4]), ("Data_Medicao", t.Measurement),
                        ("Arvore_ID", identity), ("Parcela_ID", t.PlotIdentity) })
                        calc.Set(row, calc.Ensure(name), value);
                    calc.Set(row, calc.Ensure("VTCC_Original"), volume);
                    identification.CopyKeyFields(calc, row, r);
                }
                InventoryIdentity.SetContext(calc, calc.Count - 1, t.Inventory);
            }
            else if (spoolCalculationRows)
            {
                var cells = new object?[] {
                    stand, plot, lot, dap, Math.PI * Math.Pow(dap / 10, 2) / 40000, h, age, code, area, volume,
                    curve, eq.N("b0"), eq.N("b1"), eq.N("b2"), 0d, factor
                };
                if (identification.HasContext)
                {
                    cells[calc.Require("Contexto_ID")] = t.Inventory.Id;
                    cells[calc.Require("Base_Origem")] = t.Inventory.Origin;
                    cells[calc.Require("Tipo_Inventario")] = t.Inventory.Type;
                }
                calcSpool!.AddOwned(cells);
                usedEquationCodes!.Add(code);
            }
            else if (usedEquationCodes!.Add(code)) calc.Add(code);
            if (releaseInputRowsAsProcessed) input.ReleaseRowValues(r);
        }
        if (direct != null)
        {
            progress?.Invoke(85, "Gravando somente os resumos selecionados…");
            var summary = direct.Complete(removedDuplicates);
            progress?.Invoke(100, "Resumo direto concluído.");
            return summary;
        }
        using var calcSpoolReader = calcSpool?.Complete();
        var calcSpoolTable = calcSpoolReader == null ? null : calcSpool!.Table(calcSpoolReader);
        if (releaseInputRowsAsProcessed) input.ReleaseDataRows();
        progress?.Invoke(65, "Consolidando parcelas e talhões…");
        var plots = new DataTable([
            "Talhão",
            "Ano_Medicao",
            "Parcela",
            "DAP",
            "H",
            "VTCC",
            "Fustes",
            "area_corr",
            "Idade_anos",
            "Área efetiva (ha)",
            "EQ_COD",
            "VTCC_ha",
            "Fustes_ha",
            "VMI",
            "Lote",
            "CURVA_COD",
            "Vol_Esperado_ha",
            "Dif_IFC_pct",
            "IcV",
            "Curva_Modelo",
            "Curva_Ano",
            "VMI_real",
            "DAPd",
            "Hd",
        ]);
        var stands = new DataTable([
            "Talhão",
            "Ano_Medicao",
            "N_Parcelas",
            "DAP",
            "H",
            "VTCC_ha",
            "Fustes_ha",
            "VMI",
            "Idade_anos",
            "Área efetiva (ha)",
            "Vol_Esperado_ha",
            "Dif_IFC_pct",
            "IcV",
            "DAPd",
            "Hd",
        ]);
        if (siteIndex != null)
        {
            plots.Ensure("IS");
            stands.Ensure("IS");
        }
        if (versioned) { plots.Ensure("Data_Medicao"); stands.Ensure("Data_Medicao"); }
        void AddPlot(Tree first, double volume, double area, int stems, double age, double effective,
            double meanDap, double meanHeight, double meanVolume, DominantTreeMetrics dominant,
            IEnumerable<ModelContext>? curveContexts)
        {
            double perHa = volume * 10000 / area, stemsHa = stems * 10000 / area, expected = double.NaN;
            JsonNode? curve = null;
            if (useCurves)
            {
                curve =
                    curves.SingleOrDefault(v =>
                        v.S("codigo") == first.Curve && v.B("vigente", true)
                    )
                    ?? throw new InvalidDataException(
                        $"Selecione uma curva vigente para o ID {first.Curve}."
                    );
                expected = Program(curve, true).Aggregate(curveContexts!);
                if (!double.IsFinite(expected) || expected < 0)
                    throw new InvalidDataException(
                        $"Curva {first.Curve}: resultado inválido para a idade {age:G6} anos."
                    );
            }
            plots.Add(
                first.Stand,
                temporal ? (double)first.Year : null,
                first.Plot,
                meanDap,
                meanHeight,
                volume,
                (double)stems,
                area,
                age,
                effective,
                first.Code,
                perHa,
                stemsHa,
                stemsHa > 0 ? perHa / stemsHa : double.NaN,
                first.Lot,
                first.Curve,
                expected,
                expected > 0 ? (perHa - expected) / expected * 100 : double.NaN,
                expected > 0 ? perHa / expected : double.NaN,
                curve?.S("modelo_nome", curve.S("modelo")) ?? "",
                curve.N("ano", double.NaN),
                meanVolume,
                dominant.Dap,
                dominant.Height
            );
            if (siteIndex != null)
                plots.Set(plots.Count - 1, plots.Require("IS"), siteIndex.Calculate(first.Curve, age, dominant.Dap, dominant.Height));
            if (versioned)
            {
                plots.Set(plots.Count - 1, plots.Require("Data_Medicao"), first.Measurement);
                plots.Set(plots.Count - 1, plots.Ensure("VTCC_Original"), volume);
                plots.Set(plots.Count - 1, plots.Ensure("Fator_Volume"), 1d);
            }
            if (versioned || identification.HasContext) plots.Set(plots.Count - 1, plots.Ensure("Parcela_ID"), first.PlotIdentity);
            InventoryIdentity.SetContext(plots, plots.Count - 1, first.Inventory);
        }
        if (plotAggregates != null)
        {
            foreach (var aggregate in plotAggregates.Values.OrderBy(item => item.First.Stand, StringComparer.Ordinal)
                .ThenBy(item => item.First.Year).ThenBy(item => item.First.Plot, StringComparer.Ordinal).ThenBy(item => item.First.Row))
                AddPlot(aggregate.First, aggregate.Volume, aggregate.MeanArea, aggregate.Stems, aggregate.MeanAge,
                    aggregate.MeanEffective, aggregate.MeanDap, aggregate.MeanHeight, aggregate.MeanVolume,
                    aggregate.Dominant, null);
        }
        else if (plotRows != null)
        {
            var orderedGroups = plotRows.Values.Select(group => (Rows: group, First: FromCalcRow(group[0])))
                .OrderBy(group => group.First.Stand, StringComparer.Ordinal)
                .ThenBy(group => group.First.Year)
                .ThenBy(group => group.First.Plot, StringComparer.Ordinal)
                .ThenBy(group => group.First.Row);
            foreach (var entry in orderedGroups)
            {
                var rows = entry.Rows;
                var first = entry.First;
                var aggregate = new PlotAggregate(first, dominantTreesPerPlot);
                aggregate.Add(first);
                for (int i = 1; i < rows.Count; i++) aggregate.Add(FromCalcRow(rows[i]));
                double perHa = aggregate.Volume * 10000 / aggregate.MeanArea,
                    stemsHa = aggregate.Stems * 10000 / aggregate.MeanArea;
                AddPlot(first, aggregate.Volume, aggregate.MeanArea, aggregate.Stems, aggregate.MeanAge,
                    aggregate.MeanEffective, aggregate.MeanDap, aggregate.MeanHeight, aggregate.MeanVolume,
                    aggregate.Dominant, rows.Select(row => Context(FromCalcRow(row), perHa, stemsHa)));
            }
        }
        else
        {
            // Sort the existing compact value array instead of materializing another
            // full-sized array through OrderBy. Row is unique and preserves the former
            // stable order for all otherwise equal keys.
            trees.Sort(static (left, right) =>
            {
                int compare = StringComparer.Ordinal.Compare(left.Stand, right.Stand);
                if (compare != 0) return compare;
                compare = left.Year.CompareTo(right.Year);
                if (compare != 0) return compare;
                compare = StringComparer.Ordinal.Compare(left.Plot, right.Plot);
                return compare != 0 ? compare : left.Row.CompareTo(right.Row);
            });
            foreach (var group in trees.GroupBy(t => t.PlotIdentity))
            {
                var first = group.First();
                double volume = group.Where(t => !double.IsNaN(t.Volume)).Sum(t => t.Volume),
                    area = InventoryMath.Mean(group.Select(t => t.Area)),
                    age = InventoryMath.Mean(group.Select(t => t.Age)),
                    effective = InventoryMath.Mean(group.Select(t => t.Effective)),
                    meanDap = InventoryMath.Mean(group.Select(t => t.Dap)),
                    meanHeight = InventoryMath.Mean(group.Select(t => t.Height)),
                    meanVolume = InventoryMath.Mean(group.Select(t => t.Volume));
                int stems = group.Count(t => t.Dap > 0);
                var dominant = new DominantTreeMetrics(dominantTreesPerPlot);
                foreach (var tree in group) dominant.Add(tree.Dap, tree.Height);
                double perHa = volume * 10000 / area, stemsHa = stems * 10000 / area;
                AddPlot(first, volume, area, stems, age, effective, meanDap, meanHeight, meanVolume, dominant,
                    useCurves ? group.Select(tree => Context(tree, perHa, stemsHa)) : null);
            }
        }
        int[] positions = [3, 4, 11, 12, 13, 8, 9, 16, 17, 18];
        foreach (
            var group in Enumerable
                .Range(1, plots.Count - 1)
                .GroupBy(r => (Stand: plots.Text(r, 0), Year: plots.Text(r, 1), Measurement: versioned ? plots.Text(r, plots.Require("Data_Medicao")) : "", Scope: plots.Text(r, plots.Column("Contexto_ID"))))
        )
        {
            stands.Add(
                new object?[]
                {
                    group.Key.Stand,
                    plots.Value(group.First(), 1),
                    (double)group.Count(),
                }
                    .Concat(
                        positions.Select(p =>
                            (object?)InventoryMath.Mean(group.Select(r => plots.Number(r, p)))
                        )
                    )
                    .Concat(new object?[]
                    {
                        DominantTreeMetrics.AreaWeightedMean(group.Select(r => (plots.Number(r, plots.Require("DAPd")), plots.Number(r, plots.Require("area_corr"))))),
                        DominantTreeMetrics.AreaWeightedMean(group.Select(r => (plots.Number(r, plots.Require("Hd")), plots.Number(r, plots.Require("area_corr")))))
                    })
                    .ToArray()
            );
            if (siteIndex != null)
                stands.Set(stands.Count - 1, stands.Require("IS"), DominantTreeMetrics.AreaWeightedMean(
                    group.Select(r => (plots.Number(r, plots.Require("IS")), plots.Number(r, plots.Require("area_corr"))))));
            if (versioned) stands.Set(stands.Count - 1, stands.Require("Data_Medicao"), group.Key.Measurement);
            int firstRow = group.First();
            InventoryIdentity.SetContext(stands, stands.Count - 1, new(group.Key.Scope, plots.Text(firstRow, plots.Column("Base_Origem")), plots.Text(firstRow, plots.Column("Tipo_Inventario"))));
        }
        var result = new ProcessingResult(
            processedTrees,
            plots.Count - 1,
            stands.Count - 1,
            processedVolume,
            removedDuplicates,
            (plotAggregates?.Values.Select(item => item.First.Inventory.Type)
                ?? (plotRows != null ? plotRows.Values.Select(group => FromCalcRow(group[0]).Inventory.Type)
                    : trees.Select(t => t.Inventory.Type)))
                .Where(t => t.Length > 0).Distinct().Order().ToArray()
        );
        progress?.Invoke(85, compact ? "Gravando tabelas no banco do projeto…" : "Gravando relatório XLSX…");
        ProjectStore.AtomicWrite(
            output,
            temp => InventoryReport.Write(temp, cfg, input, original, calc, plots, stands, result, compact, progress, cancellation,
                mode == "KEEP_FILE" && !useRna ? keepFileFilledHeightCount : null,
                streamAdjustedInput ? projectedReader : null,
                streamAdjustedInput ? projectedInput!.Table : null,
                streamAdjustedInput ? projectedInput!.SourceColumns : null,
                streamAdjustedInput ? originalSourceRows : null,
                calcSpoolReader,
                calcSpoolTable,
                usedEquationCodes)
        );
        progress?.Invoke(100, "Relatório gerado com sucesso.");
        return result;
    }

    private static void NormalizeInputUnits(
        DataTable input,
        int dapColumn,
        int heightColumn,
        JsonObject config
    )
    {
        var units = config.At("arquivo_entrada.unidades");
        double dapToMillimeters = units.S("dap", "mm") == "cm" ? 10 : 1;
        double heightToDecimeters = units.S("altura", "dm") switch
        {
            "cm" => 0.1,
            "m" => 10,
            _ => 1,
        };
        if (dapToMillimeters == 1 && heightToDecimeters == 1)
            return;
        for (int row = 1; row < input.Count; row++)
        {
            if (dapToMillimeters != 1)
            {
                double dap = input.Number(row, dapColumn);
                if (double.IsFinite(dap))
                    input.Set(row, dapColumn, dap * dapToMillimeters);
            }
            if (heightToDecimeters != 1)
            {
                double height = input.Number(row, heightColumn);
                if (double.IsFinite(height))
                    input.Set(row, heightColumn, height * heightToDecimeters);
            }
        }
    }

    private static bool ShouldAggregateByPlot(DataTable input, JsonObject config)
    {
        int rows = input.Count - 1;
        if (rows < 100_000) return false;
        string Resolve(string name)
        {
            var map = config.Obj("arquivo_entrada").Obj("colunas");
            return map[name] is JsonValue ? map.S(name) : name;
        }
        var keyColumns = config.Obj("identificacao").Arr("parcela")
            .Select(field => input.Column(Resolve(field.Display()))).Where(column => column >= 0).ToList();
        foreach (var field in config.Obj("identificacao").Arr("colunas_origem"))
        {
            string name = Resolve(field.Display());
            int column = input.Column(name);
            if (column >= 0) keyColumns.Add(column);
        }
        string typeName = config.Obj("identificacao").S("coluna_tipo_inventario");
        int typeColumn = typeName.Length > 0 ? input.Column(typeName)
            : new[] { "Tipo_Inventario", "Tipo de inventário", "Tipo_Inventario_Origem" }
                .Select(input.Column).FirstOrDefault(column => column >= 0, -1);
        if (typeColumn >= 0) keyColumns.Add(typeColumn);
        keyColumns = keyColumns.Distinct().ToList();
        if (keyColumns.Count == 0) return false;
        int samples = Math.Min(10_000, rows);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (int sample = 0; sample < samples; sample++)
        {
            int row = 1 + (int)((long)sample * (rows - 1) / Math.Max(1, samples - 1));
            distinct.Add(System.Text.Json.JsonSerializer.Serialize(keyColumns.Select(column => input.Text(row, column))));
        }
        // Many trees per parcel makes one accumulator substantially smaller than
        // retaining one Tree record per row. For mostly unique parcel keys, retain
        // the compact row list instead of adding a second large dictionary.
        return distinct.Count < samples * .35;
    }

    private static ProjectedInput? ReadProjectedCompactInput(
        string path,
        string sheet,
        JsonObject config,
        NeuralModel? heightModel,
        string registryPath,
        bool compactOutput
    )
    {
        if (!Dendro.Core.Tabular.CompactBankFile.IsCompactBank(path)
            || ProcessingHistory.Enabled(config))
            return null;

        var curves = config["curvas_crescimento"]?["equacoes"] as JsonArray;
        bool useRna = heightModel != null;
        if (!useRna && ((config["ajuste_altura"]?["variaveis_customizadas"] as JsonArray)?.Count > 0
            || (config["ajuste_altura"]?["estatisticas_parcela"] as JsonArray)?.Count > 0)
            )
            return null;
        var equations = config["volume"]?["equacoes"] as JsonArray;
        if (equations == null || equations.Count == 0)
            return null;

        var reader = new Dendro.Core.Tabular.CompactBankReader(path);
        try
        {
            var sourceTable = reader.Table(sheet);
            string[] headers = sourceTable.Columns;
            string[] registryHeaders = [];
            if (heightModel != null)
            {
                var registryOptions = ProjectStore.ExcelOptions(config, registryPath);
                using var registryCursor = new Dendro.Core.Tabular.TableCursor(new(
                    registryPath,
                    registryOptions.S("sheet", config.At("cadastro").S("worksheet", "Data")),
                    (int)registryOptions.N("header_row", 1)));
                registryHeaders = registryCursor.Columns;
            }
            if (headers.Length == 0
                || headers.Any(string.IsNullOrWhiteSpace)
                || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
            {
                reader.Dispose();
                return null;
            }

            var columns = config.Obj("arquivo_entrada").Obj("colunas");
            string Resolve(string key, string fallback) => columns[key] is JsonValue ? columns.S(key) : fallback;
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, i) in ColumnKeys.Select((key, i) => (key, i)))
                required.Add(Resolve(key, ColumnDefaults[i]));
            required.Add(config.At("arquivo_entrada").S("chave", "Talhão"));

            string ResolveIdentity(string key)
            {
                var mapped = columns[key];
                return mapped is JsonValue ? columns.S(key) : key;
            }
            foreach (var field in config.Obj("identificacao").Arr("parcela"))
                required.Add(ResolveIdentity(field.Display()));
            // Duplicate removal and revision tracking use the configured tree key.
            // Include those fields so these modes can still use a compact projection.
            if (config.At("processador").B("remover_duplicadas"))
                foreach (var field in config.Obj("identificacao").Arr("arvore"))
                    required.Add(ResolveIdentity(field.Display()));
            foreach (var field in config.Obj("identificacao").Arr("colunas_origem"))
            {
                string origin = ResolveIdentity(field.Display());
                if (origin != "Fonte" || headers.Contains(origin, StringComparer.Ordinal)) required.Add(origin);
            }

            var fallback = config.At("cadastro.fallback_idade");
            if (fallback.B("ativo") && fallback.S("coluna_codigo").Length > 0)
                required.Add(fallback.S("coluna_codigo"));

            string typeColumn = config.At("identificacao").S("coluna_tipo_inventario");
            if (typeColumn.Length > 0)
                required.Add(typeColumn);
            else
                foreach (string candidate in new[] { "Tipo_Inventario", "Tipo de inventário", "Tipo_Inventario_Origem" })
                    if (headers.Contains(candidate, StringComparer.Ordinal)) required.Add(candidate);

            var featureColumns = new HashSet<string>(StringComparer.Ordinal);
            if (heightModel != null)
                featureColumns.UnionWith(heightModel.Numerical.Concat(heightModel.Categorical));

            var height = config.Obj("ajuste_altura");
            var statistics = height.Arr("estatisticas_parcela");
            var customVariables = height.Arr("variaveis_customizadas");
            var generated = statistics.OfType<JsonObject>().Select(entry => entry.S("nome"))
                .Concat(customVariables.OfType<JsonObject>().Select(entry => entry.S("nome")))
                .Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in statistics.OfType<JsonObject>())
                if (entry.S("formula").Length == 0 && entry.S("coluna").Length > 0)
                    featureColumns.Add(entry.S("coluna"));
            foreach (var entry in customVariables.OfType<JsonObject>())
            {
                if (entry.S("fonte") == "cadastro") featureColumns.Add(entry.S("coluna", entry.S("nome")));
                if (entry.S("tipo").StartsWith("data_diff_", StringComparison.Ordinal))
                {
                    string dateColumn = Resolve(entry.S("col_data1"), "");
                    if (dateColumn.Length > 0) required.Add(dateColumn);
                    featureColumns.Add(config.At("cadastro.colunas").S(entry.S("col_data2")));
                }
            }

            // Numeric feature formulas expose their exact column dependencies. Text
            // slicing features use the same source-column syntax as FeaturePreparation.
            foreach (var entry in statistics.OfType<JsonObject>().Concat(customVariables.OfType<JsonObject>()))
            {
                string expression = entry.S("formula");
                if (expression.Length == 0) continue;
                var textSource = System.Text.RegularExpressions.Regex.Match(expression,
                    @"^\s*(?:np\.where\(np\.isin\()?([^\[\]]+)\[\d+:\d+\]");
                if (textSource.Success)
                {
                    string sourceName = textSource.Groups[1].Value.Trim();
                    if (headers.Contains(sourceName, StringComparer.Ordinal)) required.Add(sourceName);
                    else featureColumns.Add(sourceName);
                    continue;
                }
                var formula = new Formula(expression, headers.Concat(registryHeaders).Concat(generated).Concat(featureColumns));
                foreach (string dependency in formula.Dependencies)
                    if (headers.Contains(dependency, StringComparer.Ordinal)) required.Add(dependency);
                    else if (!generated.Contains(dependency)) featureColumns.Add(dependency);
            }

            foreach (string feature in featureColumns)
                if (headers.Contains(feature, StringComparer.Ordinal)) required.Add(feature);

            // Growth curves evaluate only their declared inventory input columns.
            // Project those explicitly instead of loading every source column.
            foreach (var equation in curves?.OfType<JsonObject>() ?? [])
                foreach (var input in ModelDefinition.Inputs(equation, growth: true).OfType<JsonObject>())
                    if (input.S("origem") == "entrada" && input.S("coluna").Length > 0)
                        required.Add(input.S("coluna"));
            foreach (var equation in equations.OfType<JsonObject>())
                foreach (var input in ModelDefinition.Inputs(equation, growth: false).OfType<JsonObject>())
                    if (input.S("origem") == "entrada" && input.S("coluna").Length > 0)
                        required.Add(input.S("coluna"));

            if (required.Any(name => !headers.Contains(name, StringComparer.Ordinal)))
            {
                reader.Dispose();
                return null;
            }
            int[] selected = Enumerable.Range(0, headers.Length)
                .Where(index => required.Contains(headers[index]))
                .ToArray();
            var projected = new DataTable(selected.Select(index => headers[index]));
            if (compactOutput)
                projected.EnableColumnarStorage(checked((int)sourceTable.Rows + 1));
            foreach (var row in reader.ProjectedRows(sourceTable, selected)) projected.AddOwned(row);
            return new(projected, headers.Length, headers, reader, sourceTable);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }
}
