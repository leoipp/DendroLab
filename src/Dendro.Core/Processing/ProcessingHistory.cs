using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

public static class ProcessingHistory
{
    public const string InternalPrefix = "__DENDRO_";
    public const string Trees = "RESULTADO - ARVORES", Plots = "RESULTADO - PARCELAS";
    public static JsonArray DefaultExportSheets(JsonObject config, IEnumerable<string> sheets)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal) {
            [Trees] = "resultado_arvore", [Plots] = "resumo_byplot", ["RESUMO - BYLOTE"] = "resumo_bylote",
            ["AJUSTADO"] = "ajustado", ["H Ajuste"] = "h_ajuste", ["RESUMO - BYPLOT"] = "resumo_byplot",
            ["RESUMO - BYTALHAO"] = "resumo_bytalhao", ["ESTATISTICAS INV"] = "estatisticas_inv",
            ["Graficos"] = "graficos", ["MODELOS APLICADOS"] = "modelos_aplicados", ["INFORMACOES"] = "informacoes", ["CALCULOS"] = "calculos",
        };
        return new JsonArray(sheets.Where(sheet => !keys.TryGetValue(sheet, out string? key) || config.At("saida.sheets").B(key, true))
            .Select(sheet => (JsonNode?)JsonValue.Create(sheet)).ToArray());
    }
    public static bool Enabled(JsonObject cfg) => cfg.At("processador.execucao").S("id").Length > 0;

    public static JsonObject Freeze(JsonObject cfg)
    {
        var frozen = new JsonObject();
        foreach (var pair in cfg.Where(p => p.Key != "_dendro")) frozen[pair.Key] = pair.Value?.DeepClone();
        frozen["_dendro"] = new JsonObject { ["format"] = "DENDRO_PROJECT", ["version"] = 1,
            ["name"] = cfg["_dendro"].S("name"), ["files"] = new JsonArray(),
            ["file_options"] = cfg["_dendro"]?["file_options"]?.DeepClone() ?? new JsonArray() };
        return frozen;
    }

    public static void Stamp(JsonObject cfg, params DataTable[] tables)
    {
        var run = cfg.At("processador.execucao");
        foreach (var table in tables)
        {
            foreach (var field in new[] { "Processamento_ID", "Inventario_ID", "Versao", "Tipo_Inventario", "Usuario_Processamento" }) table.Ensure(field);
            for (int row = 1; row < table.Count; row++)
            {
                table.Set(row, table.Require("Processamento_ID"), run.S("id")); table.Set(row, table.Require("Inventario_ID"), run.S("family_id"));
                table.Set(row, table.Require("Versao"), run.N("number", 1));
                if (table.Text(row, table.Require("Tipo_Inventario")).Length == 0) table.Set(row, table.Require("Tipo_Inventario"), run.S("inventory_type"));
                table.Set(row, table.Require("Usuario_Processamento"), run.S("user"));
            }
        }
    }

    public static void AddCanonicalSheets(List<WorksheetData> sheets, JsonObject cfg, DataTable input, int original,
        DataTable calc, DataTable plots, DataTable stands)
    {
        if (!Enabled(cfg)) return;
        Stamp(cfg, calc, plots, stands);
        if (!sheets.Any(s => s.Name == Trees)) sheets.Add(new(Trees, calc));
        sheets.Add(new(Plots, plots));
        if (cfg.At("processador.execucao").N("number") == 1)
        {
            var config = new DataTable(["JSON", "Colunas_originais"]); config.Add(Freeze(cfg).ToJsonString(), (double)original);
            sheets.Add(new(InternalPrefix + "CONFIG", config)); sheets.Add(new(InternalPrefix + "INPUT", input)); sheets.Add(new(InternalPrefix + "STANDS", stands));
        }
        var info = new DataTable(["Item", "Valor"]);
        foreach (var pair in cfg.At("processador.execucao")!.AsObject().Where(p => p.Value is JsonValue)) info.Add(pair.Key, pair.Value.Display());
        sheets.Add(new("PROCESSAMENTO", info));
        var factors = new DataTable(["Processamento_Original_ID", "Nivel", "Codigo", "Fator"]);
        foreach (var factor in cfg.At("processador.execucao")!.AsObject().Arr("factors"))
            factors.Add(cfg.At("processador.execucao").S("family_id"), cfg.At("processador.execucao").S("factor_scope"), factor.S("code"), factor.N("factor"));
        if (factors.Count > 1) sheets.Add(new("FATORES APLICADOS", factors));
    }

    private static string Key(string stand, string plot, string date) => new JsonArray(stand, plot, date).ToJsonString();

    public static DataTable FactorTemplate(string baseline, JsonObject original, string scope)
    {
        if (scope is not ("talhao" or "lote")) throw new InvalidDataException("Selecione fatores por talhão ou lote.");
        var data = TableStore.Read(new(baseline, Trees));
        int familyColumn = data.Require("Inventario_ID"), runColumn = data.Require("Processamento_ID");
        if (original.S("id") != original.S("family_id") || original.N("number") != 1 || Enumerable.Range(1, data.Count - 1).Any(r =>
            data.Text(r, familyColumn) != original.S("family_id") || data.Text(r, runColumn) != original.S("id")))
            throw new InvalidDataException("A tabela selecionada não corresponde ao processamento original deste inventário.");
        int key = data.Require(scope == "talhao" ? "Talhão" : "Lote"), volume = data.Require("VTCC_Original");
        var table = new DataTable(["Processamento_Original_ID", "Nivel", "Codigo", "Fator", "Arvores", "VTCC_Original", "Contexto_ID", "Base_Origem", "Tipo_Inventario"]);
        foreach (var group in Enumerable.Range(1, data.Count - 1).GroupBy(r => FactorKey(data, r, data.Text(0, key))).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int first = group.First();
            table.Add(original.S("family_id"), scope, data.Text(first, key), 1d, (double)group.Count(), group.Sum(r => double.IsFinite(data.Number(r, volume)) ? data.Number(r, volume) : 0),
                data.Text(first, data.Column("Contexto_ID")), data.Text(first, data.Column("Base_Origem")), data.Text(first, data.Column("Tipo_Inventario")));
        }
        return table;
    }

    public static string FactorKey(DataTable data, int row, string codeColumn = "Codigo")
    {
        string code = data.Text(row, data.Require(codeColumn)), scope = data.Text(row, data.Column("Contexto_ID"));
        return scope.Length == 0 ? code : new JsonArray(scope, code).ToJsonString();
    }

    public static Dictionary<string, double> ValidateFactors(DataTable factors, DataTable template)
    {
        int id = factors.Require("Processamento_Original_ID"), level = factors.Require("Nivel"), code = factors.Require("Codigo"), value = factors.Require("Fator");
        string expectedId = template.Text(1, 0), expectedScope = template.Text(1, 1);
        var known = Enumerable.Range(1, template.Count - 1).Select(r => FactorKey(template, r)).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int row = 1; row < factors.Count; row++)
        {
            if (factors.Text(row, id) != expectedId || factors.Text(row, level) != expectedScope)
                throw new InvalidDataException($"Linha {row + 1}: os fatores pertencem a outro inventário ou nível.");
            string key = FactorKey(factors, row); double factor = factors.Number(row, value);
            if (!known.Contains(key) || !double.IsFinite(factor) || factor is < 0 or > 1 || !result.TryAdd(key, factor))
                throw new InvalidDataException($"Linha {row + 1}: código desconhecido/duplicado ou fator fora do intervalo 0 a 1.");
        }
        if (result.Count != known.Count) throw new InvalidDataException("A tabela de fatores precisa conter todos os talhões/lotes. Use 1 para manter sem correção.");
        return result;
    }

    public static ProcessingResult Correct(string baseline, JsonObject original, JsonObject revision, DataTable factors, string output,
        CancellationToken cancellation = default, Action<int, string>? progress = null)
    {
        string scope = revision.S("factor_scope");
        if (revision.S("family_id") != original.S("family_id") || revision.N("number") < 2) throw new InvalidDataException("Revisão incompatível com o inventário original.");
        var template = FactorTemplate(baseline, original, scope);
        var mapping = ValidateFactors(factors, template);
        DataTable Read(string name) => TableStore.Read(new(baseline, InternalPrefix + name));
        var stored = Read("CONFIG"); var cfg = JsonNode.Parse(stored.Text(1, 0))!.AsObject();
        cfg.Obj("processador")["execucao"] = revision.DeepClone();
        cfg.Obj("volume")["fator"] = 1;
        var input = Read("INPUT"); var calc = TableStore.Read(new(baseline, Trees)); var plots = TableStore.Read(new(baseline, Plots)); var stands = Read("STANDS");
        int keyColumn = calc.Require(scope == "talhao" ? "Talhão" : "Lote");
        int baseVolume = calc.Require("VTCC_Original"), volume = calc.Require("VTCC"), factorColumn = calc.Require("Fator_Volume");
        int standColumn = calc.Require("Talhão"), plotColumn = calc.Require("Parcela"), dateColumn = calc.Require("Data_Medicao");
        var totals = new Dictionary<string, (double Volume, double Original, int Rows)>(StringComparer.Ordinal);
        for (int row = 1; row < calc.Count; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            double factor = mapping[FactorKey(calc, row, calc.Text(0, keyColumn))], raw = calc.Number(row, baseVolume);
            double corrected = raw * factor;
            calc.Set(row, volume, corrected); calc.Set(row, factorColumn, factor);
            string key = calc.Column("Parcela_ID") >= 0 ? calc.Text(row, calc.Require("Parcela_ID")) : Key(calc.Text(row, standColumn), calc.Text(row, plotColumn), calc.Text(row, dateColumn));
            var previous = totals.GetValueOrDefault(key);
            totals[key] = (previous.Volume + (double.IsFinite(corrected) ? corrected : 0), previous.Original + (double.IsFinite(raw) ? raw : 0), previous.Rows + (double.IsFinite(corrected) ? 1 : 0));
        }
        for (int row = 1; row < plots.Count; row++)
        {
            var total = totals[plots.Column("Parcela_ID") >= 0 ? plots.Text(row, plots.Require("Parcela_ID")) : Key(plots.Text(row, plots.Require("Talhão")), plots.Text(row, plots.Require("Parcela")), plots.Text(row, plots.Require("Data_Medicao")))];
            void Set(string column, double value) => plots.Set(row, plots.Ensure(column), value);
            double perHa = total.Volume * 10000 / plots.Number(row, plots.Require("area_corr"));
            double stemsHa = plots.Number(row, plots.Require("Fustes_ha")), expected = plots.Number(row, plots.Require("Vol_Esperado_ha"));
            Set("VTCC", total.Volume); Set("VTCC_Original", total.Original); Set("VTCC_ha", perHa);
            Set("VMI", stemsHa > 0 ? perHa / stemsHa : double.NaN); Set("VMI_real", total.Rows > 0 ? total.Volume / total.Rows : double.NaN);
            Set("Dif_IFC_pct", expected > 0 ? (perHa - expected) / expected * 100 : double.NaN); Set("IcV", expected > 0 ? perHa / expected : double.NaN);
            Set("Fator_Volume", total.Original > 0 ? total.Volume / total.Original : mapping[FactorKey(plots, row, scope == "talhao" ? "Talhão" : "Lote")]);
        }
        var standGroups = Enumerable.Range(1, plots.Count - 1).GroupBy(p => (plots.Text(p, 0), plots.Text(p, plots.Require("Data_Medicao")), plots.Text(p, plots.Column("Contexto_ID"))))
            .ToDictionary(g => g.Key, g => g.ToArray());
        for (int row = 1; row < stands.Count; row++)
        {
            var group = standGroups[(stands.Text(row, 0), stands.Text(row, stands.Require("Data_Medicao")), stands.Text(row, stands.Column("Contexto_ID")))];
            foreach (string column in new[] { "VTCC_ha", "VMI", "Dif_IFC_pct", "IcV" })
                stands.Set(row, stands.Require(column), Dendro.Core.Mathematics.InventoryMath.Mean(group.Select(p => plots.Number(p, plots.Require(column)))));
        }
        Stamp(cfg, calc, plots, stands);
        var result = new ProcessingResult(calc.Count - 1, plots.Count - 1, stands.Count - 1, totals.Values.Sum(t => t.Volume));
        cancellation.ThrowIfCancellationRequested();
        Configuration.ProjectStore.AtomicWrite(output, temp => InventoryReport.Write(temp, cfg, input, (int)stored.Number(1, 1), calc, plots, stands, result, true, progress, cancellation));
        return result;
    }

    public static void ExportApproved(string project, JsonArray seed, string id, IReadOnlyList<string> sheets, string output,
        CancellationToken cancellation = default)
    {
        JsonObject Current()
        {
            var run = ProcessingCatalog.Read(project, seed).Arr("records").OfType<JsonObject>().SingleOrDefault(r => r.S("id") == id)
                ?? throw new InvalidDataException("Versão não encontrada.");
            if (run.S("status") != "aprovado" || !run.B("current_approved")) throw new InvalidOperationException("Somente a versão aprovada vigente pode ser exportada.");
            return run;
        }
        var snapshot = Current().DeepClone().AsObject();
        var allowed = snapshot.Arr("sheets").Select(s => s.Display()).ToHashSet(StringComparer.Ordinal);
        if (sheets.Count == 0 || sheets.Any(s => s.StartsWith(InternalPrefix, StringComparison.Ordinal) || !allowed.Contains(s)))
            throw new InvalidDataException("Selecione abas válidas da versão aprovada.");
        string source = Path.GetFullPath(snapshot.S("path"), Path.GetDirectoryName(Path.GetFullPath(project))!);
        var tables = new List<WorksheetData>();
        foreach (string sheet in sheets.Distinct(StringComparer.Ordinal).Where(s => s != "PROCESSAMENTO"))
        {
            cancellation.ThrowIfCancellationRequested();
            var data = TableStore.Read(new(source, sheet));
            if (!TableTools.FitsExcel(data.Count - 1, data.Width)) throw new InvalidOperationException("Uma aba excede o limite do Excel. Exporte a tabela em CSV.");
            tables.Add(new(sheet, data));
        }
        var info = new DataTable(["Item", "Valor"]);
        foreach (var pair in snapshot.Where(p => p.Value is JsonValue)) info.Add(pair.Key, pair.Value.Display());
        tables.Add(new("PROCESSAMENTO", info));
        output = Path.GetFullPath(output);
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".dendrolab-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WorkbookWriter.Write(temporary, tables, true);
            cancellation.ThrowIfCancellationRequested();
            using var lease = Configuration.SharedFileLock.Acquire(ProcessingCatalog.FilePath(project) + ".write.lock", cancellation);
            var current = Current();
            if (current.N("revision") != snapshot.N("revision")) throw new InvalidOperationException("A aprovação mudou durante a exportação. Atualize a versão selecionada.");
            File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
