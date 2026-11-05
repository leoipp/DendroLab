using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

public static class InventoryReport
{
    private static double At(DataTable t, int r, string name) => t.Number(r, t.Column(name));

    private static string Text(DataTable t, int r, string name) => t.Text(r, t.Column(name));

    public static void Write(
        string path,
        JsonObject cfg,
        DataTable input,
        int original,
        DataTable calc,
        DataTable plots,
        DataTable stands,
        ProcessingResult result,
        bool compact = false,
        Action<int, string>? progress = null,
        CancellationToken cancellation = default,
        int? keepFileFilledHeightCount = null,
        CompactBankReader? adjustedSourceReader = null,
        CompactTable? adjustedSourceTable = null,
        string[]? adjustedSourceColumns = null,
        int[]? originalSourceRows = null,
        CompactBankReader? calculationSourceReader = null,
        CompactTable? calculationSourceTable = null,
        IReadOnlySet<string>? appliedEquationCodes = null
    )
    {
        bool debug = cfg["ajuste_altura"].B("debug"),
            temporal = cfg["estatisticas"].B("chave_temporal_ativa"),
            curves =
                (cfg["curvas_crescimento"]?["equacoes"] as JsonArray)?.Count > 0
                && cfg["comparativo_ifc"].B("ativo", true);
        cancellation.ThrowIfCancellationRequested();
        // Reuse the prepared table when the adjusted sheet needs either all
        // columns (DLB) or only the original columns (Excel). This avoids cloning
        // every cell of a potentially multi-million-row inventory. History keeps
        // its original input snapshot, so that mode still gets its own table.
        DataTable adjusted = !debug && !ProcessingHistory.Enabled(cfg) ? input : new DataTable();
        int adjustedWidth = compact || debug ? input.Width : original;
        if (!ReferenceEquals(adjusted, input))
            for (int r = 0; r < input.Count; r++)
                adjusted.Add(
                    Enumerable
                        .Range(0, adjustedWidth)
                        .Select(c => input.Value(r, c))
                        .ToArray()
                );
        if (debug)
            foreach (
                string name in new[]
                {
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
                }
            )
            {
                int dest = adjusted.Ensure(name),
                    from = calc.Column(name);
                for (int r = 1; r < calc.Count; r++)
                    adjusted.Set(r, dest, calc.Value(r, from));
            }
        var byplot = Summary(plots, false, temporal, curves, debug);
        var bystand = Summary(stands, true, temporal, curves, debug);
        bool includeHeight = ProcessingHistory.Enabled(cfg) || cfg.At("saida.sheets").B("h_ajuste", true);
        var height = includeHeight ? Height(input, cfg, keepFileFilledHeightCount) : new DataTable();
        var stats = new DataTable();
        Statistics(stats, plots, cfg, true);
        Statistics(stats, plots, cfg, false);
        stats.Add(
            "n = parcelas; IC = média ± t × erro padrão. Área efetiva deduplicada por talhão. Versões das curvas são escolhidas pelo usuário."
        );
        DataTable? graphs = compact ? null : new DataTable(["DAP", "H", "VTCC"]);
        if (graphs != null)
            for (int r = 1; r < calc.Count; r++)
                if (At(calc, r, "DAP") > 0)
                    graphs.Add(At(calc, r, "DAP"), At(calc, r, "H"), At(calc, r, "VTCC"));
        var audit = new DataTable([
            "Tipo",
            "ID",
            "Modelo",
            "Ano",
            "b0",
            "b1",
            "b2",
            "Vigente",
            "Fórmula",
            "Unidade de t (legado)",
        ]);
        for (int type = 0; type < 2; type++)
            foreach (
                var eq in (
                    type == 0 ? cfg["volume"]?["equacoes"] : cfg["curvas_crescimento"]?["equacoes"]
                ) as JsonArray
                    ?? []
            )
            {
                if (type == 1 && !eq.B("vigente", true))
                    continue;
                if (type == 0 && eq.S("tipo_volume", "VTCC") != "VTCC") continue;
                var source = type == 0 ? calc : plots;
                string key = type == 0 ? "EQ_COD" : "CURVA_COD";
                if (appliedEquationCodes?.Contains(eq.S("codigo")) == false
                    || appliedEquationCodes == null && !Enumerable.Range(1, source.Count - 1)
                        .Any(r => Text(source, r, key) == eq.S("codigo")))
                    continue;
                bool growth = type == 1;
                audit.Add(
                    growth ? "Crescimento e produção" : "Volume individual",
                    eq.S("codigo"),
                    ModelDefinition.Name(eq!, growth),
                    eq.N("ano", double.NaN),
                    eq.N("b0"),
                    eq.N("b1"),
                    eq.N("b2"),
                    "Sim",
                    ModelDefinition.FormulaText(eq!, growth),
                    growth && eq!["entradas"] is null ? eq.S("unidade_idade", "meses") : ""
                );
                var inputs = ModelDefinition.Inputs(eq!, growth);
                for (int i = 0; i < inputs.Count; i++)
                {
                    audit.Set(0, 10 + i, $"x{i + 1} - entrada esperada");
                    audit.Set(
                        audit.Count - 1,
                        10 + i,
                        ModelDefinition.Describe(inputs[i]!, growth)
                    );
                }
            }
        var info = new DataTable(["Item", "Descrição"]);
        info.Add("Motor", "DENDRO C# / .NET");
        info.Add(
            "Curvas",
            "Fórmula, entradas, unidades, fatores e agregações constam em MODELOS APLICADOS. Vigência é selecionada por ID; o ano identifica o histórico."
        );
        info.Add(
            "Relatório",
            "Abas de dados, ajuste de altura, resumos, estatísticas e gráficos. Não comprova paridade integral com IFPC."
        );
        info.Add(
            "Limitações",
            "CALIBRATE e reprodução visual integral do IFPC ainda pendentes. Taylor excluído do escopo."
        );
        info.Add("Árvores", (double)result.Trees);
        info.Add("DAPd e Hd", $"Média das {cfg.At("processador").N("n_dominantes_parcela", 5):N0} árvores com maior DAP em cada parcela; Hd usa as mesmas árvores. Níveis superiores ponderam pela área das parcelas.");
        if (cfg.At("processador").B("calcular_is"))
        {
            info.Add("Índice de sítio (IS)", "A equação vigente é escolhida pelo CURVA_COD de cada parcela. IS = Y × f(idade-base) / f(idade da parcela); Y é DAPd ou Hd conforme a equação. Níveis acima ponderam IS pela área da parcela.");
            foreach (string code in Enumerable.Range(1, plots.Count - 1)
                .Select(row => Text(plots, row, "CURVA_COD"))
                .Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var siteModel = (cfg.At("_dendro.site_index_models") as JsonArray ?? [])
                    .OfType<JsonObject>().FirstOrDefault(model => model.S("codigo") == code && model.B("vigente", true));
                if (siteModel != null)
                    info.Add("IS · " + code, $"{siteModel.S("source_y")} · base {siteModel.N("reference_age"):G6} {siteModel.S("age_unit")} · {siteModel.S("formula")}");
            }
        }
        info.Add("Fator volumétrico", ProcessingHistory.Enabled(cfg) && cfg.At("processador.execucao").N("number") > 1
            ? "Por " + cfg.At("processador.execucao").S("factor_scope") + "; consulte FATORES APLICADOS e os resultados por árvore/parcela."
            : cfg["volume"].N("fator", 1));
        if (input.Column(RegistryAgeFallback.FlagColumn) >= 0)
        {
            info.Add("Fallback de idade", "Plantios inferidos pelo código quando a chave não existe no cadastro. Consulte Cadastro_Fallback_Idade e Data_Plantio_Fallback no resultado por árvore.");
            info.Add("Área efetiva ausente", "Não é estimada pelo fallback. Totais que dependem de área desconhecida ficam sem valor.");
        }
        foreach (string band in new[] { "verde", "amarelo", "vermelho" })
            info.Add(band, cfg.At("comparativo_ifc.descricao").S(band));
        info.Add("Erro máximo de alerta (%)", cfg["estatisticas"].N("erro_maximo_alerta", 10));
        var sheets = new List<WorksheetData>();
        if (cfg.At("saida.sheets").B("resultado_arvore", false))
            sheets.Add(new("RESULTADO - ARVORES", calc));
        if (ProcessingHistory.Enabled(cfg) || cfg.At("saida.sheets").B("resumo_bylote", false))
        {
            // A separate, rectangular table: header on row 1, no report totals or titles.
            var lotReport = new DataTable();
            Statistics(lotReport, plots, cfg, true);
            var lots = new DataTable();
            int lotWidth = lotReport.Width;
            bool dataRows = false;
            for (int r = 0; r < lotReport.Count; r++)
            {
                string first = lotReport.Text(r, 0);
                if (first == "Lot") { if (lots.Count == 0) lots.Add(Enumerable.Range(0, lotWidth).Select(c => lotReport.Value(r, c)).ToArray()); dataRows = true; continue; }
                if (first.Length == 0 || first == "TOTAL" || first.StartsWith("INVENTORY STATISTICS", StringComparison.Ordinal)) { dataRows = false; continue; }
                if (!dataRows) continue;
                lots.Add(Enumerable.Range(0, lotWidth).Select(c => lotReport.Value(r, c)).ToArray());
            }
            sheets.Add(new("RESUMO - BYLOTE", lots));
        }
        Add("ajustado", "AJUSTADO", adjusted);
        Add("h_ajuste", "H Ajuste", height);
        Add("resumo_byplot", "RESUMO - BYPLOT", byplot);
        Add("resumo_bytalhao", "RESUMO - BYTALHAO", bystand);
        Add("estatisticas_inv", "ESTATISTICAS INV", stats);
        if (graphs != null) Add("graficos", "Graficos", graphs);
        Add("modelos_aplicados", "MODELOS APLICADOS", audit);
        Add("informacoes", "INFORMACOES", info);
        if (debug)
            Add("calculos", "CALCULOS", calc);
        ProcessingHistory.AddCanonicalSheets(sheets, cfg, input, original, calc, plots, stands);
        if (!compact && ReferenceEquals(adjusted, input))
            input.TrimWidth(original);
        if (compact)
        {
            using var writer = new Dendro.Core.Tabular.CompactBankWriter(path);
            long totalRows = sheets.Sum(s => (long)Math.Max(0, s.Table.Count - 1))
                + (calculationSourceTable?.Rows ?? 0), written = 0;
            foreach (var sheet in sheets)
            {
                cancellation.ThrowIfCancellationRequested();
                var table = sheet.Table;
                bool streamOriginal = sheet.Name == "AJUSTADO"
                    && adjustedSourceReader != null
                    && adjustedSourceTable != null
                    && adjustedSourceColumns != null;
                bool streamCalculation = sheet.Name == "RESULTADO - ARVORES"
                    && calculationSourceReader != null
                    && calculationSourceTable != null;
                int width;
                string[] columns;
                if (streamOriginal)
                {
                    columns = adjustedSourceColumns!
                        .Concat(Enumerable.Range(0, table.Width).Select(c => table.Text(0, c)))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    width = columns.Length;
                    if (adjustedSourceTable!.Columns.Length != adjustedSourceColumns!.Length
                        || !adjustedSourceTable.Columns.SequenceEqual(adjustedSourceColumns, StringComparer.Ordinal))
                        throw new InvalidDataException("As colunas de origem do AJUSTADO mudaram durante o processamento.");
                    if (originalSourceRows != null && originalSourceRows.Length != table.Count)
                        throw new InvalidDataException("O mapeamento de linhas do AJUSTADO está inconsistente.");
                }
                else
                {
                    width = table.Width;
                    columns = Enumerable.Range(0, width).Select(c => table.Text(0, c)).ToArray();
                }
                string id = writer.AddTable(columns, sheet.Name);
                if (streamCalculation)
                {
                    if (!columns.SequenceEqual(calculationSourceTable!.Columns, StringComparer.Ordinal))
                        throw new InvalidDataException("As colunas do resultado por árvore mudaram durante o processamento.");
                    long calculationRow = 0;
                    foreach (var cells in calculationSourceReader!.Rows(calculationSourceTable))
                    {
                        if ((calculationRow++ & 2047) == 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            progress?.Invoke(85 + (int)(13 * written / Math.Max(1, totalRows)),
                                $"Gravando RESULTADO - ARVORES: {written:N0} de {totalRows:N0} linhas…");
                        }
                        writer.AddOwned(id, cells);
                        written++;
                    }
                    if (calculationRow != calculationSourceTable.Rows)
                        throw new InvalidDataException("A contagem do resultado por árvore mudou durante a gravação.");
                    continue;
                }
                if (streamOriginal)
                {
                    int[] overlayColumns = columns.Select(table.Column).ToArray();
                    int processedRow = 1;
                    long sourceRow = 0;
                    foreach (var sourceCells in adjustedSourceReader!.Rows(adjustedSourceTable!))
                    {
                        sourceRow++;
                        if ((sourceRow & 2047) == 1)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            progress?.Invoke(85 + (int)(13 * written / Math.Max(1, totalRows)),
                                $"Gravando AJUSTADO: {written:N0} de {totalRows:N0} linhas…");
                        }
                        int expectedSourceRow = originalSourceRows == null
                            ? checked((int)sourceRow)
                            : processedRow < originalSourceRows.Length ? originalSourceRows[processedRow] : int.MaxValue;
                        if (sourceRow < expectedSourceRow) continue;
                        if (sourceRow > expectedSourceRow)
                            throw new InvalidDataException("Não foi possível alinhar as linhas originais com o resultado AJUSTADO.");
                        var cells = new object?[width];
                        Array.Copy(sourceCells, cells, Math.Min(sourceCells.Length, adjustedSourceColumns!.Length));
                        for (int column = 0; column < overlayColumns.Length; column++)
                            if (overlayColumns[column] >= 0)
                                cells[column] = table.Value(processedRow, overlayColumns[column]);
                        writer.AddOwned(id, cells);
                        written++;
                        processedRow++;
                    }
                    if (processedRow != table.Count)
                        throw new InvalidDataException("O resultado AJUSTADO não corresponde às linhas da base original.");
                    continue;
                }
                for (int r = 1; r < table.Count; r++)
                {
                    if ((r - 1) % 2048 == 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        progress?.Invoke(85 + (int)(13 * written / Math.Max(1, totalRows)), $"Gravando {sheet.Name}: {r - 1:N0} de {table.Count - 1:N0} linhas…");
                    }
                    var cells = new object?[width];
                    for (int c = 0; c < width; c++) cells[c] = table.Value(r, c);
                    writer.AddOwned(id, cells);
                    written++;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(98, "Finalizando índice e sincronizando o arquivo…");
            writer.Complete();
            return;
        }
        WorkbookWriter.Write(
            path,
            sheets,
            true,
            cfg.At("comparativo_ifc.limiares").N("verde", 5),
            cfg.At("comparativo_ifc.limiares").N("amarelo", 15),
            cfg["estatisticas"].N("erro_maximo_alerta", 10)
        );
        void Add(string key, string name, DataTable table)
        {
            if (ProcessingHistory.Enabled(cfg) || cfg.At("saida.sheets").B(key, true))
                sheets.Add(new(name, table));
        }
    }

    private static DataTable Summary(
        DataTable source,
        bool stand,
        bool temporal,
        bool curves,
        bool debug
    )
    {
        string[] names = stand
            ?
            [
                "Talhão",
                "Ano_Medicao",
                "N_Parcelas",
                "DAP",
                "H",
                "DAPd",
                "Hd",
                "VTCC_ha",
                "Fustes_ha",
                "VMI",
                "Idade_anos",
                "Área efetiva (ha)",
                "Vol_Esperado_ha",
                "Dif_IFC_pct",
                "IcV",
                "IS",
            ]
            :
            [
                "Talhão",
                "Ano_Medicao",
                "Parcela",
                "DAP",
                "H",
                "DAPd",
                "Hd",
                "VTCC",
                "Fustes",
                "area_corr",
                "Idade_anos",
                "Área efetiva (ha)",
                "EQ_COD",
                "CURVA_COD",
                "VTCC_ha",
                "Fustes_ha",
                "VMI",
                "Vol_Esperado_ha",
                "Dif_IFC_pct",
                "IcV",
                "IS",
            ];
        var columns = names
            .Where(
                (name, i) =>
                    (temporal || name != "Ano_Medicao")
                    && (curves || i < (stand ? 12 : 17) || name == "IS")
                    && (name != "IS" || source.Column("IS") >= 0)
            )
            .ToList();
        if (debug && !stand)
            columns.Add("VMI_real");
        if (curves && !stand)
            columns.AddRange(["Curva_Modelo", "Curva_Ano"]);
        foreach (string column in new[] { "Base_Origem", "Tipo_Inventario", "Contexto_ID", "Parcela_ID" })
            if (source.Column(column) >= 0) columns.Add(column);
        columns.Insert(columns.IndexOf("Idade_anos") + 1, "Idade_meses");
        var output = new DataTable(columns);
        for (int r = 1; r < source.Count; r++)
            output.Add(columns.Select(c => c == "Idade_meses"
                ? (object?)(source.Number(r, source.Require("Idade_anos")) * 12)
                : source.Value(r, source.Column(c)) ?? "").ToArray());
        return output;
    }

    private static DataTable Height(DataTable input, JsonObject cfg, int? keepFileFilledHeightCount = null)
    {
        var output = new DataTable();
        output.Add("ESTATÍSTICAS DO AJUSTE DE ALTURA (H) - RNA POR LOTE");
        output.Add();
        output.Add("ESTATÍSTICAS GERAIS (TODOS OS LOTES)");
        output.Add("Métrica", "Valor", "Unidade");
        if (cfg["ajuste_altura"].S("h_mode", "KEEP_FILE") == "KEEP_FILE" && keepFileFilledHeightCount.HasValue)
        {
            string[] keepNames = ["R²", "RMSE", "MAE", "Bias", "Resíduo médio", "Desvio padrão resíduos", "N observações com predição", "N valores preenchidos"];
            string[] keepUnits = ["-", "m", "m", "m", "%", "%", "", ""];
            for (int i = 0; i < keepNames.Length; i++)
                output.Add(keepNames[i], i < 6 ? 0d : i == 6 ? 0d : (double)keepFileFilledHeightCount.Value, keepUnits[i]);
            output.Add("Modo", "KEEP_FILE");
            output.Add();
            output.Add("H_obs", "H_est", "Resíduo (%)", "Lote", "Parcela", "Método");
            return output;
        }
        var pairs = new List<int>();
        int filled = 0;
        for (int row = 1; row < input.Count; row++)
        {
            if (At(input, row, "H_obs") > 0 && double.IsFinite(At(input, row, "H_est"))) pairs.Add(row);
            if (!(At(input, row, "H_obs") > 0) && At(input, row, "H_final") > 0) filled++;
        }
        double[] metrics = HeightMetrics(input, pairs.ToArray());
        string[] names =

            [
                "R²",
                "RMSE",
                "MAE",
                "Bias",
                "Resíduo médio",
                "Desvio padrão resíduos",
                "N observações com predição",
                "N valores preenchidos",
            ],
            units = ["-", "m", "m", "m", "%", "%", "", ""];
        for (int i = 0; i < 8; i++)
            output.Add(
                names[i],
                i < 6 ? metrics[i]
                    : i == 6 ? (double)pairs.Count
                    : (double)filled,
                units[i]
            );
        output.Add("Modo", cfg["ajuste_altura"].S("h_mode", "KEEP_FILE"));
        output.Add();
        var cols = cfg["arquivo_entrada"]?["colunas"];
        string lot = cols.S("lote", "Lote"),
            plot = cols.S("parcela", "Parcela");
        var identification = new InventoryIdentity(input, cfg);
        string LotLabel(int row)
        {
            var scope = identification.Context(row);
            return Text(input, row, lot) + (scope.Id.Length > 0 ? " · " + string.Join(" · ", new[] { scope.Origin, scope.Type }.Where(s => s.Length > 0)) : "");
        }
        if (cfg["ajuste_altura"].S("h_mode", "KEEP_FILE") != "KEEP_FILE")
        {
            output.Add("ESTATÍSTICAS POR LOTE");
            output.Add(
                "Lote",
                "Método",
                "N total",
                "N observações com predição",
                "N valores preenchidos",
                "R²",
                "RMSE (m)",
                "MAE (m)",
                "Bias (m)",
                "Resíduo médio (%)",
                "Desvio padrão resíduos (%)"
            );
            foreach (
                var group in Enumerable.Range(1, input.Count - 1).GroupBy(r => (Lot: Text(input, r, lot), Scope: identification.Context(r)))
                    .OrderBy(g => g.Key.Lot, StringComparer.Ordinal)
            )
            {
                var selected = group
                    .Where(r => At(input, r, "H_obs") > 0 && double.IsFinite(At(input, r, "H_est")))
                    .ToArray();
                string[] methods = group
                    .Select(r => Text(input, r, "H_ajuste"))
                    .Distinct()
                    .ToArray();
                output.Add(
                    new object?[]
                    {
                        LotLabel(group.First()),
                        methods.Length == 1 ? methods[0] : "MISTO",
                        (double)group.Count(),
                        (double)selected.Length,
                        (double)
                            group.Count(r =>
                                !(At(input, r, "H_obs") > 0) && At(input, r, "H_final") > 0
                            ),
                    }
                        .Concat(HeightMetrics(input, selected).Cast<object?>())
                        .ToArray()
                );
            }
            output.Add();
        }
        output.Add("H_obs", "H_est", "Resíduo (%)", "Lote", "Parcela", "Método");
        foreach (int r in pairs)
            output.Add(
                At(input, r, "H_obs"),
                At(input, r, "H_est"),
                (At(input, r, "H_est") - At(input, r, "H_obs")) / At(input, r, "H_obs") * 100,
                LotLabel(r),
                Text(input, r, plot),
                Text(input, r, "H_ajuste")
            );
        return output;
    }

    private static double[] HeightMetrics(DataTable input, int[] rows)
    {
        int n = rows.Length;
        if (n == 0)
            return new double[6];
        double sum = 0,
            square = 0,
            absolute = 0,
            resSum = 0,
            resSquare = 0,
            obs = 0,
            obsSquare = 0;
        foreach (int r in rows)
        {
            double o = At(input, r, "H_obs"),
                d = At(input, r, "H_est") - o,
                res = d / o;
            sum += d;
            square += d * d;
            absolute += Math.Abs(d);
            resSum += res;
            resSquare += res * res;
            obs += o;
            obsSquare += o * o;
        }
        double ss = obsSquare - obs * obs / n;
        return
        [
            ss > 0 ? 1 - square / ss
            : square < 1e-12 ? 1
            : 0,
            Math.Sqrt(square / n) / 10,
            absolute / n / 10,
            sum / n / 10,
            resSum / n * 100,
            Math.Sqrt(Math.Max(0, resSquare / n - Math.Pow(resSum / n, 2))) * 100,
        ];
    }

    private static void Statistics(DataTable output, DataTable plots, JsonObject cfg, bool byLot)
    {
        int scopeColumn = plots.Column("Contexto_ID");
        if (scopeColumn < 0) { StatisticsSection(output, plots, cfg, byLot); return; }
        foreach (var scope in Enumerable.Range(1, plots.Count - 1).GroupBy(r => plots.Text(r, scopeColumn)))
        {
            var subset = new DataTable(); subset.Add(plots.Rows[0]);
            foreach (int row in scope) subset.Add(plots.Rows[row]);
            var section = new DataTable(); StatisticsSection(section, subset, cfg, byLot);
            int firstRow = scope.First(), extra = section.Rows[1].Length;
            string origin = Text(plots, firstRow, "Base_Origem"), type = Text(plots, firstRow, "Tipo_Inventario");
            string label = string.Join(" · ", new[] { origin, type }.Where(t => t.Length > 0));
            section.Set(0, 0, section.Text(0, 0) + " · " + label);
            for (int row = 1; row < section.Count; row++)
            {
                if (section.Text(row, 0).Length == 0) continue;
                bool header = section.Text(row, 0) is "Lot" or "Stand";
                if (!header && section.Text(row, 0) != "TOTAL") section.Set(row, 0, section.Text(row, 0) + " · " + label);
                section.Set(row, extra, header ? "Base_Origem" : origin);
                section.Set(row, extra + 1, header ? "Tipo_Inventario" : type);
                section.Set(row, extra + 2, header ? "Contexto_ID" : scope.Key);
            }
            foreach (var row in section.Rows) output.Add(row);
        }
    }

    private static void StatisticsSection(DataTable output, DataTable plots, JsonObject cfg, bool byLot)
    {
        double confidence = cfg["estatisticas"].N("nivel_confianca", .95),
            minimum = cfg["estatisticas"].N("min_parcelas_estatistica", 3);
        bool temporal = cfg["estatisticas"].B("chave_temporal_ativa");
        output.Add(byLot ? "INVENTORY STATISTICS BY LOT" : "INVENTORY STATISTICS BY STAND");
        output.Add(
            byLot ? "Lot" : "Stand",
            "n",
            "Área (ha)",
            "Idade",
            "DAP Med",
            "H Med",
            "DAPd Med",
            "Hd Med",
            "Fustes/ha",
            "Vol/ha Med",
            "Vol/ha Esp",
            "VMI",
            "Variância",
            "Desv.Pad",
            "CV(%)",
            "Erro Pad",
            $"t({confidence * 100:F0}%)",
            "Erro Abs",
            "Erro Rel(%)",
            "IC Inf",
            "IC Sup",
            "Vol Total",
            "Gini",
            "Homog",
            "IcV"
        );
        double totalArea = 0,
            totalVolume = 0,
            totalStems = 0,
            totalDapd = 0,
            totalDapdArea = 0,
            totalHd = 0,
            totalHdArea = 0,
            totalError = 0,
            errorArea = 0,
            totalIcv = 0,
            icvArea = 0;
        int totalN = 0;
        foreach (
            var group in Enumerable
                .Range(1, plots.Count - 1)
                .GroupBy(r =>
                    Text(plots, r, byLot ? "Lote" : "Talhão")
                    + (temporal ? $" ({At(plots, r, "Ano_Medicao"):F0})" : "")
                )
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            int n = group.Count();
            string[] fields =
            [
                "VTCC_ha",
                "DAP",
                "H",
                "Idade_anos",
                "Fustes_ha",
                "VMI",
                "Vol_Esperado_ha",
                "Dif_IFC_pct",
            ];
            double[] means = fields
                .Select(f => InventoryMath.Mean(group.Select(r => At(plots, r, f))))
                .ToArray();
            double dapd = DominantTreeMetrics.AreaWeightedMean(group.Select(r => (At(plots, r, "DAPd"), At(plots, r, "area_corr"))));
            double hd = DominantTreeMetrics.AreaWeightedMean(group.Select(r => (At(plots, r, "Hd"), At(plots, r, "area_corr"))));
            double area = 0;
            var seenStands = new HashSet<string>(StringComparer.Ordinal);
            var gini = new List<double>();
            foreach (int r in group)
            {
                string stand = Text(plots, r, "Talhão");
                if (seenStands.Add(stand))
                {
                    double x = At(plots, r, "Área efetiva (ha)");
                    area += x;
                }
                double dap = At(plots, r, "DAP");
                if (dap > 0 && double.IsFinite(dap))
                    gini.Add(Math.PI * Math.Pow(dap / 10, 2) / 40000);
            }
            double mean = means[0],
                variance = 0,
                sd = 0,
                se = 0,
                t = 0,
                absolute = 0,
                relative = 0,
                cv = 0;
            var volumes = group
                .Select(r => At(plots, r, "VTCC_ha"))
                .Where(double.IsFinite)
                .ToArray();
            if (n >= minimum && volumes.Length > 1)
            {
                variance = volumes.Sum(x => (x - mean) * (x - mean)) / (volumes.Length - 1);
                sd = Math.Sqrt(variance);
                cv = mean > 0 ? sd / mean * 100 : 0;
                se = sd / Math.Sqrt(n);
                t = StudentT(confidence, n - 1);
                absolute = t * se;
                relative = mean > 0 ? absolute / mean * 100 : 0;
            }
            double expected = double.IsFinite(means[6]) ? means[6] : 0,
                icv = expected > 0 ? mean / expected : double.NaN,
                g = gini.Count > 1 ? InventoryMath.Gini(gini) : 0,
                volume = mean * area;
            double[] values =
            [
                n,
                area,
                means[3],
                means[1],
                means[2],
                dapd,
                hd,
                means[4],
                mean,
                expected,
                means[5],
                variance,
                sd,
                cv,
                se,
                t,
                absolute,
                relative,
                mean - absolute,
                mean + absolute,
                volume,
                g,
                1 - g,
                icv,
            ];
            int[] decimals = [0, 2, 2, 2, 2, 2, 2, 1, 2, 2, 4, 2, 2, 2, 4, 3, 2, 2, 2, 2, 2, 2, 4, 4, 3];
            output.Add(
                new object?[] { group.Key }
                    .Concat(values.Select((v, i) => (object?)Round(v, decimals[i])))
                    .ToArray()
            );
            totalN += n;
            totalArea += area;
            totalVolume += volume;
            foreach (int r in group)
            {
                double plotArea = At(plots, r, "area_corr"), plotDapd = At(plots, r, "DAPd"), plotHd = At(plots, r, "Hd");
                if (double.IsFinite(plotArea) && plotArea > 0 && double.IsFinite(plotDapd)) { totalDapd += plotDapd * plotArea; totalDapdArea += plotArea; }
                if (double.IsFinite(plotArea) && plotArea > 0 && double.IsFinite(plotHd)) { totalHd += plotHd * plotArea; totalHdArea += plotArea; }
            }
            if (double.IsFinite(means[4]))
                totalStems += means[4] * area;
            if (n >= minimum)
            {
                totalError += relative * area;
                errorArea += area;
            }
            if (double.IsFinite(icv))
            {
                totalIcv += icv * area;
                icvArea += area;
            }
        }
        if (plots.Count > 1)
        {
            output.Add();
            int row = output.Count;
            output.Add("TOTAL", (double)totalN, Round(totalArea, 2));
            output.Set(row, 6, Round(totalDapdArea > 0 ? totalDapd / totalDapdArea : double.NaN, 2));
            output.Set(row, 7, Round(totalHdArea > 0 ? totalHd / totalHdArea : double.NaN, 2));
            output.Set(row, 8, Round(double.IsFinite(totalArea) ? totalArea > 0 ? totalStems / totalArea : 0 : double.NaN, 1));
            output.Set(row, 9, Round(double.IsFinite(totalArea) ? totalArea > 0 ? totalVolume / totalArea : 0 : double.NaN, 2));
            output.Set(row, 11, Round(double.IsFinite(totalStems) ? totalStems > 0 ? totalVolume / totalStems : 0 : double.NaN, 4));
            output.Set(row, 18, Round(double.IsFinite(errorArea) ? errorArea > 0 ? totalError / errorArea : 0 : double.NaN, 2));
            output.Set(row, 21, Round(totalVolume, 2));
            output.Set(row, 24, Round(icvArea > 0 ? totalIcv / icvArea : double.NaN, 3));
        }
        output.Add();
        output.Add();
    }

    private static double Round(double x, int decimals) =>
        double.IsFinite(x) ? Math.Round(x, decimals, MidpointRounding.AwayFromZero) : double.NaN;

    public static double StudentT(double confidence, int degrees)
    {
        if (confidence <= 0 || confidence >= 1 || degrees < 1)
            return double.NaN;
        double target = (1 - confidence) / 2,
            lo = 0,
            hi = 1;
        double Tail(double t) => .5 * Beta(degrees / (degrees + t * t), degrees / 2d, .5);
        while (Tail(hi) > target && hi < 1e12)
            hi *= 2;
        for (int i = 0; i < 100; i++)
        {
            double mid = (lo + hi) / 2;
            if (Tail(mid) > target)
                lo = mid;
            else
                hi = mid;
        }
        return (lo + hi) / 2;
    }

    private static double LogGamma(double z)
    {
        double[] c =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7,
        ];
        if (z < .5)
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * z)) - LogGamma(1 - z);
        z -= 1;
        double x = .99999999999980993;
        for (int i = 0; i < c.Length; i++)
            x += c[i] / (z + i + 1);
        double t = z + 7.5;
        return .5 * Math.Log(2 * Math.PI) + (z + .5) * Math.Log(t) - t + Math.Log(x);
    }

    private static double Beta(double x, double a, double b)
    {
        if (x <= 0)
            return 0;
        if (x >= 1)
            return 1;
        double factor = Math.Exp(
            LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x)
        );
        return x < (a + 1) / (a + b + 2)
            ? factor * Fraction(a, b, x) / a
            : 1 - factor * Fraction(b, a, 1 - x) / b;
    }

    private static double Fraction(double a, double b, double x)
    {
        double c = 1,
            d = 1 - (a + b) * x / (a + 1);
        if (Math.Abs(d) < 1e-300)
            d = 1e-300;
        d = 1 / d;
        double h = d;
        for (int m = 1; m <= 300; m++)
        {
            double mm = m,
                aa = mm * (b - mm) * x / ((a + 2 * mm - 1) * (a + 2 * mm));
            d = 1 + aa * d;
            if (Math.Abs(d) < 1e-300)
                d = 1e-300;
            c = 1 + aa / c;
            if (Math.Abs(c) < 1e-300)
                c = 1e-300;
            d = 1 / d;
            h *= d * c;
            aa = -(a + mm) * (a + b + mm) * x / ((a + 2 * mm) * (a + 2 * mm + 1));
            d = 1 + aa * d;
            if (Math.Abs(d) < 1e-300)
                d = 1e-300;
            c = 1 + aa / c;
            if (Math.Abs(c) < 1e-300)
                c = 1e-300;
            d = 1 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 2e-14)
                break;
        }
        return h;
    }
}
