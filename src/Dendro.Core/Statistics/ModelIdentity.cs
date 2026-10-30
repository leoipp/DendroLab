using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;

namespace Dendro.Core.Statistics;

public static class ModelIdentity
{
    public static AnalysisReport Test(
        DataTable data,
        string x,
        string y,
        string group,
        RegressionFamily family,
        double alpha = .05
    )
    {
        HypothesisTests.ValidateAlpha(alpha);
        var coefficientNames = System
            .Text.RegularExpressions.Regex.Matches(family.Formula, @"\bb[0-9]+\b")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (
            family.Parameters is < 1 or > 4
            || !coefficientNames.SetEquals(
                Enumerable.Range(0, family.Parameters).Select(i => "b" + i)
            )
        )
            throw new InvalidDataException(
                "O número de coeficientes declarado deve corresponder aos coeficientes utilizados na fórmula."
            );
        if (string.IsNullOrWhiteSpace(group))
            throw new InvalidDataException("Selecione a coluna que identifica os grupos.");
        int groupColumn = data.Require(group);
        if (
            data.Rows.Skip(1)
                .Select(row => TableCursorText(row, groupColumn))
                .Distinct()
                .Take(26)
                .Count() > 25
        )
            throw new InvalidDataException("Selecione no máximo 25 grupos por análise.");
        var engine = new RegressionEngine([family]);
        var full = engine.Generate(
            data,
            new JsonObject
            {
                ["x"] = x,
                ["y"] = y,
                ["stratum"] = group,
            },
            0
        );
        var reduced = engine.Generate(data, new JsonObject { ["x"] = x, ["y"] = y }, 0);
        var separateFits = full.Chart["fits"]!.AsArray();
        var common = reduced.Chart["fits"]!.AsArray()[0]!;
        if (separateFits.Count < 2 || separateFits.Count > 25)
            throw new InvalidDataException("O teste de identidade aceita de 2 a 25 grupos.");
        if (!common.B("converged") || separateFits.Any(f => !f.B("converged")))
            throw new InvalidDataException(
                "Todos os ajustes, incluindo o comum, precisam convergir e ter observações suficientes."
            );
        CheckRank(full.Points, common, family);
        foreach (var fit in separateFits)
            CheckRank(full.Points.Where(p => p.Group == fit.S("group")), fit!, family);
        double n = full.Points.Length,
            k = family.Parameters,
            df1 = k * (separateFits.Count - 1),
            df2 = n - k * separateFits.Count;
        if (df2 <= 0)
            throw new InvalidDataException("Não há graus de liberdade residuais suficientes.");
        double sseFull = separateFits.Sum(f => f.N("rmse") * f.N("rmse") * f.N("n"));
        double sseReduced = common.N("rmse") * common.N("rmse") * common.N("n");
        bool zeroResidual = sseFull <= 1e-24 * full.Points.Sum(p => p.Y * p.Y);
        if (
            !zeroResidual
            && sseReduced < sseFull - 1e-7 * Math.Max(double.Epsilon, Math.Max(sseReduced, sseFull))
        )
            throw new InvalidDataException(
                "O ajuste comum apresentou soma de quadrados menor que os ajustes separados. Revise a convergência antes de testar."
            );
        double f =
            !zeroResidual && sseFull > 0
                ? Math.Max(0, sseReduced - sseFull) / df1 / (sseFull / df2)
                : double.NaN;
        string normalized = family
            .Formula.Replace("x1", "x", StringComparison.Ordinal)
            .Replace("t", "x", StringComparison.Ordinal)
            .Replace(" ", "");
        bool linear = RegressionEngine
            .Models.Take(3)
            .Append(RegressionEngine.Models[5])
            .Any(m => m.Formula == normalized);
        var summary = HypothesisTests.Result(
            linear
                ? "Identidade de modelos — F (Graybill)"
                : "Identidade de modelos não lineares — aproximação F",
            "Uma única equação, com os mesmos coeficientes, representa todos os grupos.",
            alpha,
            f,
            double.IsFinite(f) ? HypothesisTests.FUpper(f, df1, df2) : double.NaN,
            df1,
            df2,
            "Mesma família e variável resposta nos grupos; erros independentes, aproximadamente normais e homocedásticos. Medições repetidas da mesma parcela exigem um método que considere essa dependência.",
            linear
                ? "Compara o modelo reduzido (comum) ao completo (separado por grupo)."
                : "A estatística F é uma aproximação para regressão não linear. Não constitui teste exato para qualquer família."
        );
        summary["n"] = n;
        summary["groups"] = separateFits.Count;
        summary["model"] = family.Name;
        summary["formula"] = family.Formula;
        summary["sse_common"] = sseReduced;
        summary["sse_separate"] = sseFull;
        summary["critical_high"] = FisherSnedecor.InvCDF(df1, df2, 1 - alpha);
        if (zeroResidual)
            summary["notes"] =
                summary.S("notes")
                + " Resíduo numericamente nulo: não há variância residual para uma inferência F confiável.";
        var details = new DataTable(["Grupo", "N", "SQ resíduos", "RMSE", "b0", "b1", "b2", "b3"]);
        void Add(JsonNode fit, string label) =>
            details.Add(
                label,
                fit.N("n"),
                fit.N("rmse") * fit.N("rmse") * fit.N("n"),
                fit.N("rmse"),
                fit.N("b0"),
                fit.N("b1"),
                fit.N("b2"),
                fit.N("b3")
            );
        Add(common, "Equação comum");
        foreach (var fit in separateFits)
            Add(fit!, fit.S("group"));
        var chart = full.Chart.DeepClone().AsObject();
        chart["title"] = "Identidade de modelos · " + family.Name;
        var plotFits = chart["fits"]!.AsArray();
        var pooled = common.DeepClone().AsObject();
        pooled["group"] = "Equação comum";
        pooled["curve_color"] = "#263D52";
        string[] colors = ["#21669D", "#1C8965", "#D56D2C", "#8155B4", "#C33E5E"];
        foreach (var fit in plotFits)
        {
            uint hash = 2166136261;
            foreach (char c in fit.S("group"))
                hash = (hash ^ c) * 16777619;
            fit!["curve_color"] = colors[hash % 5];
        }
        plotFits.Add(pooled);
        chart["selected_fits"] = new JsonArray(
            Enumerable
                .Range(0, plotFits.Count)
                .Select(i => (JsonNode?)JsonValue.Create(i))
                .ToArray()
        );
        return new(
            summary,
            details,
            new(chart, full.Points, full.Curves.Concat(reduced.Curves).ToArray(), full.Invalid)
        );
    }

    private static string TableCursorText(List<object?> row, int column) =>
        Dendro.Core.Tabular.TableCursor.Text(column < row.Count ? row[column] : null);

    private static void CheckRank(
        IEnumerable<ChartPoint> points,
        JsonNode fit,
        RegressionFamily family
    )
    {
        int k = family.Parameters;
        var gram = Matrix<double>.Build.Dense(k, k);
        var coefficients = Enumerable.Range(0, k).Select(i => fit.N("b" + i)).ToArray();
        var formula = new Formula(
            family.Formula,
            Enumerable.Range(0, k).Select(i => "b" + i).Concat(["x", "x1", "t"])
        );
        double Evaluate(double x) =>
            formula.Evaluate(s => s is "x" or "x1" or "t" ? x : coefficients[int.Parse(s[1..])]);
        foreach (var point in points)
        {
            var gradient = new double[k];
            for (int i = 0; i < k; i++)
            {
                double original = coefficients[i],
                    step = 1e-5 * Math.Max(1, Math.Abs(original));
                coefficients[i] = original + step;
                double upper = Evaluate(point.X);
                coefficients[i] = original - step;
                double lower = Evaluate(point.X);
                coefficients[i] = original;
                gradient[i] = (upper - lower) / (2 * step);
                if (!double.IsFinite(gradient[i]))
                    throw new InvalidDataException(
                        "Não foi possível avaliar a identificabilidade dos coeficientes."
                    );
            }
            for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
                gram[i, j] += gradient[i] * gradient[j];
        }
        var norms = Enumerable.Range(0, k).Select(i => Math.Sqrt(gram[i, i])).ToArray();
        if (norms.Any(v => !double.IsFinite(v) || v <= 0))
            throw new InvalidDataException(
                "Há coeficientes não identificáveis no ajuste. Revise a fórmula e os dados antes do teste."
            );
        for (int i = 0; i < k; i++)
        for (int j = 0; j < k; j++)
            gram[i, j] /= norms[i] * norms[j];
        var singular = gram.Svd().S;
        if (singular.Min() <= singular.Max() * 1e-12)
            throw new InvalidDataException(
                "O ajuste apresenta coeficientes redundantes ou numericamente não identificáveis; a inferência F não pode ser aplicada com esses graus de liberdade."
            );
    }
}
