using System.Text.Json.Nodes;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using MathNet.Numerics;
using MathNet.Numerics.Distributions;

namespace Dendro.Core.Statistics;

public sealed record AnalysisReport(
    JsonObject Summary,
    DataTable Details,
    ChartResult? Plot = null
);

public sealed class SampleMoments
{
    public long Count { get; private set; }
    public double Mean { get; private set; }
    public double M2 { get; private set; }
    public double Variance => Count > 1 ? M2 / (Count - 1) : double.NaN;

    public void Add(double value)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException("A amostra contém valor não finito.");
        Count++;
        double delta = value - Mean;
        Mean += delta / Count;
        M2 += delta * (value - Mean);
    }

    public static SampleMoments From(IEnumerable<double> values)
    {
        var result = new SampleMoments();
        foreach (var value in values)
            result.Add(value);
        return result;
    }
}

public static class HypothesisTests
{
    public static void ValidateAlpha(double alpha)
    {
        if (!double.IsFinite(alpha) || alpha <= 0 || alpha >= 1)
            throw new InvalidDataException("O nível de significância deve estar entre 0 e 1.");
    }

    public static double FUpper(double f, double df1, double df2) =>
        f <= 0 ? 1
        : double.IsPositiveInfinity(f) ? 0
        : SpecialFunctions.BetaRegularized(df2 / 2, df1 / 2, 1 / (1 + df1 / df2 * f));

    public static JsonObject Result(
        string test,
        string h0,
        double alpha,
        double statistic,
        double p,
        double df1,
        double df2,
        string assumptions,
        string note = ""
    )
    {
        ValidateAlpha(alpha);
        bool defined = double.IsFinite(p) && p >= 0 && p <= 1 && double.IsFinite(statistic);
        var result = new JsonObject
        {
            ["test"] = test,
            ["h0"] = h0,
            ["alpha"] = alpha,
            ["statistic"] = JsonExtensions.Finite(statistic),
            ["p_value"] = JsonExtensions.Finite(p),
            ["df1"] = JsonExtensions.Finite(df1),
            ["decision"] =
                !defined ? "Teste indefinido: verifique variância, amostra e ajuste."
                : p < alpha ? "Rejeitar H0 ao nível de significância informado."
                : "Não rejeitar H0. Isso não demonstra igualdade ou equivalência.",
            ["assumptions"] = assumptions,
            ["notes"] = note,
        };
        if (double.IsFinite(df2))
            result["df2"] = df2;
        return result;
    }

    private static void ValidateSample(SampleMoments sample)
    {
        if (sample.Count < 2 || !double.IsFinite(sample.Mean) || !double.IsFinite(sample.Variance))
            throw new InvalidDataException(
                "Cada amostra deve ter ao menos duas observações numéricas válidas."
            );
    }

    public static JsonObject Welch(SampleMoments a, SampleMoments b, double alpha = .05)
    {
        ValidateSample(a);
        ValidateSample(b);
        ValidateAlpha(alpha);
        double va = a.Variance / a.Count,
            vb = b.Variance / b.Count,
            se = Math.Sqrt(va + vb);
        double df = (va + vb) * (va + vb) / (va * va / (a.Count - 1) + vb * vb / (b.Count - 1));
        double t = (a.Mean - b.Mean) / se;
        double p =
            double.IsFinite(t) && df > 0
                ? SpecialFunctions.BetaRegularized(df / 2, .5, df / (df + t * t))
                : double.NaN;
        var result = Result(
            "t de Welch (bilateral)",
            "As médias das duas populações são iguais.",
            alpha,
            t,
            p,
            df,
            double.NaN,
            "Amostras independentes; normalidade aproximada das médias. Não exige variâncias iguais."
        );
        AddSamples(result, a, b);
        result["difference"] = a.Mean - b.Mean;
        double critical =
            double.IsFinite(df) && df > 0 ? StudentT.InvCDF(0, 1, df, 1 - alpha / 2) : double.NaN;
        result["critical_high"] = JsonExtensions.Finite(critical);
        result["critical_low"] = JsonExtensions.Finite(-critical);
        result["ci_low"] = JsonExtensions.Finite(a.Mean - b.Mean - critical * se);
        result["ci_high"] = JsonExtensions.Finite(a.Mean - b.Mean + critical * se);
        return result;
    }

    public static JsonObject Paired(SampleMoments differences, double alpha = .05)
    {
        ValidateSample(differences);
        ValidateAlpha(alpha);
        double df = differences.Count - 1,
            se = Math.Sqrt(differences.Variance / differences.Count),
            t = differences.Mean / se;
        double p = double.IsFinite(t)
            ? SpecialFunctions.BetaRegularized(df / 2, .5, df / (df + t * t))
            : double.NaN;
        var result = Result(
            "t pareado (bilateral)",
            "A média das diferenças A − B é zero.",
            alpha,
            t,
            p,
            df,
            double.NaN,
            "Pares correspondentes; independência entre pares e normalidade aproximada das diferenças. Pares incompletos são descartados juntos."
        );
        double critical = StudentT.InvCDF(0, 1, df, 1 - alpha / 2);
        result["n_pairs"] = differences.Count;
        result["difference"] = differences.Mean;
        result["critical_high"] = critical;
        result["critical_low"] = -critical;
        result["ci_low"] = JsonExtensions.Finite(differences.Mean - critical * se);
        result["ci_high"] = JsonExtensions.Finite(differences.Mean + critical * se);
        return result;
    }

    public static JsonObject Variances(SampleMoments a, SampleMoments b, double alpha = .05)
    {
        ValidateSample(a);
        ValidateSample(b);
        ValidateAlpha(alpha);
        double f = a.Variance > 0 && b.Variance > 0 ? a.Variance / b.Variance : double.NaN;
        double upper = double.IsFinite(f) ? FUpper(f, a.Count - 1, b.Count - 1) : double.NaN;
        double lower = double.IsFinite(f) ? FUpper(1 / f, b.Count - 1, a.Count - 1) : double.NaN;
        var result = Result(
            "F de variâncias (bilateral)",
            "As variâncias das duas populações são iguais.",
            alpha,
            f,
            Math.Min(1, 2 * Math.Min(upper, lower)),
            a.Count - 1,
            b.Count - 1,
            "Amostras independentes e populações normais. O teste F é sensível à falta de normalidade; considere Brown–Forsythe."
        );
        AddSamples(result, a, b);
        result["critical_low"] = FisherSnedecor.InvCDF(a.Count - 1, b.Count - 1, alpha / 2);
        result["critical_high"] = FisherSnedecor.InvCDF(a.Count - 1, b.Count - 1, 1 - alpha / 2);
        return result;
    }

    public static JsonObject Anova(IReadOnlyList<SampleMoments> groups, double alpha = .05)
    {
        ValidateAlpha(alpha);
        if (groups.Count < 2)
            throw new InvalidDataException("Selecione pelo menos dois grupos.");
        foreach (var g in groups)
            ValidateSample(g);
        double n = groups.Sum(g => g.Count),
            mean = groups.Sum(g => g.Count * g.Mean) / n;
        double between = groups.Sum(g => g.Count * Math.Pow(g.Mean - mean, 2)),
            within = groups.Sum(g => g.M2);
        double df1 = groups.Count - 1,
            df2 = n - groups.Count,
            f = within > 0 ? between / df1 / (within / df2) : double.NaN;
        var result = Result(
            "ANOVA de um fator",
            "Todas as médias populacionais são iguais.",
            alpha,
            f,
            double.IsFinite(f) ? FUpper(f, df1, df2) : double.NaN,
            df1,
            df2,
            "Observações independentes; resíduos aproximadamente normais e variâncias homogêneas. A rejeição indica diferença em pelo menos um grupo, sem identificar quais pares."
        );
        result["n"] = n;
        result["groups"] = groups.Count;
        result["critical_high"] = FisherSnedecor.InvCDF(df1, df2, 1 - alpha);
        return result;
    }

    public static JsonObject BrownForsythe(IReadOnlyList<double[]> groups, double alpha = .05)
    {
        var deviations = new List<SampleMoments>();
        foreach (var group in groups)
        {
            if (group.Length < 2)
                throw new InvalidDataException("Cada grupo precisa de ao menos duas observações.");
            var sorted = group.Order().ToArray();
            double median =
                sorted.Length % 2 == 1
                    ? sorted[sorted.Length / 2]
                    : sorted[sorted.Length / 2 - 1] / 2 + sorted[sorted.Length / 2] / 2;
            deviations.Add(SampleMoments.From(group.Select(v => Math.Abs(v - median))));
        }
        var result = Anova(deviations, alpha);
        result["test"] = "Brown–Forsythe (Levene pela mediana)";
        result["h0"] = "As variâncias dos grupos são iguais.";
        result["assumptions"] =
            "Grupos independentes; compara desvios absolutos em relação à mediana. Menos sensível à falta de normalidade que o F de variâncias.";
        return result;
    }

    private static void AddSamples(JsonObject result, SampleMoments a, SampleMoments b)
    {
        result["n_a"] = a.Count;
        result["n_b"] = b.Count;
        result["mean_a"] = a.Mean;
        result["mean_b"] = b.Mean;
        result["variance_a"] = a.Variance;
        result["variance_b"] = b.Variance;
    }
}
