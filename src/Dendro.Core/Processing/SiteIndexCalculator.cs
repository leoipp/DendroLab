using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Processing;

/// <summary>Projects parcel DAPd or Hd to the reference age of its current site-index equation.</summary>
public sealed class SiteIndexCalculator
{
    private sealed class Equation
    {
        private readonly Formula formula;
        private readonly double[] coefficients;
        private readonly double referenceAge;
        private readonly bool months;
        private readonly bool useDapd;

        public string ModelCode { get; }
        public string SourceY { get; }
        public string Expression { get; }
        public double BaseAge => referenceAge;
        public string AgeUnit => months ? "meses" : "anos";

        public Equation(JsonObject model)
        {
            string response = model.S("source_y");
            SourceY = response;
            ModelCode = model.S("codigo");
            Expression = model.S("formula");
            useDapd = response.Equals("DAPd", StringComparison.OrdinalIgnoreCase);
            if (!useDapd && !response.Equals("Hd", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"O modelo de IS {ModelCode} precisa usar DAPd ou Hd como Y; recebeu '{response}'.");

            coefficients = (model["coefficients"] as JsonArray ?? [])
                .Select(value => JsonExtensions.Number(value)).ToArray();
            if (coefficients.Length is < 1 or > 4 || coefficients.Any(value => !double.IsFinite(value)))
                throw new InvalidDataException($"O modelo de IS {ModelCode} não contém coeficientes válidos.");
            formula = new Formula(Expression,
                Enumerable.Range(0, coefficients.Length).Select(index => "b" + index).Concat(["x", "x1", "t"]));
            referenceAge = model.N("reference_age", double.NaN);
            if (!double.IsFinite(referenceAge) || referenceAge <= 0)
                throw new InvalidDataException($"O modelo de IS {ModelCode} não tem idade-base positiva.");
            months = model.S("age_unit") switch
            {
                "anos" => false,
                "meses" => true,
                _ => throw new InvalidDataException($"A unidade de idade do modelo de IS {ModelCode} deve ser anos ou meses."),
            };
            double reference = Evaluate(referenceAge);
            if (!double.IsFinite(reference) || reference <= 0)
                throw new InvalidDataException($"A equação de IS {ModelCode} não retorna valor positivo na idade-base.");
        }

        public double Calculate(double ageYears, double dapd, double hd)
        {
            double observed = useDapd ? dapd : hd;
            if (!double.IsFinite(ageYears) || ageYears <= 0 || !double.IsFinite(observed) || observed <= 0)
                return double.NaN;
            double currentAge = months ? ageYears * 12 : ageYears;
            double current = Evaluate(currentAge);
            if (!double.IsFinite(current) || current <= 0)
                throw new InvalidDataException($"A equação de IS {ModelCode} retorna valor inválido na idade atual de {currentAge:G6} {AgeUnit}.");
            double result = observed * Evaluate(referenceAge) / current;
            if (!double.IsFinite(result) || result <= 0)
                throw new InvalidDataException($"O cálculo de IS da equação {ModelCode} gerou um resultado inválido.");
            return result;
        }

        private double Evaluate(double age) => formula.Evaluate(symbol =>
        {
            if (symbol is "x" or "x1" or "t")
                return age;
            if (symbol.Length > 1 && symbol[0] == 'b' && int.TryParse(symbol.AsSpan(1), out int index)
                && index >= 0 && index < coefficients.Length)
                return coefficients[index];
            throw new InvalidDataException($"Símbolo não reconhecido na equação de IS {ModelCode}: {symbol}.");
        });
    }

    private readonly Dictionary<string, Equation> equations;

    private SiteIndexCalculator(IEnumerable<JsonObject> models)
    {
        equations = new(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var equation = new Equation(model);
            if (!equations.TryAdd(equation.ModelCode, equation))
                throw new InvalidDataException($"Há mais de uma equação vigente de IS para o ID {equation.ModelCode}.");
        }
    }

    public static SiteIndexCalculator? Create(JsonObject project)
    {
        if (!project.At("processador").B("calcular_is"))
            return null;
        var models = (project.At("_dendro.site_index_models") as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(model => model.B("vigente", true)
                && (model.S("source_y").Equals("DAPd", StringComparison.OrdinalIgnoreCase)
                    || model.S("source_y").Equals("Hd", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (models.Length == 0)
            throw new InvalidDataException("O cálculo de IS está habilitado, mas não há equações vigentes no catálogo Índice de sítio.");
        return new SiteIndexCalculator(models);
    }

    public static void ValidateConfiguration(JsonObject project) => _ = Create(project);

    public double Calculate(string curveId, double ageYears, double dapd, double hd)
    {
        if (!equations.TryGetValue(curveId, out var equation))
            throw new InvalidDataException($"Não há equação vigente de IS para o ID {curveId}. Cadastre ou ative esse ID em Índice de sítio.");
        return equation.Calculate(ageYears, dapd, hd);
    }

    public string Describe(string curveId)
    {
        if (!equations.TryGetValue(curveId, out var equation))
            throw new InvalidDataException($"Não há equação vigente de IS para o ID {curveId}.");
        return $"{equation.SourceY} · base {equation.BaseAge:G6} {equation.AgeUnit} · {equation.Expression}";
    }
}
