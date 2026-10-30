using System.Text.Json.Nodes;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

/// <summary>Streams tree output and retains plot aggregates rather than the complete tree/report tables.</summary>
public sealed class DirectInventorySummary : IDisposable
{
    public static readonly string[] Levels = ["arvore", "parcela", "talhao", "lote", "projeto"];
    public static JsonObject Defaults()
    {
        var columns=new JsonObject();foreach(string level in Levels){var fields=new JsonObject();foreach(string field in DirectOutputColumns.Headers(level))fields[field]=true;columns[level]=fields;}
        return new() {
        ["modo"] = "completo", ["remover_duplicadas"] = false, ["inventory_type"]="",["factor_scope"]="talhao",["colunas_direto"]=columns,["saidas"] = new JsonObject {
            ["arvore"] = false, ["parcela"] = true, ["talhao"] = true, ["lote"] = true, ["projeto"] = true,
        },
        };
    }
    public static void Validate(JsonNode? config)
    {
        if (config == null) return;
        DirectOutputColumns.Validate(config);
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
    private sealed class Plot(string stand, string lot, string plot, int year, double effective, string id, InventoryContext context)
    {
        public readonly string Id = id;
        public readonly InventoryContext Context = context;
        public readonly string Stand = stand, Lot = lot, Name = plot;
        public readonly int Year = year;
        public readonly double Effective = effective;
        public double Volume, Area, AgeSum;
        public string Measurement = "";
        public double DapSum, DapSquareSum, HeightSum, BasalArea;
        public int Rows, Stems, Invalid;
        public int ValidDap, ValidHeight;
        public double VolumeHa => Area > 0 ? Volume * 10000 / (Area / Rows) : double.NaN;
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
    private readonly Dictionary<string,int[]> projections=new();
    private string AddTable(string level,string[] columns,string name)
    {
        var selected=Enumerable.Range(0,columns.Length).Where(i=>DirectOutputColumns.Enabled(processing,level,columns[i])).ToArray();
        string id=writer.AddTable(selected.Select(i=>columns[i]).ToArray(),name);projections[id]=selected;return id;
    }
    private void AddRow(string id,object?[] row)=>writer.Add(id,projections[id].Select(i=>row[i]).ToArray());
    private bool published;
    private int rows;
    private double volume;
    public DirectInventorySummary(string output, JsonObject config)
    {
        Validate(config["processador"]);
        processing=config["processador"]?.DeepClone();
        if (!Path.GetExtension(output).Equals(CompactBankReader.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resumo direto deve ser gravado em DLB.");
        this.output = output;
        projectName = config["_dendro"].S("name", "Projeto");
        levels = Levels.Where(l => config.At("processador.saidas").B(l, l != "arvore")).ToHashSet();
        temporary = Path.Combine(Path.GetDirectoryName(output)!, ".dendro-" + Guid.NewGuid().ToString("N") + ".tmp");
        writer = new CompactBankWriter(temporary);
        if (levels.Contains("arvore")) trees = AddTable("arvore",[
            "Talhão", "Parcela", "Lote", "Ano_Medicao", "Linha_Origem", "DAP", "H", "AS", "Idade_anos", "Volume_m3",
            RegistryAgeFallback.FlagColumn, RegistryAgeFallback.DateColumn,
            "Parcela_ID", "Contexto_ID", "Base_Origem", "Tipo_Inventario",
        ], "DIRETO - ARVORES");
    }
    public void Add(string stand, string plot, string lot, int year, int sourceRow, double age,
        double treeVolume, bool stem, double area, double effective, string inferred, string planted,
        double dap = double.NaN, double height = double.NaN, string? plotId = null, InventoryContext? context = null, string measurement = "")
    {
        rows++;
        if (double.IsFinite(treeVolume)) volume += treeVolume;
        context ??= new("", "", "");
        string key = plotId ?? new JsonArray(stand, plot, year).ToJsonString();
        if (!plots.TryGetValue(key, out var p)) plots.Add(key, p = new(stand, lot, plot, year, effective, key, context));
        else if (p.Stand != stand || p.Lot != lot || p.Name != plot) throw new InvalidDataException("A chave de parcela está agrupando talhões, lotes ou números de parcela diferentes. Revise Configurações > Identificação.");
        p.Measurement = p.Rows == 0 ? measurement : p.Measurement == measurement ? measurement : "";
        p.Rows++; p.Area += area; p.AgeSum += age;
        if (stem) p.Stems++;
        if (double.IsFinite(dap) && dap > 0)
        {
            p.DapSum += dap; p.DapSquareSum += dap * dap; p.ValidDap++;
            p.BasalArea += Math.PI * Math.Pow(dap / 10, 2) / 40000;
        }
        if (double.IsFinite(height) && height > 0) { p.HeightSum += height; p.ValidHeight++; }
        if (double.IsFinite(treeVolume)) p.Volume += treeVolume; else if (stem) p.Invalid++;
        if (trees != null) AddRow(trees, [stand, plot, lot, year == 0 ? null : (double)year,
            (double)sourceRow, dap, height, Math.PI * Math.Pow(dap / 10, 2) / 40000, age, treeVolume,
            inferred, planted, key, context.Id, context.Origin, context.Type]);
    }
    public ProcessingResult Complete(int removedDuplicates = 0)
    {
        string[] headers = ["Grupo", "Talhão", "Parcela", "Lote", "Ano_Medicao", "N_Parcelas", "Volume_amostrado_m3",
            "Fustes_amostrados", "VMI_amostrado_m3_fuste", "Volume_m3_ha", "Fustes_ha", "VMI_m3_fuste",
            "Area_efetiva_ha", "Volume_total_m3", "Fustes_totais_estimados", "Fustes_sem_volume"];
        void Write(string level, string name, IEnumerable<(string Name, IEnumerable<Plot> Plots)> groups)
        {
            if (!levels.Contains(level)) return;
            bool includeAge = level is "parcela" or "talhao";
            string[] parcelHeaders = ["Grupo", "Talhão", "Parcela", "Lote", "Ano_Medicao", "N_Parcelas",
                "DAP_medio", "Dq", "H_medio", "AB", "Volume_m3_ha", "Fustes_ha", "VMI_m3_fuste",
                "Area_efetiva_ha", "Volume_total_m3", "Fustes_totais_estimados", "Fustes_sem_volume",
                "Idade_anos", "Idade_meses"];
            string[] selectedHeaders = level == "parcela" ? [.. parcelHeaders, "Data_Medicao", "area_corr"] : includeAge ? [.. headers, "Idade_anos", "Idade_meses", "Data_Medicao"] : [..headers,"Data_Medicao"];
            string id = AddTable(level,[.. selectedHeaders, "Parcela_ID", "Contexto_ID", "Base_Origem", "Tipo_Inventario"], name);
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
                double stemsHa = level == "parcela" ? first.StemsHa : level == "talhao" ? all.Average(p => p.StemsHa)
                    : effective > 0 ? totalStems / effective : double.NaN;
                // A plot is a sample; extrapolated stand totals do not belong to individual plot rows.
                if (level == "parcela") effective = totalVolume = totalStems = double.NaN;
                object?[] cells = [group.Name, level is "parcela" or "talhao" ? first.Stand : "",
                    level == "parcela" ? first.Name : "", level is "parcela" or "lote" ? first.Lot : "",
                    first.Year == 0 ? null : (double)first.Year, (double)all.Length, rawVolume, stems,
                    stems > 0 ? rawVolume / stems : double.NaN, volumeHa, stemsHa,
                    stemsHa > 0 ? volumeHa / stemsHa : double.NaN, effective, totalVolume, totalStems, (double)all.Sum(p => p.Invalid)];
                double age = all.Average(p => p.Age);
                if (level == "parcela")
                    AddRow(id, [group.Name, first.Stand, first.Name, first.Lot,
                        first.Year == 0 ? null : (double)first.Year, (double)all.Length,
                        first.MeanDap, first.QuadraticDap, first.MeanHeight, all.Sum(p => p.BasalArea),
                        volumeHa, stemsHa, stemsHa > 0 ? volumeHa / stemsHa : double.NaN,
                        effective, totalVolume, totalStems, (double)all.Sum(p => p.Invalid), age, age * 12, first.Measurement, first.Area / first.Rows, first.Id, first.Context.Id, first.Context.Origin, first.Context.Type]);
                else
                    AddRow(id, [.. (includeAge ? [.. cells, age, age * 12] : cells), all.Select(p=>p.Measurement).Where(s=>s.Length>0).Order(StringComparer.Ordinal).LastOrDefault()??"", "", first.Context.Id, first.Context.Origin, first.Context.Type]);
            }
        }
        var values = plots.Values.OrderBy(p => p.Stand, StringComparer.Ordinal).ThenBy(p => p.Year).ThenBy(p => p.Name, StringComparer.Ordinal).ToArray();
        Write("parcela", "DIRETO - PARCELAS", values.Select(p => (p.Name, (IEnumerable<Plot>)new[] { p })));
        Write("talhao", "DIRETO - TALHOES", values.GroupBy(p => (p.Context.Id, p.Stand, p.Year)).Select(g => (g.Key.Stand, (IEnumerable<Plot>)g)));
        Write("lote", "DIRETO - LOTES", values.GroupBy(p => (p.Context.Id, p.Lot, p.Year)).Select(g => (g.Key.Lot, (IEnumerable<Plot>)g)));
        Write("projeto", "DIRETO - PROJETO", values.GroupBy(p => (p.Context.Id, p.Year)).Select(g => (projectName, (IEnumerable<Plot>)g)));
        string info = writer.AddTable(["Item", "Descrição"], "INFORMACOES");
        writer.Add(info, ["Modo", "Resumo direto: volumetria, fustes e VMI; sem curvas de crescimento, comparação IFC e relatórios estatísticos completos."]);
        writer.Add(info, ["Amostra e totais", "Volume/fustes amostrados são somas observadas. Por hectare: média entre parcelas por talhão; lote/projeto ponderam talhões por área efetiva. Totais dependem dessa área e ficam vazios se desconhecida."]);
        writer.Add(info, ["Volumes ausentes", "Fustes_sem_volume identifica DAP positivo sem volume válido; reveja os dados antes de interpretar VMI e totais."]);
        writer.Add(info, ["Linhas duplicadas removidas antes da RNA e das somas", (double)removedDuplicates]);
        writer.Add(info, ["Unidades", "DAP e Dq: mm; H: dm; AS e AB: m². AS = π × (DAP_mm / 10)² / 40000. Área corrigida: m². Data_Medicao é preservada por parcela quando única."]);
        writer.Add(info,["Colunas selecionadas",processing?["colunas_direto"]?.ToJsonString()??"Todas as colunas padrão habilitadas"]);
        writer.Add(info,["Data dos níveis agregados","Em talhão, lote e projeto, Data_Medicao registra a última data das parcelas abrangidas pelo grupo. Os indicadores continuam agregando as parcelas do grupo; a data não transforma o resultado em medição de uma única parcela."]);
        writer.Complete(); writer.Dispose(); File.Move(temporary, output); published = true;
        return new(rows, plots.Count, values.Select(p => (p.Context.Id, p.Stand, p.Year)).Distinct().Count(), volume, removedDuplicates,
            InventoryTypes: values.Select(p => p.Context.Type).Where(t => t.Length > 0).Distinct().Order().ToArray());
    }
    public void Dispose() { writer.Dispose(); if (!published && File.Exists(temporary)) File.Delete(temporary); }
}
