using System.Text.Json.Nodes;
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
    private sealed record Tree(
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
        var height = cfg["ajuste_altura"]!;
        int dominantTreesPerPlot = (int)cfg.At("processador").N("n_dominantes_parcela", 5);
        string mode = height.S("h_mode", "KEEP_FILE");
        bool useRna = mode is "MODEL_GENERAL" or "FINETUNE_MODEL";
        if (mode != "KEEP_FILE" && !useRna)
            throw new InvalidDataException(
                $"O modo {mode} ainda não está disponível. Use KEEP_FILE, MODEL_GENERAL ou FINETUNE_MODEL."
            );
        var equations = cfg["volume"]!["equacoes"]!.AsArray().Where(e => e.S("tipo_volume", "VTCC") == "VTCC").ToArray();
        if (equations.Length < 1)
            throw new InvalidDataException("Configure os modelos volumétricos VTCC antes de processar.");
        progress?.Invoke(10, "Lendo inventário…");
        var options = ProjectStore.ExcelOptions(cfg, inputPath);
        var input = Dendro.Core.Tabular.TableStore.IsBank(inputPath)
            ? Dendro.Core.Tabular.TableStore.Read(new(inputPath, request.Sheet.Length > 0 ? request.Sheet : options.S("sheet")))
            : SpreadsheetReader.Read(
            inputPath,
            request.Sheet.Length > 0 ? request.Sheet : options.S("sheet"),
            (int)options.N("header_row", 1)
        );
        if (input.Count < 2)
            throw new InvalidDataException("O inventário não contém árvores.");
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
        int original = input.Width;
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
            var identities = new HashSet<string>(StringComparer.Ordinal);
            bool remove = removeDuplicateRows;
            int retained = 1, received = input.Count - 1;
            for (int row = 1; row < input.Count; row++)
            {
                if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                if (!identities.Add(TreeIdentity(row)))
                {
                    if (!remove) throw new InvalidDataException($"Linha {row + 1}: identificação de árvore duplicada conforme Configurações > Identificação. Para processar mantendo a primeira ocorrência, marque Remover árvores duplicadas no Processador.");
                    removedDuplicates++;
                }
                else input.Rows[retained++] = input.Rows[row];
            }
            input.Rows.RemoveRange(retained, input.Count - retained);
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
        var validatedAreas = ParcelAreaValidation.Compute(input, cfg, cancellation, progress, retainAreas: !directMode);
        var cad = cfg["cadastro"];
        var cadCols = cad?["colunas"];
        string registryPath = Path.GetFullPath(
            request.Registry.Length > 0 ? request.Registry : cad.S("caminho")
        );
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
            HeightProcessor.Apply(input, registry, cfg);
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
        var trees = new List<Tree>();
        var calc = new DataTable([
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
        ]);
        bool temporal = cfg["estatisticas"].B("chave_temporal_ativa");
        int fallbackFlag = input.Column(RegistryAgeFallback.FlagColumn), fallbackDate = input.Column(RegistryAgeFallback.DateColumn);
        int calcFallbackFlag = fallbackFlag >= 0 ? calc.Ensure(RegistryAgeFallback.FlagColumn) : -1;
        int calcFallbackDate = fallbackFlag >= 0 ? calc.Ensure(RegistryAgeFallback.DateColumn) : -1;
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
        for (int r = 1; r < input.Count; r++)
        {
            if (r % 2048 == 0) cancellation.ThrowIfCancellationRequested();
            int rr = RegistryRow(r);
            string stand = input.Text(r, c[0]),
                plot = input.Text(r, c[1]),
                lot = input.Text(r, c[2]);
            if (stand.Length == 0 || plot.Length == 0 || lot.Length == 0)
                throw new InvalidDataException($"Linha {r + 1}: talhão, parcela ou lote vazio.");
            double planted = InventoryMath.Date(registry.Text(rr, cp)),
                measured = InventoryMath.Date(input.Text(r, c[5]));
            if (!double.IsFinite(planted) || !double.IsFinite(measured))
                throw new InvalidDataException(
                    $"Linha {r + 1}: data inválida de plantio ou medição."
                );
            double age = Math.Floor(measured - planted) / 365.25,
                area = directMode
                    ? InventoryMath.Area(input.Number(r, lengthColumn), input.Number(r, widthColumn),
                        input.Number(r, slopeLengthColumn), input.Number(r, slopeWidthColumn))
                    : validatedAreas[r],
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
                    input.Text(r, fallbackFlag), input.Text(r, fallbackDate), dap, h, t.PlotIdentity, t.Inventory, t.Measurement);
                continue;
            }
            trees.Add(t with { Volume = volume });
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
        if (direct != null)
        {
            progress?.Invoke(85, "Gravando somente os resumos selecionados…");
            var summary = direct.Complete(removedDuplicates);
            progress?.Invoke(100, "Resumo direto concluído.");
            return summary;
        }
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
        var curves = cfg["curvas_crescimento"]?["equacoes"] as JsonArray ?? [];
        if (versioned) { plots.Ensure("Data_Medicao"); stands.Ensure("Data_Medicao"); }
        bool useCurves = curves.Count > 0;
        foreach (
            var group in trees
                .OrderBy(t => t.Stand, StringComparer.Ordinal)
                .ThenBy(t => t.Year)
                .ThenBy(t => t.Plot, StringComparer.Ordinal)
                .ThenBy(t => t.Row)
                .GroupBy(t => t.PlotIdentity)
        )
        {
            var first = group.First();
            if (group.Any(t => t.Stand != first.Stand || t.Lot != first.Lot || t.Plot != first.Plot))
                throw new InvalidDataException("A chave de parcela está agrupando talhões, lotes ou números de parcela diferentes. Revise as colunas da chave em Configurações > Identificação.");
            double volume = group.Where(t => !double.IsNaN(t.Volume)).Sum(t => t.Volume),
                area = InventoryMath.Mean(group.Select(t => t.Area)),
                perHa = volume * 10000 / area,
                stems = group.Count(t => t.Dap > 0),
                stemsHa = stems * 10000 / area,
                age = InventoryMath.Mean(group.Select(t => t.Age)),
                expected = double.NaN;
            var dominant = new DominantTreeMetrics(dominantTreesPerPlot);
            foreach (var tree in group) dominant.Add(tree.Dap, tree.Height);
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
                expected = Program(curve, true)
                    .Aggregate(group.Select(t => Context(t, perHa, stemsHa)));
                if (!double.IsFinite(expected) || expected < 0)
                    throw new InvalidDataException(
                        $"Curva {first.Curve}: resultado inválido para a idade {age:G6} anos."
                    );
            }
            plots.Add(
                first.Stand,
                temporal ? (double)first.Year : null,
                first.Plot,
                InventoryMath.Mean(group.Select(t => t.Dap)),
                InventoryMath.Mean(group.Select(t => t.Height)),
                volume,
                (double)stems,
                area,
                age,
                InventoryMath.Mean(group.Select(t => t.Effective)),
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
                InventoryMath.Mean(group.Select(t => t.Volume)),
                dominant.Dap,
                dominant.Height
            );
            if (versioned)
            {
                plots.Set(plots.Count - 1, plots.Require("Data_Medicao"), first.Measurement);
                plots.Set(plots.Count - 1, plots.Ensure("VTCC_Original"), volume);
                plots.Set(plots.Count - 1, plots.Ensure("Fator_Volume"), 1d);
            }
            if (versioned || identification.HasContext) plots.Set(plots.Count - 1, plots.Ensure("Parcela_ID"), first.PlotIdentity);
            InventoryIdentity.SetContext(plots, plots.Count - 1, first.Inventory);
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
            if (versioned) stands.Set(stands.Count - 1, stands.Require("Data_Medicao"), group.Key.Measurement);
            int firstRow = group.First();
            InventoryIdentity.SetContext(stands, stands.Count - 1, new(group.Key.Scope, plots.Text(firstRow, plots.Column("Base_Origem")), plots.Text(firstRow, plots.Column("Tipo_Inventario"))));
        }
        var result = new ProcessingResult(
            trees.Count,
            plots.Count - 1,
            stands.Count - 1,
            trees.Where(t => !double.IsNaN(t.Volume)).Sum(t => t.Volume),
            removedDuplicates,
            trees.Select(t => t.Inventory.Type).Where(t => t.Length > 0).Distinct().Order().ToArray()
        );
        progress?.Invoke(85, compact ? "Gravando tabelas no banco do projeto…" : "Gravando relatório XLSX…");
        ProjectStore.AtomicWrite(
            output,
            temp => InventoryReport.Write(temp, cfg, input, original, calc, plots, stands, result, compact, progress, cancellation)
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
}
