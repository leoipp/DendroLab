using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Configuration;

public sealed record GrowthFamily(string Id, string Name, string Formula);

public static class ModelDefinition
{
    public static readonly GrowthFamily[] Growth =
    [
        new("gompertz", "Gompertz", "b0 * exp(-exp(b1 - b2*t))"),
        new("logistica", "Logística", "b0 / (1 + exp(b1 - b2*t))"),
        new("exponencial_modificado", "Exponencial modificado", "b0 * exp(b1/t)"),
        new("schumacher", "Schumacher", "exp(b0 + b1/t)"),
        new("schumacher_generalizado", "Schumacher generalizado", "exp(b0 + b1/(t^b2))"),
        new("chapman_richards", "Chapman–Richards", "b0 * (1 - exp(-b1*t))^b2"),
        new("weibull", "Weibull", "b0 * (1 - exp(-b1*t^b2))"),
        new("monomolecular", "Monomolecular", "b0 * (1 - exp(-b1*t))"),
        new("exponencial", "Exponencial", "b0 * exp(b1*t)"),
        new("potencial", "Potencial", "b0 * t^b1"),
    ];

    public static string FormulaText(JsonNode v, bool growth) =>
        v.S(
            "formula",
            growth
                ? Growth.FirstOrDefault(g => g.Id == v.S("modelo"))?.Formula ?? ""
                : "exp(b0 + b1*log(x1) + b2*log(x2))"
        );

    public static string Name(JsonNode v, bool growth) =>
        v.S(
            "modelo_nome",
            growth
                ? Growth.FirstOrDefault(g => g.Id == v.S("modelo"))?.Name ?? v.S("modelo")
                : "Schumacher–Hall"
        );

    public static JsonArray Inputs(JsonNode v, bool growth)
    {
        if (v["entradas"] is JsonArray a)
            return a;
        var result = new JsonArray();
        for (int i = 0; i < (growth ? 1 : 2); i++)
        {
            bool years = v.S("unidade_idade") == "anos";
            var x = new JsonObject
            {
                ["simbolo"] =
                    growth ? "t"
                    : i == 0 ? "x1"
                    : "x2",
                ["origem"] = "calculado",
                ["coluna"] =
                    growth ? (years ? "idade_anos" : "idade_meses")
                    : i == 0 ? "dap_cm"
                    : "altura_m",
                ["unidade"] =
                    growth ? (years ? "anos" : "meses")
                    : i == 0 ? "cm"
                    : "m",
                ["fator"] = 1,
            };
            if (growth)
                x["agregacao"] = "media";
            result.Add(x);
        }
        return result;
    }

    public static string Describe(JsonNode input, bool growth) =>
        $"{input.S("simbolo")} = {input.S("origem")}.{input.S("coluna")} [{input.S("unidade")}] × {input.N("fator", 1):G8}"
        + (growth ? " · " + input.S("agregacao") : "");

    public static void Validate(JsonNode v, bool growth)
    {
        var a = Inputs(v, growth);
        if (a.Count > 32)
            throw new InvalidDataException("Declare até 32 entradas por modelo.");
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        string[] calculated =
        [
            "idade_anos",
            "idade_meses",
            "dap_cm",
            "altura_m",
            "area_corr",
            "area_efetiva",
            "volume_ha",
            "fustes_ha",
        ];
        foreach (var x in a)
        {
            string s = x.S("simbolo");
            if (
                !Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_]{0,30}$")
                || new[]
                {
                    "b0",
                    "b1",
                    "b2",
                    "pi",
                    "exp",
                    "log",
                    "ln",
                    "log10",
                    "log2",
                    "sqrt",
                    "abs",
                    "sin",
                    "cos",
                    "tan",
                    "pow",
                    "min",
                    "max",
                }.Contains(s)
                || !symbols.Add(s)
                || x.S("coluna").Length is < 1 or > 255
                || x.S("unidade").Length is < 1 or > 63
                || x.S("origem") is not ("entrada" or "cadastro" or "calculado")
                || !double.IsFinite(x.N("fator", double.NaN))
                || x.N("fator") == 0
            )
                throw new InvalidDataException(
                    $"Entrada {s} inválida. Informe símbolo, origem, coluna, unidade e fator não zero."
                );
            if (
                growth
                && x.S("agregacao") is not ("media" or "soma" or "min" or "max" or "primeiro")
            )
                throw new InvalidDataException("Agregação por parcela inválida.");
            if (
                x.S("origem") == "calculado"
                && (
                    !calculated.Contains(x.S("coluna"))
                    || (!growth && x.S("coluna") is "volume_ha" or "fustes_ha")
                )
            )
                throw new InvalidDataException("Variável calculada indisponível neste modelo.");
        }
        _ = new Formula(FormulaText(v, growth), symbols.Concat(["b0", "b1", "b2"]));
        if (growth && v.S("unidade_idade", "meses") is not ("meses" or "anos"))
            throw new InvalidDataException("A unidade de idade deve ser meses ou anos.");
        if (
            growth
            && v["codigo"] is not null
            && v.S("modelo") is "gompertz" or "logistica"
            && FormulaText(v, true) == Growth.First(g => g.Id == v.S("modelo")).Formula
            && (v.N("b0") <= 0 || v.N("b2") <= 0)
        )
            throw new InvalidDataException("b0 e b2 devem ser positivos para Gompertz/Logística.");
    }
}
