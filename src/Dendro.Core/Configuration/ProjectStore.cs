using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Configuration;

public static class ProjectStore
{
    private static JsonNode Resource(string name)
    {
        using var stream = typeof(ProjectStore).Assembly.GetManifestResourceStream(
            $"Dendro.Core.Configuration.{name}.json"
        )!;
        return JsonNode.Parse(stream)!;
    }

    public static JsonArray Fields => Resource("processing-fields").AsArray();
    public static JsonObject HeightDefaults => Resource("height-defaults").AsObject();
    public static void EnsureRegistryFallback(JsonObject p)
    {
        var settings = p.Obj("cadastro").Obj("fallback_idade");
        MergeDefaults(settings, Resource("registry-fallback-defaults").AsObject());
        if (settings.S("padrao_ano") == Processing.RegistryAgeFallback.LegacyDefaultPattern)
            settings["padrao_ano"] = Processing.RegistryAgeFallback.DefaultPattern;
    }

    public static void EnsureInputUnits(JsonObject p)
    {
        Processing.InventoryIdentity.Ensure(p);
        MergeDefaults(p.Obj("arquivo_entrada").Obj("declinacoes"), new JsonObject { ["comprimento_vazio_zero"] = false, ["largura_vazio_zero"] = false });
        MergeDefaults(p.Obj("arquivo_entrada").Obj("unidades"), new JsonObject { ["dap"] = "mm", ["altura"] = "dm" });
        MergeDefaults(p.Obj("arquivo_entrada").Obj("colunas"), new JsonObject { ["cova"] = "Cova", ["fuste"] = "Fuste", ["fila"] = "Fila" });
    }

    public static JsonObject Create(string name)
    {
        var p = Resource("project-defaults").AsObject();
        p.Obj("_dendro")["name"] = name;
        p["ajuste_altura"] = HeightDefaults;
        EnsureInputUnits(p);
        RegressionCatalog.Ensure(p);
        EnsureRegistryFallback(p);
        MergeDefaults(p.Obj("processador"), Processing.DirectInventorySummary.Defaults());
        foreach (var f in Fields)
            if (p.At(f.S("path")) is null)
                p.Set(f.S("path"), f!["default"]?.DeepClone());
        return p;
    }

    public static JsonObject Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var p =
            JsonNode.Parse(stream)?.AsObject()
            ?? throw new InvalidDataException("Projeto inválido.");
        RegressionCatalog.Ensure(p);
        EnsureRegistryFallback(p);
        MergeDefaults(p.Obj("processador"), Processing.DirectInventorySummary.Defaults());
        Validate(p, true);
        ProjectSnapshots.Compact(p);
        return p;
    }

    public static void Save(string path, JsonObject p)
    {
        path = Path.GetFullPath(path);
        using var lease = SharedFileLock.Acquire(path + ".write.lock");
        Validate(p, true);
        ProjectSnapshots.Compact(p);
        long revision = 0;
        if (File.Exists(path))
        {
            using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = JsonDocument.Parse(current);
            if (document.RootElement.TryGetProperty("_dendro", out var meta) && meta.TryGetProperty("store_revision", out var stored)) revision = stored.GetInt64();
            if ((long)p["_dendro"].N("store_revision") != revision)
                throw new InvalidOperationException("O projeto foi salvo por outro usuário. Reabra a versão atual antes de gravar; suas alterações locais não foram sobrescritas.");
        }
        p.Obj("_dendro")["store_revision"] = revision + 1;
        try
        {
            AtomicWrite(
                path,
                temporary => {
                    using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                    p.WriteTo(writer); writer.Flush(); stream.Flush(true);
                },
                true
            );
        }
        catch { p.Obj("_dendro")["store_revision"] = revision; throw; }
    }

    public static void AtomicWrite(string path, Action<string> write, bool replace = false)
    {
        path = Path.GetFullPath(path);
        string temp = Path.Combine(
            Path.GetDirectoryName(path)!,
            ".dendro-" + Guid.NewGuid().ToString("N") + ".tmp"
        );
        try
        {
            write(temp);
            File.Move(temp, path, replace);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void MergeDefaults(JsonObject target, JsonObject defaults)
    {
        foreach (var pair in defaults)
        {
            if (!target.ContainsKey(pair.Key))
                target[pair.Key] = pair.Value?.DeepClone();
            else if (target[pair.Key] is JsonObject child && pair.Value is JsonObject fallback)
                MergeDefaults(child, fallback);
        }
    }

    public static void Validate(JsonObject p, bool project = false)
    {
        EnsureInputUnits(p);
        Processing.InventoryIdentity.Validate(p);
        foreach (string key in new[] { "comprimento_vazio_zero", "largura_vazio_zero" })
            if (p.Obj("arquivo_entrada").Obj("declinacoes")[key] is not JsonValue flag || !flag.TryGetValue<bool>(out _))
                throw new InvalidDataException("As opções de declinação vazia devem ser marcadas ou desmarcadas.");
        var units = p.At("arquivo_entrada.unidades");
        if (units.S("dap") is not ("mm" or "cm"))
            throw new InvalidDataException("A unidade de importação do DAP deve ser mm ou cm.");
        if (units.S("altura") is not ("dm" or "cm" or "m"))
            throw new InvalidDataException("A unidade de importação da altura deve ser dm, cm ou m.");
        Processing.RegistryAgeFallback.Validate(p["cadastro"]?["fallback_idade"]);
        Processing.DirectInventorySummary.Validate(p["processador"]);
        RegressionCatalog.Validate(p);
        if (
            project
            && (
                p["_dendro"]?.S("format") != "DENDRO_PROJECT"
                || p["_dendro"].N("version") != 1
                || p["_dendro"]?["files"] is not JsonArray
            )
        )
            throw new InvalidDataException(
                "O arquivo não é um projeto DENDRO compatível (versão 1)."
            );
        foreach (var field in Fields)
        {
            string path = field.S("path");
            var v = p.At(path);
            if (v is null)
                continue;
            bool valid = (int)field.N("type") switch
            {
                0 => v.GetValueKind() == JsonValueKind.String
                    && (path == "saida.sufixo_arquivo" || v.GetValue<string>().Length > 0),
                1 => v.GetValueKind() == JsonValueKind.Number
                    && double.IsFinite(JsonExtensions.Number(v))
                    && JsonExtensions.Number(v) >= 0,
                2 => v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
                _ => false,
            };
            if (!valid)
                throw new InvalidDataException($"Parâmetro inválido: {path}.");
        }
        double confidence = p["estatisticas"].N("nivel_confianca", .95),
            minimum = p["estatisticas"].N("min_parcelas_estatistica", 3);
        if (
            confidence <= 0
            || confidence >= 1
            || minimum < 2
            || minimum > 1000000
            || Math.Floor(minimum) != minimum
        )
            throw new InvalidDataException("Nível de confiança ou mínimo de parcelas inválido.");
        if (
            p.At("volume.fator") is { } factor
            && (
                factor.GetValueKind() != JsonValueKind.Number
                || !double.IsFinite(JsonExtensions.Number(factor))
                || JsonExtensions.Number(factor) < 0
                || JsonExtensions.Number(factor) > 1
            )
        )
            throw new InvalidDataException(
                "Fator volumétrico inválido: informe um número de 0 a 1."
            );
        string suffix = p["saida"].S("sufixo_arquivo");
        if (suffix.Length > 120 || suffix.IndexOfAny("<>:\"/\\|?*\r\n\t".ToCharArray()) >= 0)
            throw new InvalidDataException("O sufixo não pode conter caracteres de caminho.");
        p.Obj("comparativo_ifc")["ativo"] = true;
        if (
            p.At("comparativo_ifc.limiares") is { } limits
            && limits.N("verde", 5) > limits.N("amarelo", 15)
        )
            throw new InvalidDataException("O limite verde não pode superar o amarelo.");
        if (p["volume"]?["equacoes"] is not JsonArray equations || equations.Count > 10000)
            throw new InvalidDataException("Lista de modelos volumétricos inválida.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in equations)
        {
            string volumeType = e.S("tipo_volume", "VTCC");
            if (volumeType is not ("VTCC" or "VCCC")) throw new InvalidDataException("Tipo de volume inválido. Use VTCC ou VCCC.");
            e!["tipo_volume"] = volumeType;
            ValidateCoefficients(e!);
            ModelDefinition.Validate(e!, false);
            if (!ids.Add(e.S("codigo") + "\0" + volumeType))
                throw new InvalidDataException("Modelo volumétrico duplicado.");
        }
        if (p["curvas_crescimento"]?["equacoes"] is JsonArray curves)
        {
            ids.Clear();
            foreach (var c in curves)
            {
                ValidateCoefficients(c!);
                ModelDefinition.Validate(c!, true);
                int year = (int)c.N("ano");
                if (c.N("ano") != year || (year != 0 && (year < 1800 || year > 9999)))
                    throw new InvalidDataException("Ano da curva inválido.");
                if (c.B("vigente", true) && !ids.Add(c.S("codigo")))
                    throw new InvalidDataException("Há mais de uma curva vigente para o mesmo ID.");
            }
        }
        if (p["ajuste_altura"] is JsonObject height)
            ValidateHeight(height);
        if (p["_dendro"]?["file_options"] is JsonArray options)
            foreach (var o in options)
                if (
                    o.S("path").Length == 0
                    || o.S("sheet").Length == 0
                    || o.N("header_row") < 1
                    || o.N("header_row") > 1048576
                    || Math.Floor(o.N("header_row")) != o.N("header_row")
                )
                    throw new InvalidDataException("Worksheet ou cabeçalho inválido.");
        ValidateCharts(p);
        if (p["curvas_crescimento"]?["modelos"] is JsonArray definitions)
        {
            if (definitions.Count > 1000)
                throw new InvalidDataException("Limite de definições de modelos excedido.");
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in definitions)
            {
                string id = d.S("id");
                if (
                    id.Length is < 1 or > 63
                    || d.S("nome").Length == 0
                    || ModelDefinition.Growth.Any(g => g.Id == id)
                    || !defined.Add(id)
                )
                    throw new InvalidDataException(
                        "Definição de modelo personalizada inválida ou duplicada."
                    );
                ModelDefinition.Validate(d!, true);
            }
        }
    }

    public static void ValidateCoefficients(JsonNode e)
    {
        if (
            string.IsNullOrEmpty(e.S("codigo"))
            || Encoding.UTF8.GetByteCount(e.S("codigo")) > 31
            || new[] { "b0", "b1", "b2" }.Any(k =>
                e[k]?.GetValueKind() != JsonValueKind.Number || !double.IsFinite(e.N(k, double.NaN))
            )
        )
            throw new InvalidDataException(
                "Informe ID de até 31 bytes e coeficientes numéricos finitos."
            );
    }

    public static void ValidateHeight(JsonObject height)
    {
        if (!height.ContainsKey("h_mode") && height.B("ativo"))
            height["h_mode"] = "CALIBRATE";
        MergeDefaults(height, HeightDefaults);
        string mode = height.S("h_mode");
        if (mode is not ("KEEP_FILE" or "MODEL_GENERAL" or "FINETUNE_MODEL" or "CALIBRATE"))
            throw new InvalidDataException("Modo de altura desconhecido.");
        if (
            mode is "MODEL_GENERAL" or "FINETUNE_MODEL"
            && height["modelo_geral"].S("caminho").Length == 0
        )
            throw new InvalidDataException("Selecione o ZIP do modelo geral.");
        foreach (var key in new[] { "debug", "por_lote", "fallback_rna_geral" })
            if (height[key]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Opção inválida: {key}.");
        foreach (string section in new[] { "finetune", "rede_neural" })
        {
            var v = height[section]!;
            foreach (string k in new[] { "epochs", "batch_size", "patience" })
                Range(v, k, k == "patience" ? 0 : 1, 1000000, true);
            Range(v, "learning_rate", 1e-12, 1, false);
            if (section == "finetune")
            {
                foreach (
                    string k in new[]
                    {
                        "min_amostras_bias",
                        "min_amostras_linear",
                        "min_amostras_finetune",
                    }
                )
                    Range(v, k, 1, 1000000, true);
                Range(v, "ridge_alpha", 0, 1e12, false);
            }
            else
            {
                Range(v, "n_splits", 2, 1000, true);
                Range(v, "min_amostras_treino", 1, 1000000, true);
            }
        }
        if (
            height["features"] is not JsonArray features
            || features.Any(f =>
                f?.GetValueKind() != JsonValueKind.String || f.GetValue<string>().Length == 0
            )
        )
            throw new InvalidDataException("Features devem ser nomes válidos.");
        if (
            height["rede_neural"]?["camadas"] is not JsonArray layers
            || layers.Count is < 1 or > 32
            || layers.Any(n =>
                JsonExtensions.Number(n) < 1
                || JsonExtensions.Number(n) > 1000000
                || Math.Floor(JsonExtensions.Number(n)) != JsonExtensions.Number(n)
            )
        )
            throw new InvalidDataException("Camadas inválidas.");
        foreach (string key in new[] { "variaveis_customizadas", "estatisticas_parcela" })
            if (
                height[key] is not JsonArray a
                || a.Any(e => e is not JsonObject || e.S("nome").Length == 0)
            )
                throw new InvalidDataException($"Definição inválida: {key}.");
        height["ativo"] = mode != "KEEP_FILE";
    }

    private static void Range(JsonNode v, string key, double min, double max, bool integer)
    {
        double n = v.N(key, double.NaN);
        if (!double.IsFinite(n) || n < min || n > max || (integer && Math.Floor(n) != n))
            throw new InvalidDataException($"Valor inválido para {key}.");
    }

    public static JsonNode? ExcelOptions(JsonObject p, string path) =>
        (p["_dendro"]?["file_options"] as JsonArray)?.FirstOrDefault(o =>
            string.Equals(o.S("path"), path, StringComparison.OrdinalIgnoreCase)
        );

    private static void ValidateCharts(JsonObject p)
    {
        var charts = p["_dendro"]?["charts"];
        if (charts is null)
            return;
        if (charts is not JsonArray a || a.Count > 10000)
            throw new InvalidDataException("Lista de gráficos inválida.");
        foreach (var chart in a)
        {
            foreach (
                string key in new[]
                {
                    "path",
                    "x",
                    "y",
                    "title",
                    "x_label",
                    "y_label",
                    "sheet",
                    "stratum",
                    "group",
                    "formula",
                }
            )
                if (
                    chart?[key] is { } value
                    && (
                        value.GetValueKind() != JsonValueKind.String
                        || value.GetValue<string>().Length
                            > (
                                key == "path" ? 131000
                                : key == "formula" ? 4096
                                : 1000
                            )
                    )
                )
                    throw new InvalidDataException("Parâmetro do gráfico inválido.");
            if (
                chart.S("path").Length == 0
                || chart.S("x").Length == 0
                || chart.S("y").Length == 0
                || chart.N("header_row", 1) < 1
                || chart.N("header_row", 1) > 1048576
                || chart.N("point_size", 3) < 1
                || chart.N("point_size", 3) > 12
            )
                throw new InvalidDataException("Configuração de gráfico inválida.");
            if (
                chart?["excluded"] is JsonArray removed
                && removed.Any(n =>
                    n?.GetValueKind() != JsonValueKind.Number
                    || JsonExtensions.Number(n) < 1
                    || Math.Floor(JsonExtensions.Number(n)) != JsonExtensions.Number(n)
                )
            )
                throw new InvalidDataException("Índices de remoção inválidos.");
            foreach (string key in new[] { "xmin", "xmax", "ymin", "ymax" })
                if (
                    chart?[key] is { } value
                    && (
                        value.GetValueKind() != JsonValueKind.Number
                        || !double.IsFinite(JsonExtensions.Number(value))
                    )
                )
                    throw new InvalidDataException("Limite de eixo inválido.");
            foreach (string axis in new[] { "x", "y" })
                if (
                    chart?[axis + "min"] is not null
                    && chart[axis + "max"] is not null
                    && chart.N(axis + "min") >= chart.N(axis + "max")
                )
                    throw new InvalidDataException("O mínimo do eixo deve ser menor que o máximo.");
        }
    }
}
