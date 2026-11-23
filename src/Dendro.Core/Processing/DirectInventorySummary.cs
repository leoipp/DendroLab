using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Tabular;
using Dendro.Core.Statistics;

namespace Dendro.Core.Processing;

/// <summary>Streams tree output and retains plot aggregates rather than the complete tree/report tables.</summary>
public sealed class DirectInventorySummary : IDisposable
{
    public static readonly string[] Levels = ["arvore", "parcela", "talhao", "lote", "projeto"];
    public static JsonObject Defaults()
    {
        var columns=new JsonObject();foreach(string level in Levels){var fields=new JsonObject();foreach(string field in DirectOutputColumns.Headers(level))fields[field]=DirectOutputColumns.DefaultEnabled(level,field);columns[level]=fields;}
        return new() {
        ["modo"] = "completo", ["remover_duplicadas"] = false, ["inventory_type"]="",["factor_scope"]="talhao",["n_dominantes_parcela"] = 5,["calcular_is"] = false,["colunas_direto"]=columns,["saidas"] = new JsonObject {
            ["arvore"] = false, ["parcela"] = true, ["talhao"] = true, ["lote"] = true, ["projeto"] = true,
        },
        ["distribuicao_classes"] = false, ["largura_classe_dap_mm"] = 20, ["intervalo_classe_volume_m3"] = 0.1,
        };
    }
    public static void Validate(JsonNode? config)
    {
        if (config == null) return;
        DirectOutputColumns.Validate(config);
        double dominantTrees = config.N("n_dominantes_parcela", 5);
        if (!double.IsFinite(dominantTrees) || dominantTrees < 1 || dominantTrees > 10000 || dominantTrees != Math.Truncate(dominantTrees))
            throw new InvalidDataException("N de árvores dominantes por parcela deve ser um inteiro entre 1 e 10.000.");
        if (config["calcular_is"] is { } enabled && enabled.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new InvalidDataException("A opção de cálculo de IS deve estar marcada ou desmarcada.");
        if (config.B("distribuicao_classes")) _ = new DiameterClassDistribution(config.N("largura_classe_dap_mm", 20), config.N("intervalo_classe_volume_m3", 0.1));
        if (config.S("modo", "completo") is not ("completo" or "resumo_direto"))
            throw new InvalidDataException("Modo do Processador inválido.");
        if (config["saidas"] is not null && config["saidas"] is not JsonObject)
            throw new InvalidDataException("Saídas do resumo direto inválidas.");
        foreach (string level in Levels)
            if (config["saidas"]?[level] is { } value && value.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                throw new InvalidDataException($"Seleção de saída inválida: {level}.");
        if (config.S("modo") == "resumo_direto" && !Levels.Any(l => config["saidas"].B(l, l != "arvore")))
            throw new InvalidDataException("Selecione pelo menos um nível no resumo direto.");
    }

    public static void MigrateConfiguration(JsonObject project)
    {
        var processing = project.Obj("processador");
        if (processing.ContainsKey("modelo_is_id")) processing.Remove("modelo_is_id");
        if (processing.ContainsKey("n_dominantes_ha") && !processing.ContainsKey("n_dominantes_parcela"))
            processing["n_dominantes_parcela"] = 5;
        processing.Remove("n_dominantes_ha");
    }

    private sealed class Plot(string stand, string lot, string plot, string curve, int year, double effective, string id, InventoryContext context, int dominantCount)
    {
        public readonly string Id = id;
        public readonly InventoryContext Context = context;
        public readonly string Stand = stand, Lot = lot, Name = plot;
        public readonly string Curve = curve;
        public readonly int Year = year;
        public readonly double Effective = effective;
        public double Volume, CommercialVolume = double.NaN, Area, AgeSum;
        public readonly double[] TypedVolumes = new double[4];
        public readonly int[] TypedVolumeCounts = new int[4];
        public string Measurement = "";
        public double DapSum, DapSquareSum, HeightSum, BasalArea;
        public readonly DominantTreeMetrics Dominant = new(dominantCount);
        public int Rows, Stems, Invalid;
        public int ValidDap, ValidHeight;
        public double VolumeHa => Area > 0 ? Volume * 10000 / (Area / Rows) : double.NaN;
        public double TypedVolume(int index) => TypedVolumeCounts[index] > 0 ? TypedVolumes[index] : double.NaN;
        public double TypedVolumeHa(int index) => Area > 0 && TypedVolumeCounts[index] > 0 ? TypedVolumes[index] * 10000 / (Area / Rows) : double.NaN;
        public double StemsHa => Area > 0 ? Stems * 10000.0 / (Area / Rows) : double.NaN;
        public double Age => Rows > 0 ? AgeSum / Rows : double.NaN;
        public double MeanDap => ValidDap > 0 ? DapSum / ValidDap : double.NaN;
        public double QuadraticDap => ValidDap > 0 ? Math.Sqrt(DapSquareSum / ValidDap) : double.NaN;
        public double MeanHeight => ValidHeight > 0 ? HeightSum / ValidHeight : double.NaN;
    }
    private readonly Dictionary<string, Plot> plots = new(StringComparer.Ordinal);
    private readonly CompactBankWriter writer;
    private readonly string temporary, output, projectName;
    private readonly HashSet<string> levels;
    private readonly string? trees;
    private readonly JsonNode? processing;
    private readonly int dominantTreesPerPlot;
    private readonly DiameterClassDistribution? classDistribution;
    private readonly double confidence, minimumPlots;
    private readonly SiteIndexCalculator? siteIndex;
    private readonly Dictionary<string,int[]> projections=new();
    private string AddTable(string level,string[] columns,string name)
    {
        var selected=Enumerable.Range(0,columns.Length).Where(i=>DirectOutputColumns.Enabled(processing,level,columns[i])
            && (columns[i] != "IS" || processing?.B("calcular_is") == true)).ToArray();
        string id=writer.AddTable(selected.Select(i=>columns[i]).ToArray(),name);projections[id]=selected;return id;
    }
    private void AddRow(string id,object?[] row)=>writer.AddOwned(id,projections[id].Select(i=>row[i]).ToArray());
    private bool published;
    private int rows;
    private double volume;
    private long vcccPairs, vcccViolations;
    public DirectInventorySummary(string output, JsonObject config)
    {
        Validate(config["processador"]);
        processing=config["processador"]?.DeepClone();
        confidence=config.At("estatisticas").N("nivel_confianca",.95);
        minimumPlots=config.At("estatisticas").N("min_parcelas_estatistica",3);
        dominantTreesPerPlot=(int)(processing?.N("n_dominantes_parcela",5)??5);
        if (processing?.B("distribuicao_classes") == true)
            classDistribution = new(processing.N("largura_classe_dap_mm", 20), processing.N("intervalo_classe_volume_m3", 0.1));
        siteIndex = SiteIndexCalculator.Create(config);
        if (!Path.GetExtension(output).Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resumo direto deve ser gravado em DLB.");
        this.output = output;
        projectName = config["_dendro"].S("name", "Projeto");
        levels = Levels.Where(l => config.At("processador.saidas").B(l, l != "arvore")).ToHashSet();
        temporary = Path.Combine(Path.GetDirectoryName(output)!, ".dendrolab-" + Guid.NewGuid().ToString("N") + ".tmp");
        writer = new CompactBankWriter(temporary);
        if (levels.Contains("arvore")) trees = AddTable("arvore",[
            "Talhão", "Parcela", "Lote", "Ano_Medicao", "Linha_Origem", "DAP", "H", "AS", "Idade_anos", "Volume_m3",
            "VTCC_m3", "VCCC_m3", "VTSC_m3", "VCSC_m3",
            RegistryAgeFallback.FlagColumn, RegistryAgeFallback.DateColumn,
            "Parcela_ID", "Contexto_ID", "Base_Origem", "Tipo_Inventario",
        ], "DIRETO - ARVORES");
    }
    public void Add(string stand, string plot, string lot, int year, long sourceRow, double age,
        double treeVolume, bool stem, double area, double effective, string inferred, string planted,
        double dap = double.NaN, double height = double.NaN, string? plotId = null, InventoryContext? context = null, string measurement = "", string curveId = "",
        double commercialVolume = double.NaN, double vtcc = double.NaN, double vtsc = double.NaN, double vcsc = double.NaN)
    {
        rows++;
        if (double.IsFinite(treeVolume)) volume += treeVolume;
        if (double.IsFinite(vtcc) && double.IsFinite(commercialVolume))
        {
            vcccPairs++;
            if (vtcc <= commercialVolume) vcccViolations++;
        }
        context ??= new("", "", "");
        // The configured parcel identity is authoritative. Keep a fallback only
        // for callers that do not supply an identity key.
        string key = plotId ?? new JsonArray(stand, plot, year).ToJsonString();
        if (!plots.TryGetValue(key, out var p))
            plots.Add(key, p = new(stand, lot, plot, curveId, year, effective, key, context, dominantTreesPerPlot));
        else
        {
            if (p.Stand != stand || p.Lot != lot || p.Name != plot)
            {
                if (p.Lot != lot)
                    throw new InvalidDataException($"A chave de parcela configurada está agrupando lotes diferentes para o talhão '{stand}' e a parcela '{plot}' ({p.Lot} e {lot}). Inclua a coluna Lote em Configurações > Identificação > Chave de parcela.");
                if (p.Stand != stand)
                    throw new InvalidDataException($"A chave de parcela configurada está agrupando talhões diferentes para a parcela '{plot}' ({p.Stand} e {stand}). Inclua a coluna Talhão em Configurações > Identificação > Chave de parcela.");
                throw new InvalidDataException($"A chave de parcela configurada está agrupando números de parcela diferentes no talhão '{stand}' e lote '{lot}' ({p.Name} e {plot}). Inclua a coluna Parcela em Configurações > Identificação > Chave de parcela.");
            }
            if (p.Year != year || p.Measurement != measurement)
                throw new InvalidDataException($"A chave de parcela configurada está agrupando medições diferentes ({p.Measurement} e {measurement}) para o talhão '{stand}', parcela '{plot}' e lote '{lot}'. Inclua a data de medição na chave em Configurações > Identificação.");
            if (p.Curve != curveId)
                throw new InvalidDataException($"A parcela '{plot}' tem mais de um CURVA_COD ({p.Curve} e {curveId}); corrija a identificação antes de calcular IS.");
        }
        p.Measurement = p.Rows == 0 ? measurement : p.Measurement == measurement ? measurement : "";
        p.Rows++; p.Area += area; p.AgeSum += age;
        if (stem) p.Stems++;
        if (double.IsFinite(dap) && dap > 0)
        {
            p.DapSum += dap; p.DapSquareSum += dap * dap; p.ValidDap++;
            p.BasalArea += Math.PI * Math.Pow(dap / 10, 2) / 40000;
        }
        classDistribution?.Add(stand, plot, lot, year, key, context.Id, dap, treeVolume, context);
        if (double.IsFinite(height) && height > 0) { p.HeightSum += height; p.ValidHeight++; }
        p.Dominant.Add(dap, height);
        if (double.IsFinite(treeVolume)) p.Volume += treeVolume; else if (stem) p.Invalid++;
        double[] typedValues = [vtcc, commercialVolume, vtsc, vcsc];
        for (int index = 0; index < typedValues.Length; index++)
            if (double.IsFinite(typedValues[index]))
            {
                p.TypedVolumes[index] += typedValues[index];
                p.TypedVolumeCounts[index]++;
            }
        if (double.IsFinite(commercialVolume)) p.CommercialVolume = double.IsFinite(p.CommercialVolume) ? p.CommercialVolume + commercialVolume : commercialVolume;
        if (trees != null) AddRow(trees, [stand, plot, lot, year == 0 ? null : (double)year,
            (double)sourceRow, dap, height, Math.PI * Math.Pow(dap / 10, 2) / 40000, age, treeVolume, vtcc, commercialVolume, vtsc, vcsc,
            inferred, planted, key, context.Id, context.Origin, context.Type]);
    }
    public ProcessingResult Complete(int removedDuplicates = 0)
    {
        string[] volumeTypes = ModelDefinition.VolumeTypes;
        double[] TypedSums(IEnumerable<Plot> group) => volumeTypes.Select((_, index) =>
        {
            double[] values = group.Select(plot => plot.TypedVolume(index)).Where(double.IsFinite).ToArray();
            return values.Length > 0 ? values.Sum() : double.NaN;
        }).ToArray();
        double[] TypedPerHa(Plot[] group, string level, double effective)
        {
            var result = new double[volumeTypes.Length];
            for (int index = 0; index < result.Length; index++)
            {
                if (level == "parcela") result[index] = group[0].TypedVolumeHa(index);
                else if (level == "talhao") result[index] = MeanFinite(group.Select(plot => plot.TypedVolumeHa(index)));
                else if (effective > 0)
                {
                    double total = 0;
                    bool any = false;
                    foreach (var stand in group.GroupBy(plot => plot.Stand))
                    {
                        double standMean = MeanFinite(stand.Select(plot => plot.TypedVolumeHa(index)));
                        double area = stand.First().Effective;
                        if (!double.IsFinite(standMean) || !double.IsFinite(area)) continue;
                        total += standMean * area;
                        any = true;
                    }
                    result[index] = any ? total / effective : double.NaN;
                }
                else result[index] = double.NaN;
            }
            return result;
        }
        static double MeanFinite(IEnumerable<double> values)
        {
            double[] finite = values.Where(double.IsFinite).ToArray();
            return finite.Length > 0 ? finite.Average() : double.NaN;
        }
        void Write(string level, string name, IEnumerable<(string Name, IEnumerable<Plot> Plots)> groups)
        {
            if (!levels.Contains(level)) return;
            bool includeAge = level is "parcela" or "talhao";
            string id = AddTable(level, DirectOutputColumns.Headers(level), name);
            foreach (var group in groups)
            {
                var all = group.Plots.ToArray(); var first = all[0];
                double rawVolume = all.Sum(p => p.Volume), stems = all.Sum(p => p.Stems);
                double effective = 0, totalVolume = 0, totalStems = 0;
                foreach (var stand in all.GroupBy(p => p.Stand)) {
                    double area = stand.First().Effective;
                    effective += area;
                    totalVolume += stand.Average(p => p.VolumeHa) * area;
                    totalStems += stand.Average(p => p.StemsHa) * area;
                }
                double volumeHa = level == "parcela" ? first.VolumeHa : level == "talhao" ? all.Average(p => p.VolumeHa)
                    : effective > 0 ? totalVolume / effective : double.NaN;
                double[] sampledTyped = TypedSums(all);
                double[] typedHa = TypedPerHa(all, level, effective);
                double[] typedTotals = typedHa.Select(value => level == "parcela" ? double.NaN : value * effective).ToArray();
                var strata = all.GroupBy(plot => plot.Stand, StringComparer.Ordinal)
                    .Select(group => new AreaStratumSample(group.First().Effective, group.Select(plot => plot.VolumeHa).ToArray()));
                double absoluteError = StratifiedSamplingStatistics.AreaWeightedMarginOfError(
                    strata, confidence, (int)Math.Ceiling(minimumPlots));
                double samplingError = double.IsFinite(volumeHa) && volumeHa > 0 && double.IsFinite(absoluteError)
                    ? absoluteError / volumeHa * 100 : double.NaN;
                double stemsHa = level == "parcela" ? first.StemsHa : level == "talhao" ? all.Average(p => p.StemsHa)
                    : effective > 0 ? totalStems / effective : double.NaN;
                double dapd = DominantTreeMetrics.AreaWeightedMean(all.Select(p => (p.Dominant.Dap, p.Area / p.Rows)));
                double hd = DominantTreeMetrics.AreaWeightedMean(all.Select(p => (p.Dominant.Height, p.Area / p.Rows)));
                double SiteIndex(Plot plot) => siteIndex?.Calculate(plot.Curve, plot.Age, plot.Dominant.Dap, plot.Dominant.Height) ?? double.NaN;
                double siteIndexValue = level == "parcela" ? SiteIndex(first)
                    : DominantTreeMetrics.AreaWeightedMean(all.Select(p => (SiteIndex(p), p.Area / p.Rows)));
                // A plot is a sample; extrapolated stand totals do not belong to individual plot rows.
                if (level == "parcela") effective = totalVolume = totalStems = double.NaN;
                double age = all.Average(p => p.Age);
                if (level == "parcela")
                {
                    AddRow(id, [group.Name, first.Stand, first.Name, first.Lot, first.Year == 0 ? null : (double)first.Year,
                        (double)all.Length, first.MeanDap, first.QuadraticDap, first.MeanHeight, first.Dominant.Dap, first.Dominant.Height,
                        siteIndexValue, all.Sum(p => p.BasalArea), .. typedHa, stemsHa,
                        stemsHa > 0 ? volumeHa / stemsHa : double.NaN, effective, totalVolume, .. typedTotals, totalStems,
                        (double)all.Sum(p => p.Invalid), age, age * 12, first.Measurement, first.Area / first.Rows,
                        first.Id, first.Context.Id, first.Context.Origin, first.Context.Type]);
                }
                else
                {
                    AddRow(id, [group.Name, level == "talhao" ? first.Stand : "", "", level is "parcela" or "lote" ? first.Lot : "",
                        first.Year == 0 ? null : (double)first.Year, (double)all.Length, rawVolume, .. sampledTyped, stems,
                        stems > 0 ? rawVolume / stems : double.NaN, .. typedHa, stemsHa,
                        stemsHa > 0 ? volumeHa / stemsHa : double.NaN, dapd, hd, siteIndexValue, effective, totalVolume,
                        .. typedTotals, totalStems, (double)all.Sum(p => p.Invalid),
                        .. (level == "talhao" ? new object?[] { age, age * 12 } : Array.Empty<object?>()),
                        .. (level is "talhao" or "lote" or "projeto" ? new object?[] { absoluteError, samplingError } : Array.Empty<object?>()),
                        all.Select(p => p.Measurement).Where(s => s.Length > 0).Order(StringComparer.Ordinal).LastOrDefault() ?? "",
                        "", first.Context.Id, first.Context.Origin, first.Context.Type]);
                }
            }
        }
        var values = plots.Values.OrderBy(p => p.Stand, StringComparer.Ordinal).ThenBy(p => p.Year).ThenBy(p => p.Name, StringComparer.Ordinal).ToArray();
        Write("parcela", "DIRETO - PARCELAS", values.Select(p => (p.Name, (IEnumerable<Plot>)new[] { p })));
        Write("talhao", "DIRETO - TALHOES", values.GroupBy(p => (p.Context.Id, p.Stand, p.Year)).Select(g => (g.Key.Stand, (IEnumerable<Plot>)g)));
        Write("lote", "DIRETO - LOTES", values.GroupBy(p => (p.Context.Id, p.Lot, p.Year)).Select(g => (g.Key.Lot, (IEnumerable<Plot>)g)));
        Write("projeto", "DIRETO - PROJETO", values.GroupBy(p => (p.Context.Id, p.Year)).Select(g => (projectName, (IEnumerable<Plot>)g)));
        if (classDistribution != null)
            foreach (var (name, table) in classDistribution.CreateTables(processing?["saidas"] as JsonObject))
            {
                string id = writer.AddTable(Enumerable.Range(0, table.Width).Select(c => table.Text(0, c)).ToArray(), name);
                for (int row = 1; row < table.Count; row++) writer.Add(id, Enumerable.Range(0, table.Width).Select(c => table.Value(row, c)).ToArray());
            }
        string info = writer.AddTable(["Item", "Descrição"], "INFORMACOES");
        if (vcccPairs > 0)
        {
            string audit = writer.AddTable(["Regra", "Pares avaliados", "Violações", "Resultado"], "AUDITORIA VOLUMES");
            writer.Add(audit, ["VTCC > VCCC", (double)vcccPairs, (double)vcccViolations, vcccViolations == 0 ? "Conforme" : "Revisar estimativas"]);
        }
        writer.Add(info, ["Modo", "Resumo direto: volumetria, fustes e VMI; sem curvas de crescimento, comparação IFC e relatórios estatísticos completos."]);
        writer.Add(info, ["Tipos de volume", "As equações ativas VTCC, VCCC, VTSC e VCSC são calculadas na ordem configurada. Volume_m3 e os indicadores derivados usam a primeira equação disponível para cada código; colunas tipadas podem ser habilitadas por nível."]);
        writer.Add(info, ["Amostra e totais", "Volume/fustes amostrados são somas observadas. Por hectare: média entre parcelas por talhão; lote/projeto ponderam talhões por área efetiva. Totais dependem dessa área e ficam vazios se desconhecida."]);
        writer.Add(info, ["Erro amostral", $"Erro absoluto e erro amostral (%) do volume principal são calculados nos níveis de lote e projeto com IC t de {confidence:P0}, ponderação por área efetiva entre talhões e variância dentro de cada talhão. Exigem ao menos {minimumPlots:G} parcelas no total e duas por talhão. Graus de liberdade usam aproximação de Satterthwaite; não há correção para população finita porque o tamanho populacional por talhão não está disponível. O erro absoluto está em m³/ha."]);
        writer.Add(info, ["Modelos de volume", "Cada tipo usa a equação cadastrada. Modelos configurados como razão multiplicam o volume anterior da sequência; modelos de saída direta usam o valor calculado e o fator informado no resumo direto."]);
        if (vcccPairs > 0) writer.Add(info, ["Auditoria de hierarquia dos volumes", $"VTCC > VCCC: {vcccViolations:N0} violações em {vcccPairs:N0} árvores avaliadas."]);
        writer.Add(info, ["Volumes ausentes", "Fustes_sem_volume identifica DAP positivo sem volume válido; reveja os dados antes de interpretar VMI e totais."]);
        writer.Add(info, ["Remover árvores duplicadas", processing?.B("remover_duplicadas") == true ? "Ativado: mantém a primeira ocorrência de cada chave configurada." : "Desativado."]);
        writer.Add(info, ["Linhas de entrada", (double)(rows + removedDuplicates)]);
        writer.Add(info, ["Linhas usadas no cálculo", (double)rows]);
        writer.Add(info, ["Linhas duplicadas removidas antes da RNA e das somas", (double)removedDuplicates]);
        writer.Add(info, ["Unidades", "DAP, Dq e DAPd: mm; H e Hd: dm. DAPd e Hd são médias das N árvores com maiores DAP em cada parcela, e os níveis acima da parcela ponderam os resultados pela área da parcela. AS = π × (DAP_mm / 10)² / 40000. Área corrigida: m²."]);
        if (siteIndex != null)
        {
            writer.Add(info, ["Índice de sítio (IS)", "A equação vigente é escolhida pelo CURVA_COD de cada parcela. IS = Y × f(idade-base) / f(idade da parcela); Y é DAPd ou Hd conforme a equação. Níveis acima ponderam IS pela área da parcela."]);
            foreach (string code in values.Select(plot => plot.Curve).Where(code => code.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                writer.Add(info, ["IS · " + code, siteIndex.Describe(code)]);
        }
        writer.Add(info,["Colunas selecionadas",processing?["colunas_direto"]?.ToJsonString()??"Todas as colunas padrão habilitadas"]);
        writer.Add(info,["Data dos níveis agregados","Em talhão, lote e projeto, Data_Medicao registra a última data das parcelas abrangidas pelo grupo. Os indicadores continuam agregando as parcelas do grupo; a data não transforma o resultado em medição de uma única parcela."]);
        writer.Complete(); writer.Dispose(); File.Move(temporary, output); published = true;
        return new(rows, plots.Count, values.Select(p => (p.Context.Id, p.Stand, p.Year)).Distinct().Count(), volume, removedDuplicates,
            InventoryTypes: values.Select(p => p.Context.Type).Where(t => t.Length > 0).Distinct().Order().ToArray());
    }
    public void Dispose() { writer.Dispose(); if (!published && File.Exists(temporary)) File.Delete(temporary); }

}
