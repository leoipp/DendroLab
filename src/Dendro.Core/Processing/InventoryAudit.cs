using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;
using Dendro.Core.Regression;
using Dendro.Core.Statistics;

namespace Dendro.Core.Processing;

public sealed record InventoryAuditOptions(double VolumeAlert = 1000, double HeightAlert = 50,
    double DapAlert = 80, double DensityAlert = 2000, double AgeBand = 5, int MinimumPeers = 8,
    double IqrMultiplier = 3, int SourcePlotLimit = 200, string[]? PeerColumns = null,
    double DeclineTolerancePercent = 0, string[]? TemporalColumns = null);

public sealed record InventoryAuditResult(DataTable Plots, DataTable Trees, DataTable Information, DataTable Evolution);

/// <summary>Read-only evidence, independent arithmetic and local peer comparison. Alerts are review requests, not error labels.</summary>
public static class InventoryAudit
{
    public static AnalysisReport Presentation(InventoryAuditResult result)
    {
        var data = result.Plots;
        int age = data.Require("Idade_anos"), volume = data.Require("Volume_m3_ha"), flag = data.Require("Sinalizada");
        var points = Enumerable.Range(1,data.Count-1).Where(r=>double.IsFinite(data.Number(r,age)) && double.IsFinite(data.Number(r,volume)))
            .Select(r=>new ChartPoint(data.Number(r,age),data.Number(r,volume),r+1,data.Text(r,flag)=="Sim"?"Revisar":"Sem alerta")).ToArray();
        var summary = new JsonObject { ["test"]="Auditoria pós-processamento", ["decision"]=$"{Enumerable.Range(1,data.Count-1).Count(r=>data.Text(r,flag)=="Sim"):N0} parcela(s) sinalizada(s) entre {data.Count-1:N0}.",
            ["notes"]="Alertas indicam revisão, não erro comprovado. Consulte o banco de auditoria: parcelas, evidências por árvore e informações/configuração utilizadas.",
            ["method"]="Conferência de V = N × VMI, grupos de lote/origem/tipo/faixa de idade e recomposição independente da origem selecionada. Sem exclusão ou ajuste automático." };
        return new(summary,data,new(new JsonObject { ["title"]="Auditoria · volume e idade", ["x_label"]="Idade (anos)", ["y_label"]="Volume (m³/ha)",
            ["point_alpha"]=.45, ["point_size"]=3, ["group_colors"]=new JsonObject { ["Revisar"]="#C33E5E", ["Sem alerta"]="#21669D" } },points,[],0));
    }
    private sealed class Plot(int row, string id, double volume, double density, double vmi, double age, string lot)
    {
        public int Row = row;
        public string Id = id, Lot = lot, PeerKey = "";
        public double Volume = volume, Density = density, Vmi = vmi, Age = age;
        public double PeerMedian = double.NaN, PeerLimit = double.NaN, PeerDensity = double.NaN, PeerVmi = double.NaN;
        public int Peers;
        public List<string> Notes = [];
        public SourceEvidence? Source;
    }
    private sealed class SourceEvidence
    {
        public Dictionary<string, string> Keys = new(StringComparer.Ordinal);
        public HashSet<string> Dimensions = new(StringComparer.Ordinal);
        public int Rows, Duplicates, Conflicts, Stems, MissingHeight, HighHeight, HighDap, Negative, InvalidArea, FractionalHeight;
        public double AreaSum, MinArea = double.PositiveInfinity, MaxArea, Volume, BasalArea, HeightSum, MaxHeight, MaxDap;
        public bool StandardEquation = true;
        public List<double> Contributions = [];
        public int Unique => Keys.Count;
        public double Area => Unique > InvalidArea ? AreaSum / (Unique - InvalidArea) : double.NaN;
    }
    private static double Q(double[] sorted, double probability)
    {
        if (sorted.Length == 0) return double.NaN;
        double index = probability * (sorted.Length - 1); int lo = (int)index, hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] * (1 - index + lo) + sorted[hi] * (index - lo);
    }
    private static double Relative(double value, double baseline) => baseline > 0 ? (value / baseline - 1) * 100 : double.NaN;
    private static bool Differs(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) > Math.Max(1e-7, Math.Max(Math.Abs(a), Math.Abs(b)) * 1e-6);
    private static string F(double value) => value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));

    public static InventoryAuditResult Run(DataTable results, JsonObject configuration, TableSource? source = null,
        InventoryAuditOptions? options = null, CancellationToken cancellation = default, Action<double, string>? progress = null)
    {
        var o = options ?? new();
        if (new[] { o.VolumeAlert, o.HeightAlert, o.DapAlert, o.DensityAlert, o.AgeBand }.Any(v => !double.IsFinite(v) || v <= 0)
            || !double.IsFinite(o.IqrMultiplier) || o.IqrMultiplier < 0 || o.MinimumPeers < 3 || o.SourcePlotLimit is < 1 or > 5000
            || !double.IsFinite(o.DeclineTolerancePercent) || o.DeclineTolerancePercent is < 0 or > 100)
            throw new InvalidDataException("Parâmetros inválidos da auditoria. Limiares e faixa de idade devem ser positivos; mínimo de 3 pares e de 1 a 5.000 parcelas na origem.");
        var cfg = configuration.DeepClone().AsObject(); InventoryIdentity.Validate(cfg);
        bool direct = results.Column("Volume_m3_ha") >= 0;
        int Col(params string[] names) => names.Select(results.Column).FirstOrDefault(i => i >= 0, -1);
        int volumeColumn = Col("Volume_m3_ha", "VTCC_ha"), densityColumn = Col("Fustes_ha"), vmiColumn = Col("VMI_m3_fuste", "VMI"),
            idColumn = Col("Parcela_ID"), ageColumn = Col("Idade_anos"), lotColumn = Col("Lote"), plotColumn = Col("Parcela");
        if (volumeColumn < 0 || densityColumn < 0 || vmiColumn < 0 || plotColumn < 0)
            throw new InvalidDataException("Selecione DIRETO - PARCELAS ou RESULTADO - PARCELAS, contendo volume por hectare, fustes por hectare, VMI e Parcela.");
        if (source != null && idColumn < 0) throw new InvalidDataException("A conferência da origem exige Parcela_ID no resultado. Reprocesse a versão antiga para registrar a chave.");
        string[] peerNames = o.PeerColumns ?? ["Lote", "Base_Origem", "Tipo_Inventario"];
        int[] peerColumns = peerNames.Select(results.Require).ToArray();
        var plots = new List<Plot>();
        for (int row = 1; row < results.Count; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            var p = new Plot(row, idColumn >= 0 ? results.Text(row, idColumn) : row.ToString(), results.Number(row, volumeColumn),
                results.Number(row, densityColumn), results.Number(row, vmiColumn), results.Number(row, ageColumn), results.Text(row, lotColumn));
            if (double.IsFinite(p.Age) && p.Age >= 0)
                p.PeerKey = JsonSerializer.Serialize(peerColumns.Select(c => results.Text(row, c)).Append(Math.Floor(p.Age / o.AgeBand).ToString(CultureInfo.InvariantCulture)));
            plots.Add(p);
        }
        foreach (var group in plots.Where(p => p.PeerKey.Length > 0).GroupBy(p => p.PeerKey))
        {
            var valid = group.Where(p => double.IsFinite(p.Volume) && p.Volume >= 0).ToArray();
            double[] volumes = valid.Select(p => p.Volume).Order().ToArray();
            double median = Q(volumes, .5), q1 = Q(volumes, .25), q3 = Q(volumes, .75);
            double density = Q(valid.Select(p => p.Density).Where(double.IsFinite).Order().ToArray(), .5),
                vmi = Q(valid.Select(p => p.Vmi).Where(double.IsFinite).Order().ToArray(), .5);
            foreach (var p in group)
            {
                p.Peers = valid.Length; p.PeerMedian = median; p.PeerDensity = density; p.PeerVmi = vmi;
                p.PeerLimit = valid.Length >= o.MinimumPeers ? q3 + o.IqrMultiplier * (q3 - q1) : double.NaN;
            }
        }
        foreach (var p in plots)
        {
            if (!double.IsFinite(p.Volume) || p.Volume < 0 || !double.IsFinite(p.Density) || p.Density < 0 || (p.Density > 0 && (!double.IsFinite(p.Vmi) || p.Vmi < 0))) p.Notes.Add("Indicador ausente, negativo ou inválido");
            if (p.Volume > o.VolumeAlert) p.Notes.Add("Volume acima do limiar de triagem");
            if (double.IsFinite(p.PeerLimit) && p.Volume > p.PeerLimit) p.Notes.Add("Volume acima de Q3 + multiplicador × IQR do grupo de referência");
            if (p.Density > o.DensityAlert) p.Notes.Add("Densidade acima do limiar de triagem");
            if (Differs(p.Volume, p.Density * p.Vmi)) p.Notes.Add("Volume/ha difere de fustes/ha × VMI");
        }
        var evolution = Evolution(results,o,cancellation);
        var temporalIds = evolution.Rows.Skip(1).Where(r=>r[evolution.Require("Situacao")]?.ToString()=="Diminuiu — revisar")
            .SelectMany(r=>new[]{r[evolution.Require("Parcela_ID_anterior")]?.ToString(),r[evolution.Require("Parcela_ID_atual")]?.ToString()}).Where(s=>!string.IsNullOrEmpty(s)).ToHashSet();
        foreach(var p in plots.Where(p=>temporalIds.Contains(p.Id)))p.Notes.Add("Participa de intervalo com queda de volume; conferir evolução, população e continuidade da medição");
        var trees = new DataTable(["Parcela_ID", "Talhão", "Parcela", "Fonte", "Tipo_Inventario", "Linha_Origem", "Arvore_ID",
            "Data_Origem", "Cova", "Fuste", "Fila", "DAP_cm", "H_origem_m", "Area_corr_m2", "AS_m2", "Equacao", "Volume_origem_m3", "Fator_forma_aparente", "Observacao"]);
        var selected = plots.Where(p => p.Notes.Count > 0).OrderByDescending(p => double.IsFinite(p.Volume) ? p.Volume : double.MaxValue).Take(o.SourcePlotLimit).ToArray();
        if (source != null && selected.Length > 0) ScanSource(results, cfg, source, selected, trees, o, direct, cancellation, progress);
        var output = new DataTable(["Talhão", "Parcela", "Lote", "Base_Origem", "Tipo_Inventario", "Parcela_ID", "Idade_anos", "Volume_m3_ha", "Fustes_ha", "VMI_m3_fuste",
            "Volume_recomposto_indicadores", "Grupo_referencia", "N_grupo", "Mediana_volume_grupo", "Limite_IQR_grupo", "Diferenca_mediana_pct", "Densidade_vs_mediana_pct", "VMI_vs_mediana_pct",
            "Conferencia_origem", "Linhas_origem", "Arvores_unicas", "Duplicadas_origem", "Duplicadas_conflitantes", "Fustes_unicos", "Linhas_sem_DAP_positivo", "Area_corr_m2", "Area_min_m2", "Area_max_m2",
            "Variacoes_dimensoes", "H_ausente_fustes", "H_acima_limiar", "DAP_acima_limiar", "H_max_m", "DAP_max_cm", "H_media_origem_m", "G_m2_ha", "Volume_conhecido_origem_ha",
            "Diferenca_origem_pct", "Densidade_origem_ha", "Top5_volume_conhecido_pct", "AB_reportada_m2", "AB_origem_m2", "Sinalizada", "Diagnostico", "Revisao"]);
        foreach (var p in plots.OrderByDescending(p => p.Notes.Count > 0).ThenByDescending(p => p.Volume))
        {
            cancellation.ThrowIfCancellationRequested(); var s = p.Source;
            double sourceHa = s is { StandardEquation: true } && s.Area > 0 ? s.Volume * 10000 / s.Area : double.NaN;
            string coverage = source == null ? "Não solicitada" : s == null ? "Fora da seleção para origem" : s.Unique == 0 ? "Sem correspondência de Parcela_ID" :
                s.MissingHeight > 0 || !s.StandardEquation || s.InvalidArea > 0 ? "Parcial: faltam dados ou modelo não recomposto" : "Recomposta com H da origem e configuração selecionada";
            if (s != null)
            {
                if (s.Unique == 0) p.Notes.Add("Origem e resultado não correspondem pelas chaves configuradas");
                if (s.Duplicates > 0) p.Notes.Add($"Origem contém {s.Duplicates} repetição(ões); auditoria mantém a primeira por chave");
                if (s.Conflicts > 0) p.Notes.Add($"{s.Conflicts} repetição(ões) com DAP/H/dimensões diferentes");
                if (s.Dimensions.Count > 1) p.Notes.Add("Dimensões/declinações variam dentro da parcela");
                if (s.InvalidArea > 0 || s.Negative > 0) p.Notes.Add("Medidas inválidas na origem");
                if (s.HighHeight > 0) p.Notes.Add($"{s.HighHeight} altura(s) da origem acima de {F(o.HeightAlert)} m");
                if (s.HighDap > 0) p.Notes.Add($"{s.HighDap} DAP(s) acima de {F(o.DapAlert)} cm");
                if (s.Unique > 0 && results.Column("AB") >= 0 && Differs(results.Number(p.Row,results.Column("AB")),s.BasalArea))
                    p.Notes.Add("AB reportada difere da soma de AS em m² na origem: conferir unidade de DAP e versão do processamento");
                if (s.MissingHeight > 0) p.Notes.Add("H ausente na origem: conferir H estimada na versão processada");
                if (s.Unique > 0 && s.InvalidArea == 0 && Differs(p.Density, s.Stems * 10000 / s.Area)) p.Notes.Add("Densidade não recomposta com árvores únicas e área da origem: conferir repetição, chaves e versão");
                if (s.Unique > 0 && s.MissingHeight == 0 && s.StandardEquation && s.InvalidArea == 0 && Differs(p.Volume, sourceHa))
                    p.Notes.Add("Volume não recomposto pela configuração selecionada: conferir equação, fator, versão e altura final");
            }
            string review = s == null ? "Conferir origem para avaliar DAP, H, área, contagem e equação" : s.Unique == 0 ? "Selecionar a origem/configuração correspondente ao processamento" :
                "Conferir campo e cadastro; validar H, DAP, área, equação/faixa de aplicação e fatores. Alerta não comprova erro";
            output.Add(results.Text(p.Row, Col("Talhão")), results.Text(p.Row, plotColumn), p.Lot, results.Text(p.Row, Col("Base_Origem")), results.Text(p.Row, Col("Tipo_Inventario")), p.Id,
                p.Age, p.Volume, p.Density, p.Vmi, p.Density * p.Vmi, p.PeerKey, (double)p.Peers, p.PeerMedian, p.PeerLimit, Relative(p.Volume, p.PeerMedian), Relative(p.Density,p.PeerDensity), Relative(p.Vmi,p.PeerVmi),
                coverage, s == null ? null : (double)s.Rows, s == null ? null : (double)s.Unique, s == null ? null : (double)s.Duplicates, s == null ? null : (double)s.Conflicts,
                s == null ? null : (double)s.Stems, s == null ? null : (double)(s.Unique-s.Stems), s?.Area, s?.MinArea, s?.MaxArea, s == null ? null : (double)s.Dimensions.Count, s == null ? null : (double)s.MissingHeight,
                s == null ? null : (double)s.HighHeight, s == null ? null : (double)s.HighDap, s?.MaxHeight, s?.MaxDap, s != null && s.Stems > s.MissingHeight ? s.HeightSum / (s.Stems - s.MissingHeight) : null,
                s != null && s.Area > 0 ? s.BasalArea * 10000 / s.Area : null, sourceHa, Relative(sourceHa,p.Volume), s != null && s.Area > 0 ? s.Stems * 10000 / s.Area : null,
                s is { Volume: > 0 } ? s.Contributions.OrderDescending().Take(5).Sum() / s.Volume * 100 : null,
                results.Number(p.Row,Col("AB")), s?.BasalArea, p.Notes.Count == 0 ? "Não" : "Sim", p.Notes.Count == 0 ? "Sem alerta nos critérios aplicados" : string.Join("; ", p.Notes), review);
        }
        var info = new DataTable(["Item", "Resultado"]);
        info.Add("Auditoria", "Pós-processamento de inventário — triagem e rastreabilidade, sem alterar observações");
        info.Add("Executada em", DateTimeOffset.Now.ToString("O"));
        info.Add("Parcelas avaliadas", (double)plots.Count); info.Add("Parcelas com alerta", (double)plots.Count(p => p.Notes.Count > 0));
        info.Add("Origem", source?.Path ?? "Não solicitada"); info.Add("Tabela de origem", source?.Table ?? "");
        info.Add("Parcelas selecionadas para conferir origem", source == null ? 0d : (double)selected.Length);
        info.Add("Parcelas sem correspondência na origem", (double)selected.Count(p => p.Source is { Unique: 0 }));
        info.Add("Limite de conferência na origem", (double)o.SourcePlotLimit);
        info.Add("Critérios", JsonSerializer.Serialize(o with { PeerColumns = peerNames }));
        info.Add("Referências", "Mesmo grupo de colunas selecionadas e faixa de idade. Grupo inclui a própria parcela; IQR exige mínimo configurado. Grupo pequeno fica sem limiar estatístico.");
        info.Add("Unidades", "Diagnóstico e evidências: DAP em cm, H em m, área/AS em m², G em m²/ha, volume em m³/ha. Conversões independentes para auditoria; H não é reajustada.");
        info.Add("Recomposição", "Origem é deduplicada por chave configurada. H positiva da origem é usada; H ausente impede conclusão exata. Não executa RNA nem aplica fatores específicos de revisões. Recalcula modelos padrão VTCC, usando idade do resultado.");
        info.Add("Configuração usada", new JsonObject {
            ["identificacao"]=cfg["identificacao"]?.DeepClone(), ["arquivo_entrada"]=cfg["arquivo_entrada"]?.DeepClone(),
            ["volume"]=cfg["volume"]?.DeepClone(), ["processador"]=new JsonObject { ["modo"]=cfg.At("processador").S("modo"), ["remover_duplicadas"]=cfg.At("processador").B("remover_duplicadas") }
        }.ToJsonString());
        info.Add("Limites", "Limiares são triagem editável, não limites biológicos. Configuração e origem atuais podem diferir da versão processada. Reproduzir aritmética não valida campo, área, equação ou domínio de calibração.");
        info.Add("Comparações temporais", (double)(evolution.Count-1));
        info.Add("Vínculo temporal", string.Join("; ",o.TemporalColumns ?? ["Talhão","Parcela","Lote","Tipo_Inventario"]));
        info.Add("Evolução", "Cruza a identidade física escolhida, separando IFC/IFPC por padrão. Fonte não entra por padrão porque o nome pode mudar a cada ano. Nomes de parcela reutilizados em bases independentes exigem incluir outra coluna de identidade física. Sem data, usa diferenças de idade; empate de data/idade não gera comparação arbitrária. Queda pode refletir mortalidade, intervenção, área, cadastro, modelo ou dados.");
        progress?.Invoke(100,"Auditoria concluída."); return new(output, trees, info,evolution);
    }

    public static DataTable Evolution(DataTable results, InventoryAuditOptions options, CancellationToken cancellation = default)
    {
        int Col(params string[] names)=>names.Select(results.Column).FirstOrDefault(c=>c>=0,-1);
        int[] keys=(options.TemporalColumns ?? ["Talhão","Parcela","Lote","Tipo_Inventario"]).Select(results.Require)
            .Concat(results.Column("Tipo_Inventario")>=0 ? new[]{results.Require("Tipo_Inventario")} : []).Distinct().ToArray();
        if(keys.Length==0)throw new InvalidDataException("Selecione as colunas da identidade física para a evolução temporal.");
        int date=Col("Data_Medicao"), age=Col("Idade_anos"), volume=Col("Volume_m3_ha","VTCC_ha"), stems=Col("Fustes_ha"), vmi=Col("VMI_m3_fuste","VMI"), area=Col("area_corr"), origin=Col("Base_Origem");
        double Time(int row)=>date>=0 ? InventoryMath.Date(results.Text(row,date))/365.25 : Math.Round(results.Number(row,age)*365.25)/365.25;
        var output=new DataTable(["Identidade_fisica","Talhão","Parcela","Tipo_Inventario","Fonte_anterior","Fonte_atual","Parcela_ID_anterior","Parcela_ID_atual",
            "Data_anterior","Data_atual","Idade_anterior","Idade_atual","Intervalo_anos","Volume_anterior_ha","Volume_atual_ha","Variacao_m3_ha","Variacao_pct","Incremento_m3_ha_ano",
            "Fustes_anterior_ha","Fustes_atual_ha","VMI_anterior","VMI_atual","Variacao_fustes_pct","Variacao_VMI_pct","Area_anterior_m2","Area_atual_m2","Situacao","Observacao"]);
        foreach(var group in Enumerable.Range(1,results.Count-1).GroupBy(r=>JsonSerializer.Serialize(keys.Select(c=>results.Text(r,c)))))
        {
            cancellation.ThrowIfCancellationRequested();
            var times=group.Where(r=>double.IsFinite(Time(r))).GroupBy(Time).OrderBy(g=>g.Key).ToArray();
            int? previous=null;
            foreach(var instant in times)
            {
                if(instant.Count()>1)
                {
                    int row=instant.First();
                    output.Add(group.Key,results.Text(row,Col("Talhão")),results.Text(row,Col("Parcela")),results.Text(row,Col("Tipo_Inventario")),"","","","","","",null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,"Ambígua","Múltiplos registros da mesma identidade e data/idade. Revise as colunas de vínculo; este instante interrompe a sequência.");
                    previous=null; continue;
                }
                int current=instant.Single();
                if(previous is int before)
                {
                    double years=Time(current)-Time(before), v0=results.Number(before,volume),v1=results.Number(current,volume),
                        n0=results.Number(before,stems),n1=results.Number(current,stems),u0=results.Number(before,vmi),u1=results.Number(current,vmi),
                        a0=results.Number(before,area),a1=results.Number(current,area),delta=v1-v0,pct=Relative(v1,v0);
                    string state=!double.IsFinite(v0)||!double.IsFinite(v1)||v0<0||v1<0 ? "Incompleta" : delta<0 && (v0==0 || pct < -options.DeclineTolerancePercent) ? "Diminuiu — revisar" : delta<0 ? "Diminuiu dentro da tolerância" : delta>0 ? "Cresceu" : "Estável";
                    var notes=new List<string>{date<0?"Intervalo calculado pelas idades; Data_Medicao ausente":"Intervalo entre datas de medição"};
                    if(years<30/365.25)notes.Add("Intervalo menor que 30 dias: verificar medição repartida entre datas e remedição");
                    if(Differs(a0,a1))notes.Add("Área corrigida mudou");
                    if(n1<n0)notes.Add("Densidade diminuiu: verificar mortalidade, desbaste, área e contagem");
                    if(u1<u0)notes.Add("VMI diminuiu: verificar população remanescente, DAP, altura, equação e fatores");
                    notes.Add("Confirmar mesma parcela física, intervenções, cadastro e modelos antes de interpretar crescimento");
                    output.Add(group.Key,results.Text(current,Col("Talhão")),results.Text(current,Col("Parcela")),results.Text(current,Col("Tipo_Inventario")),
                        results.Text(before,origin),results.Text(current,origin),results.Text(before,Col("Parcela_ID")),results.Text(current,Col("Parcela_ID")),
                        results.Text(before,date),results.Text(current,date),results.Number(before,age),results.Number(current,age),years,v0,v1,delta,pct,
                        double.IsFinite(delta)&&years>0?delta/years:null,n0,n1,u0,u1,Relative(n1,n0),Relative(u1,u0),a0,a1,state,string.Join("; ",notes));
                }
                previous=current;
            }
        }
        return output;
    }

    private static void ScanSource(DataTable results, JsonObject cfg, TableSource source, Plot[] selected, DataTable trees,
        InventoryAuditOptions o, bool direct, CancellationToken cancellation, Action<double,string>? progress)
    {
        if (selected.Any(p => string.IsNullOrEmpty(p.Id)) || selected.Select(p=>p.Id).Distinct().Count()!=selected.Length)
            throw new InvalidDataException("Parcela_ID ausente ou repetido no resultado; selecione uma única versão de parcelas.");
        var byId = selected.ToDictionary(p => p.Id, StringComparer.Ordinal); foreach(var p in selected) p.Source = new();
        using var cursor = new TableCursor(source);
        var one = new DataTable(cursor.Columns); one.Add(new object?[cursor.Columns.Length]);
        var identities = new InventoryIdentity(one,cfg,true);
        var columns = cfg.At("arquivo_entrada.colunas");
        int C(string key,string fallback) => one.Require(columns.S(key,fallback));
        int dapCol=C("dap","DAP"), hCol=C("altura","H"), lengthCol=C("comprimento_parcela","Compr"), widthCol=C("largura_parcela","Larg"),
            dlCol=C("declinacao_comprimento","Dec_Compr"), dwCol=C("declinacao_largura","Dec_Larg");
        double dapToCm = cfg.At("arquivo_entrada.unidades").S("dap","mm") == "cm" ? 1 : .1;
        double heightToM = cfg.At("arquivo_entrada.unidades").S("altura","dm") switch { "m"=>1, "cm"=>.01, _=>.1 };
        double factor = direct ? cfg.At("volume").N("fator",1) : 1;
        var models = cfg.At("volume.equacoes")!.AsArray().Where(e => e.S("tipo_volume","VTCC")=="VTCC").ToDictionary(e=>e.S("codigo"));
        int row=0;
        foreach(var values in cursor.Rows())
        {
            row++; if ((row & 8191)==0) { cancellation.ThrowIfCancellationRequested(); progress?.Invoke(10 + 80d*row/Math.Max(1,cursor.TotalRows ?? row),$"Conferindo origem: {row:N0} linhas…"); }
            one.Rows[1]=values.ToList();
            if (!byId.TryGetValue(identities.PlotKey(1), out var p)) continue;
            var s=p.Source!; s.Rows++;
            string tree = identities.TreeKey(1), fingerprint = JsonSerializer.Serialize(new[]{ dapCol,hCol,lengthCol,widthCol,dlCol,dwCol }.Select(c=>one.Text(1,c)));
            if (s.Keys.TryGetValue(tree,out string? previous)) { s.Duplicates++; if(previous!=fingerprint)s.Conflicts++; continue; }
            s.Keys.Add(tree,fingerprint);
            double dap = one.Number(1,dapCol)*dapToCm, height=one.Number(1,hCol)*heightToM;
            double Slope(int col,string setting) => string.IsNullOrWhiteSpace(one.Text(1,col)) && cfg.At("arquivo_entrada.declinacoes").B(setting) ? 0 : one.Number(1,col);
            double dl=Slope(dlCol,"comprimento_vazio_zero"), dw=Slope(dwCol,"largura_vazio_zero"), length=one.Number(1,lengthCol), width=one.Number(1,widthCol);
            double area = length>0 && width>0 && double.IsFinite(dl) && double.IsFinite(dw) && Math.Abs(dl)<90 && Math.Abs(dw)<90 ? InventoryMath.Area(length,width,dl,dw) : double.NaN;
            s.Dimensions.Add(JsonSerializer.Serialize(new[]{lengthCol,widthCol,dlCol,dwCol}.Select(c=>one.Text(1,c))));
            if (double.IsFinite(area) && area>0) { s.AreaSum+=area; s.MinArea=Math.Min(s.MinArea,area); s.MaxArea=Math.Max(s.MaxArea,area); } else s.InvalidArea++;
            if (!double.IsFinite(dap) || dap<0 || !double.IsFinite(height) || height<0) s.Negative++;
            bool stem=double.IsFinite(dap) && dap>0;
            if(stem) { s.Stems++; s.MaxDap=Math.Max(s.MaxDap,dap); if(dap>o.DapAlert)s.HighDap++; s.BasalArea+=Math.PI*dap*dap/40000; }
            if(stem && !(double.IsFinite(height) && height>0)) s.MissingHeight++;
            if(stem && double.IsFinite(height) && height>0) { s.HeightSum+=height; s.MaxHeight=Math.Max(s.MaxHeight,height); if(height>o.HeightAlert)s.HighHeight++; if(Math.Abs(one.Number(1,hCol)-Math.Round(one.Number(1,hCol)))>1e-7)s.FractionalHeight++; }
            string code = InventoryMath.EquationCode(p.Lot,p.Age,cfg.At("volume").N("idade_limite_equacao_geral",15));
            double volume=double.NaN, basal=stem ? Math.PI*dap*dap/40000 : double.NaN;
            if (models.TryGetValue(code,out var equation) && equation!=null && equation["entradas"]==null && equation.S("formula").Length==0 && double.IsFinite(p.Age))
            {
                volume=InventoryMath.Volume(dap*10,height*10,equation.N("b0"),equation.N("b1"),equation.N("b2"))*factor;
                if (double.IsFinite(volume)) { s.Volume+=volume; s.Contributions.Add(volume); }
            } else s.StandardEquation=false;
            string note = !stem ? "Sem DAP positivo" : !(height>0) ? "H ausente; depende da altura final processada" : "H já preenchida na origem; validar procedência e medição";
            trees.Add(p.Id,results.Text(p.Row,results.Column("Talhão")),results.Text(p.Row,results.Column("Parcela")),identities.Context(1).Origin,identities.Context(1).Type,
                (double)(row+source.HeaderRow),tree,one.Text(1,C("data_medicao","Data")),
                one.Text(1,one.Column(columns.S("cova","Cova"))),one.Text(1,one.Column(columns.S("fuste","Fuste"))),one.Text(1,one.Column(columns.S("fila","Fila"))),
                dap,height,area,basal,code,volume,basal>0 && height>0 ? volume/(basal*height) : null,note);
        }
    }
}
