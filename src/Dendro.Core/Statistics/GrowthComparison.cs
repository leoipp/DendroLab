using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;
using MathNet.Numerics.Distributions;

namespace Dendro.Core.Statistics;

public sealed record GrowthComparisonOptions(double MinAge = .5, double MaxAge = 15,
    double ReferenceAge = 7, double RelativeTolerance = 5, double AbsoluteTolerance = 0,
    string AgeColumn = "", string ObservedColumn = "", string DataAgeUnit = "anos",
    string[]? UnitColumns = null, string YearColumn = "", int HistoricalThroughYear = 0,
    bool IndependentValidation = false, string ProductionUnit = "m³/ha");

/// <summary>Compares fixed curves on a common age grid and, optionally, common observed rows.</summary>
public static class GrowthComparison
{
    public static AnalysisReport Run(GrowthCurve baseline, IReadOnlyList<GrowthCurve> candidates, GrowthComparisonOptions options, DataTable? data = null)
    {
        if (candidates.Count == 0 || candidates.Any(c => c.Name == baseline.Name) || candidates.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new InvalidDataException("Selecione pelo menos uma curva distinta da base, sem repetir curvas.");
        var reports = candidates.Select(candidate => Run(baseline, candidate, options, data)).ToArray();
        var summary = new JsonObject
        {
            ["test"] = $"Comparação de crescimento e produção · {candidates.Count} curvas contra {baseline.Name}",
            ["decision"] = string.Join(" ", reports.Select(r => $"{r.Summary["Curva nova"]?.Display()}: {r.Summary.S("decision")}")),
            ["method"] = "Cada curva foi comparada individualmente com a mesma curva base, nas mesmas idades e, se informada, na mesma base de observações.",
            ["Curva base"] = baseline.Name,
            ["Curvas comparadas"] = string.Join("; ", candidates.Select(c => c.Name)),
        };
        foreach (var (report, candidate) in reports.Zip(candidates))
        {
            foreach (var pair in report.Summary)
            {
                if (pair.Key is "test" or "decision" or "method") continue;
                summary[$"{candidate.Name} · {pair.Key}"] = pair.Value?.DeepClone();
            }
        }
        var details = new DataTable(new[] { reports[0].Details.Rows[0][0]?.ToString() ?? "Tipo", "Curva comparada" }
            .Concat(reports[0].Details.Rows[0].Skip(1).Select(c => c?.ToString() ?? "")));
        foreach (var (report, candidate) in reports.Zip(candidates))
            for (int row = 1; row < report.Details.Count; row++)
            {
                var cells = report.Details.Rows[row].ToArray();
                details.Add([cells[0], candidate.Name, .. cells.Skip(1)]);
            }
        var first = reports[0].Plot!;
        var fits = new JsonArray(new JsonObject { ["converged"] = true, ["model"] = baseline.Name, ["group"] = baseline.Name, ["curve_color"] = "#21669D" });
        var lines = new List<double[][]> { first.Curves[0] };
        var points = first.Points.ToList();
        foreach (var (report, index) in reports.Select((r, i) => (r, i)))
        {
            var candidate = candidates[index];
            var color = CandidateColor(index);
            fits.Add(new JsonObject { ["converged"] = true, ["model"] = candidate.Name, ["group"] = candidate.Name, ["curve_color"] = color, ["line_dashed"] = index % 2 == 0 });
            lines.Add(report.Plot!.Curves[1]);
        }
        var chart = first.Chart.DeepClone().AsObject();
        chart["title"] = "Curvas de crescimento e produção · base e comparações";
        chart["fits"] = fits;
        chart["selected_fits"] = new JsonArray(Enumerable.Range(0, fits.Count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        return new(summary, details, new(chart, points.ToArray(), lines.ToArray(), first.Invalid));
    }

    private static string CandidateColor(int index)
    {
        // Golden-angle spacing keeps later candidates distinct instead of repeating a six-color cycle.
        double hue = (24 + index * 137.507764) % 360 / 60;
        double saturation = .72, lightness = index % 2 == 0 ? .47 : .40;
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue % 2 - 1));
        double m = lightness - chroma / 2;
        (double r, double g, double b) = hue switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x),
        };
        return $"#{(byte)Math.Round((r + m) * 255):X2}{(byte)Math.Round((g + m) * 255):X2}{(byte)Math.Round((b + m) * 255):X2}";
    }

    private static Func<double, double> Predictor(GrowthCurve curve)
    {
        if (curve.AgeUnit is not ("anos" or "meses") || curve.Coefficients.Length is < 1 or > 4 ||
            curve.Coefficients.Any(v => !double.IsFinite(v)) || !double.IsFinite(curve.AgeFactor) || curve.AgeFactor <= 0)
            throw new InvalidDataException("Confira unidades de idade, fator e coeficientes das curvas.");
        var formula = new Formula(curve.Formula, new[] { "x", "x1", "t", curve.AgeSymbol }.Concat(curve.FixedInputs?.Keys ?? [])
            .Concat(Enumerable.Range(0, curve.Coefficients.Length).Select(i => "b" + i)));
        return years => formula.Evaluate(symbol => symbol is "x" or "x1" or "t" || symbol == curve.AgeSymbol
            ? years * (curve.AgeUnit == "meses" ? 12 : 1) * curve.AgeFactor
            : curve.FixedInputs?.TryGetValue(symbol, out double value) == true ? value : curve.Coefficients[int.Parse(symbol[1..])]);
    }

    private sealed class Metrics
    {
        public int N;
        public double OldSquare, NewSquare, OldAbs, NewAbs, OldBias, NewBias;
        public readonly Dictionary<string, SampleMoments> Units = new(StringComparer.Ordinal);
        public void Add(double observed, double old, double updated, string? unit)
        {
            double a = old - observed, b = updated - observed;
            N++; OldSquare += a * a; NewSquare += b * b; OldAbs += Math.Abs(a); NewAbs += Math.Abs(b);
            OldBias += a; NewBias += b;
            if (unit != null)
            {
                if (!Units.TryGetValue(unit, out var moments)) Units[unit] = moments = new();
                moments.Add(Math.Abs(a) - Math.Abs(b));
            }
        }
    }

    public static AnalysisReport Run(GrowthCurve baseline, GrowthCurve updated, GrowthComparisonOptions options, DataTable? data = null)
    {
        var o = options;
        if (!double.IsFinite(o.MinAge) || !double.IsFinite(o.MaxAge) || o.MinAge <= 0 || o.MaxAge <= o.MinAge || o.MaxAge > 10000 ||
            !double.IsFinite(o.ReferenceAge) || o.ReferenceAge < o.MinAge || o.ReferenceAge > o.MaxAge ||
            !double.IsFinite(o.RelativeTolerance) || o.RelativeTolerance < 0 || !double.IsFinite(o.AbsoluteTolerance) || o.AbsoluteTolerance < 0 ||
            o.DataAgeUnit is not ("anos" or "meses") || string.IsNullOrWhiteSpace(o.ProductionUnit))
            throw new InvalidDataException("Revise o intervalo positivo, a idade de referência e as tolerâncias não negativas.");
        var old = Predictor(baseline); var next = Predictor(updated);
        (double Old, double New) Predict(double age)
        {
            double a = old(age), b = next(age);
            if (!double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b < 0)
                throw new InvalidDataException($"Produção negativa ou inválida aos {age:G6} anos. Revise curvas e intervalo.");
            return (a, b);
        }
        var details = new DataTable(["Tipo", "Grupo", "Idade (anos)", "Produção vigente", "Produção nova",
            "Nova − vigente", "Diferença (%)", "Observado", "Erro vigente (previsto − observado)", "Erro nova (previsto − observado)"]);
        var lines = new[] { new List<double[]>(), new List<double[]>() };
        double maxAbs = 0, maxPct = 0; int outside = 0, pctUndefined = 0;
        for (int i = 0; i <= 200; i++)
        {
            double age = o.MinAge + (o.MaxAge - o.MinAge) * i / 200;
            var p = Predict(age); double delta = p.New - p.Old;
            double pct = p.Old > 0 ? delta / p.Old * 100 : double.NaN;
            if (!double.IsFinite(pct)) pctUndefined++; else maxPct = Math.Max(maxPct, Math.Abs(pct));
            maxAbs = Math.Max(maxAbs, Math.Abs(delta));
            if (Math.Abs(delta) > o.AbsoluteTolerance + o.RelativeTolerance / 100 * Math.Abs(p.Old)) outside++;
            details.Add("Curvas", "Mesmas idades", age, p.Old, p.New, delta, double.IsFinite(pct) ? pct : null, null, null, null);
            lines[0].Add([age, p.Old]); lines[1].Add([age, p.New]);
        }
        var reference = Predict(o.ReferenceAge);
        var summary = new JsonObject {
            ["test"] = "Comparação de crescimento e produção · " + updated.Name + " × " + baseline.Name,
            ["decision"] = outside == 0 ? "Curvas compatíveis com a tolerância prática nas 201 idades avaliadas. Isso não demonstra identidade estatística."
                : $"As curvas diferem além da tolerância prática em {outside} das 201 idades avaliadas.",
            ["method"] = "Comparação dos modelos fixos nas mesmas idades; tolerância = absoluta + percentual × produção vigente. Não se aplica teste F às predições da grade.",
            ["assumptions"] = "As curvas devem representar a mesma variável de produção, unidade, região, regime e população. A faixa deve respeitar o domínio observado em ambos os ajustes.",
            ["notes"] = "Uma curva nova mais alta representa revisão da produção prevista. Mais dados podem mudar cobertura de idades, composição de sítios/regimes e incerteza; isso não comprova aumento biológico nem sua causa. Para avaliar a contribuição dos novos dados, compare os modelos na mesma base independente e examine a composição e os resíduos por período. Ajustes anuais cumulativos e medições repetidas não são amostras independentes.",
            ["Curva vigente / anterior"] = baseline.Name, ["Curva nova"] = updated.Name,
            ["Unidade da produção confirmada"] = o.ProductionUnit,
            ["Fórmula vigente"] = baseline.Formula, ["Fórmula nova"] = updated.Formula,
            ["Coeficientes vigentes"] = string.Join("; ", baseline.Coefficients.Select((v, i) => $"b{i}={v.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}")),
            ["Coeficientes novos"] = string.Join("; ", updated.Coefficients.Select((v, i) => $"b{i}={v.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}")),
            ["Unidade de idade vigente"] = baseline.AgeUnit, ["Unidade de idade nova"] = updated.AgeUnit,
            ["Idade mínima (anos)"] = o.MinAge, ["Idade máxima (anos)"] = o.MaxAge,
            ["Tolerância relativa (%)"] = o.RelativeTolerance, ["Tolerância absoluta"] = o.AbsoluteTolerance,
            ["Maior diferença absoluta na grade"] = maxAbs, ["Maior diferença percentual absoluta na grade"] = pctUndefined == 201 ? null : maxPct,
            ["Idades com percentual indefinido (vigente zero)"] = pctUndefined,
            ["Idade de referência (anos)"] = o.ReferenceAge, ["Produção vigente na referência"] = reference.Old,
            ["Produção nova na referência"] = reference.New, ["Revisão da produção na referência"] = reference.New - reference.Old,
            ["Validação observada"] = data == null ? "Não informada: somente comparação das curvas." : o.IndependentValidation ? "Base declarada independente dos dois ajustes." : "Avaliação descritiva: pode incluir dados dos ajustes.",
        };
        var points = new List<ChartPoint>();
        if (data != null)
        {
            int ac = data.Require(o.AgeColumn), yc = data.Require(o.ObservedColumn);
            int yearColumn = string.IsNullOrWhiteSpace(o.YearColumn) ? -1 : data.Require(o.YearColumn);
            if (yearColumn >= 0 && o.HistoricalThroughYear is < 1 or > 9999)
                throw new InvalidDataException("Informe o último ano da base histórica (1 a 9999).");
            int[] unitColumns = (o.UnitColumns ?? []).Distinct().Select(data.Require).ToArray();
            var metrics = new Dictionary<string, Metrics> { ["Base comum"] = new() };
            int omitted = 0, outsideAges = 0, missingUnit = 0;
            for (int row = 1; row < data.Count; row++)
            {
                double age = data.Number(row, ac) / (o.DataAgeUnit == "meses" ? 12 : 1), observed = data.Number(row, yc);
                double year = yearColumn >= 0 ? data.Number(row, yearColumn) : 0;
                if (!double.IsFinite(age) || !double.IsFinite(observed) || age <= 0 || observed < 0 ||
                    (yearColumn >= 0 && (!double.IsFinite(year) || year != Math.Truncate(year) || year is < 1 or > 9999))) { omitted++; continue; }
                if (age < o.MinAge || age > o.MaxAge) { outsideAges++; continue; }
                var p = Predict(age);
                string cohort = yearColumn < 0 ? "Base comum" : year <= o.HistoricalThroughYear ? "Histórico" : "Novos dados";
                string? unit = unitColumns.Length == 0 ? null : JsonSerializer.Serialize(unitColumns.Select(c => TableTools.Key(data.Value(row, c))));
                if (unitColumns.Any(c => string.IsNullOrWhiteSpace(data.Text(row, c)))) { unit = null; missingUnit++; }
                metrics["Base comum"].Add(observed, p.Old, p.New, unit);
                if (cohort != "Base comum")
                {
                    if (!metrics.TryGetValue(cohort, out var m)) metrics[cohort] = m = new();
                    m.Add(observed, p.Old, p.New, unit);
                }
                details.Add("Validação", cohort, age, p.Old, p.New, p.New - p.Old, p.Old > 0 ? (p.New - p.Old) / p.Old * 100 : null,
                    observed, p.Old - observed, p.New - observed);
                points.Add(new(age, observed, row + 1, cohort));
            }
            if (metrics["Base comum"].N == 0) throw new InvalidDataException("Nenhuma observação válida no intervalo de idades escolhido.");
            summary["Linhas inválidas omitidas"] = omitted; summary["Linhas fora da faixa omitidas"] = outsideAges;
            summary["Coluna da idade observada"] = o.AgeColumn; summary["Coluna da produção observada"] = o.ObservedColumn;
            summary["Coluna que separa históricos e novos"] = yearColumn < 0 ? "Não informada" : o.YearColumn;
            if (yearColumn >= 0) summary["Último ano histórico"] = o.HistoricalThroughYear;
            summary["Linhas sem identificação de unidade"] = missingUnit;
            foreach (var (name, m) in metrics)
            {
                summary[name + " · N"] = m.N;
                summary[name + " · RMSE vigente"] = Math.Sqrt(m.OldSquare / m.N);
                summary[name + " · RMSE nova"] = Math.Sqrt(m.NewSquare / m.N);
                summary[name + " · MAE vigente"] = m.OldAbs / m.N; summary[name + " · MAE nova"] = m.NewAbs / m.N;
                summary[name + " · Viés vigente (previsto − observado)"] = m.OldBias / m.N;
                summary[name + " · Viés nova (previsto − observado)"] = m.NewBias / m.N;
            }
            var common = metrics["Base comum"];
            summary["Desempenho na base comum"] = common.NewAbs < common.OldAbs
                ? "A curva nova tem menor MAE nesta base. Se os dados foram usados no ajuste, isso não comprova melhora fora da amostra."
                : common.NewAbs > common.OldAbs ? "A curva vigente tem menor MAE nesta base." : "As duas curvas têm o mesmo MAE nesta base.";
            if (o.IndependentValidation && unitColumns.Length > 0 && missingUnit == 0 && common.Units.Count >= 5)
            {
                var delta = new SampleMoments(); foreach (var unit in common.Units.Values) delta.Add(unit.Mean);
                double se = Math.Sqrt(delta.Variance / delta.Count), critical = StudentT.InvCDF(0, 1, delta.Count - 1, .975);
                double low = delta.Mean - critical * se, high = delta.Mean + critical * se;
                summary["Unidades independentes"] = delta.Count;
                summary["Redução média do erro absoluto por unidade (vigente − nova)"] = delta.Mean;
                summary["IC 95% da redução por unidade · inferior"] = low; summary["IC 95% da redução por unidade · superior"] = high;
                summary["Inferência de desempenho"] = se == 0 ? "Diferenças constantes entre unidades: intervalo degenerado, sem conclusão inferencial."
                    : low > 0 ? "Há evidência de menor erro absoluto da curva nova nesta base independente. Isso não demonstra causa do aumento de produção."
                    : high < 0 ? "Há evidência de menor erro absoluto da curva vigente nesta base independente."
                    : "O intervalo inclui zero: a melhora de precisão não ficou demonstrada.";
                summary["Método do IC"] = "IC t aproximado sobre a redução média do erro absoluto em cada unidade, com peso igual por unidade. Pressupõe independência entre unidades e distribuição adequada das médias; cinco unidades é apenas um mínimo operacional. Mantém juntas todas as medições da unidade.";
            }
            else summary["Inferência de desempenho"] = "Somente descritiva: IC exige validação declarada independente, identificação completa das unidades e pelo menos cinco unidades independentes.";
        }
        var fits = new JsonArray();
        foreach (var (curve, color) in new[] { (baseline, "#21669D"), (updated, "#D56D2C") })
            fits.Add(new JsonObject { ["converged"] = true, ["model"] = curve.Name, ["group"] = curve.Name, ["curve_color"] = color });
        if (data == null)
            foreach (var (line, index) in lines.Select((line, index) => (line, index)))
                foreach (var p in line) points.Add(new(p[0], p[1], points.Count + 1, index == 0 ? baseline.Name : updated.Name));
        double highest = Math.Max(lines.SelectMany(l => l).Max(p => p[1]), points.Count == 0 ? 0 : points.Max(p => p.Y));
        var chart = new JsonObject { ["title"] = "Curvas vigente e nova · crescimento e produção", ["x_label"] = "Idade (anos)",
            ["y_label"] = "Produção (" + o.ProductionUnit + ")", ["point_size"] = data == null ? 0 : 2,
            ["ymin"] = 0, ["ymax"] = highest > 0 ? highest * 1.05 : 1,
            ["xmin"] = o.MinAge, ["xmax"] = o.MaxAge, ["fits"] = fits, ["selected_fits"] = new JsonArray(0, 1) };
        return new(summary, details, new(chart, points.ToArray(), lines.Select(l => l.ToArray()).ToArray(), 0));
    }
}
