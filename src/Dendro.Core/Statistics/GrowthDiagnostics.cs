using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Statistics;

public sealed record GrowthCurve(
    string Name,
    string Formula,
    double[] Coefficients,
    string AgeUnit = "anos",
    string AgeSymbol = "t",
    double AgeFactor = 1
);

public sealed record IncrementPoint(double Age, double Volume, double Ima, double Ica);

public sealed record IncrementCrossing(double Age, double Value, string Kind);

public sealed record GrowthDiagnostic(
    GrowthCurve Curve,
    IncrementPoint[] Points,
    IncrementCrossing[] Crossings,
    string Status,
    int Invalid,
    double MaximumImaAge,
    double MaximumIma
);

public static class GrowthDiagnostics
{
    public static GrowthDiagnostic Calculate(
        GrowthCurve curve,
        double minAge,
        double maxAge,
        bool annualDifference = false,
        int samples = 601
    )
    {
        if (
            !double.IsFinite(minAge)
            || !double.IsFinite(maxAge)
            || minAge <= 0
            || maxAge <= minAge
            || maxAge > 10000
            || samples is < 21 or > 5001
        )
            throw new InvalidDataException(
                "Use um intervalo positivo de idades em anos, até 10.000 anos, e 21 a 5.001 amostras."
            );
        if (
            curve.AgeUnit is not ("anos" or "meses")
            || curve.Coefficients.Length is < 1 or > 4
            || curve.Coefficients.Any(c => !double.IsFinite(c))
            || !double.IsFinite(curve.AgeFactor)
            || curve.AgeFactor <= 0
        )
            throw new InvalidDataException(
                "Confira a unidade de idade, o fator e os coeficientes do modelo."
            );
        var formula = new Formula(
            curve.Formula,
            new[] { "x", "x1", "t", curve.AgeSymbol }.Concat(
                Enumerable.Range(0, curve.Coefficients.Length).Select(i => "b" + i)
            )
        );
        double Volume(double years)
        {
            if (years < 0)
                return double.NaN;
            double x = years * (curve.AgeUnit == "meses" ? 12 : 1) * curve.AgeFactor;
            return formula.Evaluate(s =>
                s is "x" or "x1" or "t" || s == curve.AgeSymbol
                    ? x
                    : curve.Coefficients[int.Parse(s[1..])]
            );
        }
        double Ica(double age)
        {
            if (annualDifference)
                return age >= 1 ? Volume(age) - Volume(age - 1) : double.NaN;
            double h = Math.Min(age / 4, Math.Max(1e-5, age * 1e-4));
            return (
                    Volume(age - 2 * h)
                    - 8 * Volume(age - h)
                    + 8 * Volume(age + h)
                    - Volume(age + 2 * h)
                ) / (12 * h);
        }
        double Difference(double age) => Ica(age) - Volume(age) / age;
        var points = new List<IncrementPoint>();
        int invalid = 0;
        for (int i = 0; i < samples; i++)
        {
            double age = minAge + (maxAge - minAge) * i / (samples - 1),
                v = Volume(age),
                ima = v / age,
                ica = Ica(age);
            if (!double.IsFinite(v) || !double.IsFinite(ima) || !double.IsFinite(ica))
            {
                invalid++;
                continue;
            }
            points.Add(new(age, v, ima, ica));
        }
        if (points.Count < 3)
            throw new InvalidDataException(
                "A equação não produz valores finitos suficientes nesse intervalo de idades."
            );
        bool NearZero(double age, double difference) =>
            Math.Abs(difference)
            <= 1e-7
                * Math.Max(
                    double.Epsilon,
                    Math.Max(Math.Abs(Ica(age)), Math.Abs(Volume(age) / age))
                );
        bool coincident = invalid == 0 && points.All(p => NearZero(p.Age, p.Ica - p.Ima));
        var crossings = new List<IncrementCrossing>();
        if (!coincident)
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                // Never bridge an invalid segment of the function.
                if (b.Age - a.Age > (maxAge - minAge) / (samples - 1) * 1.1)
                    continue;
                double left = a.Age,
                    right = b.Age,
                    dl = a.Ica - a.Ima,
                    dr = b.Ica - b.Ima;
                if (Math.Sign(dl) == Math.Sign(dr) && !NearZero(left, dl) && !NearZero(right, dr))
                    continue;
                if (NearZero(left, dl))
                    right = left;
                else if (NearZero(right, dr))
                    left = right;
                else
                    for (int step = 0; step < 60; step++)
                    {
                        double mid = (left + right) / 2,
                            difference = Difference(mid);
                        if (!double.IsFinite(difference))
                        {
                            left = right = double.NaN;
                            break;
                        }
                        if (Math.Sign(difference) == Math.Sign(dl))
                        {
                            left = mid;
                            dl = difference;
                        }
                        else
                            right = mid;
                    }
                double age = (left + right) / 2;
                if (!double.IsFinite(age) || !NearZero(age, Difference(age)))
                    continue;
                if (crossings.Any(c => Math.Abs(c.Age - age) < (maxAge - minAge) * 1e-6))
                    continue;
                double epsilon = Math.Max(1e-6, age * 1e-4),
                    before = Difference(Math.Max(minAge, age - epsilon)),
                    after = Difference(Math.Min(maxAge, age + epsilon));
                string kind =
                    annualDifference ? "Cruzamento do ICA anual com o IMA"
                    : before > 0 && after < 0 ? "Máximo local do IMA"
                    : before < 0 && after > 0 ? "Mínimo local do IMA"
                    : "Cruzamento / ponto estacionário";
                crossings.Add(new(age, Volume(age) / age, kind));
            }
        var maximum = points.MaxBy(p => p.Ima)!;
        double bestAge = maximum.Age,
            bestIma = maximum.Ima;
        foreach (var crossing in crossings.Where(c => c.Kind == "Máximo local do IMA"))
            if (crossing.Value > bestIma)
            {
                bestAge = crossing.Age;
                bestIma = crossing.Value;
            }
        string status =
            coincident ? "ICA e IMA coincidem no intervalo; não há culminação única."
            : crossings.Count == 0
                ? "Sem cruzamento detectado no intervalo analisado. Isso não invalida automaticamente o modelo."
            : $"{crossings.Count} cruzamento(s) no intervalo analisado.";
        if (points.Any(p => p.Volume < 0 || p.Ica < 0))
            status += " Há volume ou incremento negativo: revisar plausibilidade biológica.";
        if (!coincident && (bestAge == points[0].Age || bestAge == points[^1].Age))
            status += " O maior IMA está na borda do intervalo.";
        if (invalid > 0)
            status +=
                $" {invalid} idades com resultado não finito ou ICA anual indisponível foram omitidas.";
        if (annualDifference)
            status +=
                " O cruzamento por diferença anual não é necessariamente o máximo exato do IMA contínuo.";
        return new(curve, points.ToArray(), crossings.ToArray(), status, invalid, bestAge, bestIma);
    }

    public static AnalysisReport Report(
        IReadOnlyList<GrowthCurve> curves,
        double minAge,
        double maxAge,
        bool annualDifference
    )
    {
        if (curves.Count is < 1 or > 12)
            throw new InvalidDataException("Selecione de 1 a 12 curvas.");
        var diagnostics = curves
            .Select(c => Calculate(c, minAge, maxAge, annualDifference))
            .ToArray();
        var details = new DataTable([
            "Curva",
            "Idade (anos)",
            "Produção",
            "IMA (por ano)",
            "ICA (por ano)",
        ]);
        var points = new List<ChartPoint>();
        var lines = new List<double[][]>();
        var fits = new JsonArray();
        var summaries = new JsonArray();
        string[] colors = ["#21669D", "#1C8965", "#D56D2C", "#8155B4", "#C33E5E", "#008A99"];
        foreach (var d in diagnostics)
        {
            int index = summaries.Count;
            summaries.Add(
                new JsonObject
                {
                    ["curve"] = d.Curve.Name,
                    ["status"] = d.Status,
                    ["maximum_ima_age"] = d.MaximumImaAge,
                    ["maximum_ima"] = d.MaximumIma,
                    ["crossings"] = new JsonArray(
                        d.Crossings.Select(c =>
                                (JsonNode?)
                                    new JsonObject
                                    {
                                        ["age"] = c.Age,
                                        ["increment"] = c.Value,
                                        ["kind"] = c.Kind,
                                    }
                            )
                            .ToArray()
                    ),
                }
            );
            foreach (var p in d.Points)
            {
                details.Add(d.Curve.Name, p.Age, p.Volume, p.Ima, p.Ica);
                points.Add(new(p.Age, p.Ima, points.Count + 1, d.Curve.Name));
                points.Add(new(p.Age, p.Ica, points.Count + 1, d.Curve.Name));
            }
            foreach (bool ica in new[] { false, true })
            {
                fits.Add(
                    new JsonObject
                    {
                        ["converged"] = true,
                        ["model"] = ica ? "ICA" : "IMA",
                        ["group"] = d.Curve.Name,
                        ["curve_color"] = colors[index % colors.Length],
                        ["line_dashed"] = ica,
                    }
                );
                lines.Add(d.Points.Select(p => new[] { p.Age, ica ? p.Ica : p.Ima }).ToArray());
            }
        }
        var chart = new JsonObject
        {
            ["title"] = "ICA e IMA",
            ["x_label"] = "Idade (anos)",
            ["y_label"] = "Incremento por ano",
            ["point_size"] = 0,
            ["fits"] = fits,
            ["selected_fits"] = new JsonArray(
                Enumerable
                    .Range(0, fits.Count)
                    .Select(i => (JsonNode?)JsonValue.Create(i))
                    .ToArray()
            ),
        };
        chart["frontier_points"] = new JsonArray(
            diagnostics
                .SelectMany(d => d.Crossings)
                .Select(c =>
                    (JsonNode?)
                        new JsonObject
                        {
                            ["x"] = c.Age,
                            ["y"] = c.Value,
                            ["side"] = 1,
                        }
                )
                .ToArray()
        );
        chart["xmin"] = minAge;
        chart["xmax"] = maxAge;
        chart["ymin"] = Math.Min(0, points.Min(p => p.Y));
        chart["reference_lines"] = new JsonArray(
            diagnostics
                .SelectMany(d => d.Crossings)
                .Take(20)
                .Select(c =>
                    (JsonNode?)
                        new JsonObject
                        {
                            ["x"] = c.Age,
                            ["label"] =
                                c.Age.ToString(
                                    "0.###",
                                    System.Globalization.CultureInfo.CurrentCulture
                                ) + " anos",
                        }
                )
                .ToArray()
        );
        var summary = new JsonObject
        {
            ["test"] = "Diagnóstico ICA e IMA",
            ["method"] = annualDifference
                ? "ICA anual = V(t) − V(t−1 ano); IMA = V(t)/t"
                : "ICA instantâneo = dV/dt (derivada numérica); IMA = V(t)/t",
            ["age_min"] = minAge,
            ["age_max"] = maxAge,
            ["age_unit"] = "anos",
            ["curves"] = summaries,
        };
        return new(summary, details, new(chart, points.ToArray(), lines.ToArray(), 0));
    }
}
