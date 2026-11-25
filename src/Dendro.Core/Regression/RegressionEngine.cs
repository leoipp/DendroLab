using System.Text;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Regression;

public sealed record RegressionFamily(string Name, string Formula, int Parameters, string Id = "", int Predictors = 1);

public readonly record struct ChartPoint(
    double X,
    double Y,
    int Row,
    string Group,
    bool Excluded = false,
    int Side = 0,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double X2 = double.NaN,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double X3 = double.NaN,
    int InputCount = 1
)
{
    public double Input(int index) => index switch
    {
        0 => X,
        1 when InputCount > 1 => X2,
        2 when InputCount > 2 => X3,
        _ => 0,
    };
}

public sealed record ChartResult(
    JsonObject Chart,
    ChartPoint[] Points,
    double[][][] Curves,
    int Invalid
);

public sealed class RegressionEngine
{
    private static readonly ConcurrentDictionary<string, bool> NestedFormulaCache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<RegressionFamily> families;
    private readonly IReadOnlyList<RegressionFamily> warmFamilies;

    public RegressionEngine(IReadOnlyList<RegressionFamily>? families = null)
        : this(families, families) { }

    public RegressionEngine(
        IReadOnlyList<RegressionFamily>? families,
        IReadOnlyList<RegressionFamily>? warmFamilies
    )
    {
        this.families = families ?? Models;
        this.warmFamilies = warmFamilies ?? this.families;
    }

    public static readonly RegressionFamily[] Models =
    [
        new("Linear", "b0+b1*x", 2),
        new("Quadrático", "b0+b1*x+b2*x^2", 3),
        new("Cúbico", "b0+b1*x+b2*x^2+b3*x^3", 4),
        new("Exponencial", "b0*exp(b1*x)", 2),
        new("Potencial", "b0*x^b1", 2),
        new("Logarítmico", "b0+b1*log(x)", 2),
        new("Schumacher", "exp(b0+b1/x)", 2),
        new("Exponencial modificado", "b0*exp(b1/x)", 2),
        new("Gompertz", "b0*exp(-exp(b1-b2*x))", 3),
        new("Logística", "b0/(1+exp(b1-b2*x))", 3),
        new("Chapman–Richards", "b0*(1-exp(-b1*x))^b2", 3),
        new("Weibull", "b0*(1-exp(-b1*x^b2))", 3),
    ];

    public ChartResult Generate(DataTable table, JsonObject source, int? model = null)
        => Generate(table.Rows[0].Select(value => value?.ToString() ?? "").ToArray(), table.Rows.Skip(1), source, model);

    public ChartResult Generate(string[] columns, IEnumerable<object?[]> rows, JsonObject source, int? model = null)
    {
        var chart = source.DeepClone().AsObject();
        string[] predictors = chart["x_variables"] is JsonArray configuredPredictors
            ? configuredPredictors.Select(n => n?.GetValue<string>() ?? "").Take(3).ToArray()
            : [chart.S("x")];
        if (predictors.Length is < 1 or > 3 || predictors.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Selecione de 1 a 3 variáveis explicativas.");
        int[] xcs = predictors.Select(RequireColumn).ToArray();
        int yc = RequireColumn(chart.S("y")),
            gc = chart.S("stratum").Length > 0 ? RequireColumn(chart.S("stratum")) : -1,
            invalid = 0;
        var excluded = (chart["excluded"] as JsonArray ?? [])
            .Select(n => (int)JsonExtensions.Number(n))
            .ToHashSet();
        var points = new List<ChartPoint>();
        int row = 1;
        foreach (var values in rows)
        {
            string group = gc >= 0 ? TableCursor.Text(Cell(values, gc)) : "Global";
            if (
                (chart.B("group_selected") || chart.S("group").Length > 0)
                && chart.S("group") != group
            )
            {
                row++;
                continue;
            }
            double x = Number(values, xcs[0]),
                x2 = xcs.Length > 1 ? Number(values, xcs[1]) : double.NaN,
                x3 = xcs.Length > 2 ? Number(values, xcs[2]) : double.NaN,
                y = Number(values, yc);
            if (
                !double.IsFinite(x)
                || (xcs.Length > 1 && !double.IsFinite(x2))
                || (xcs.Length > 2 && !double.IsFinite(x3))
                || !double.IsFinite(y)
                || (chart.B("log_x") && x <= 0)
                || (chart.B("log_y") && y <= 0)
            )
            {
                invalid++;
                row++;
                continue;
            }
            points.Add(new(x, y, row, group, excluded.Contains(row), X2: x2, X3: x3, InputCount: xcs.Length));
            row++;
        }
        if (points.Count == 0)
            throw new InvalidDataException(
                "Não há pares numéricos válidos para X e Y neste estrato/escala."
            );
        var bounds=ChartBounds.Calculate(points.Where(p=>chart.B("show_removed")||!p.Excluded).Select(p=>(p.X,p.Y)),chart);
        _=ChartTicks.Create(bounds.XMin,bounds.XMax,chart.N("x_tick_step"),chart.B("log_x"));
        _=ChartTicks.Create(bounds.YMin,bounds.YMax,chart.N("y_tick_step"),chart.B("log_y"));
        if(chart.S("stratum").Length>0)
        {
            var colors=chart["group_colors"]?.DeepClone().AsObject()??new JsonObject();
            var groups=points.Select(p=>p.Group).Distinct().Order(StringComparer.Ordinal).ToArray();
            var used=groups.Where(g=>colors[g]!=null).Select(g=>colors.S(g)).ToHashSet(StringComparer.OrdinalIgnoreCase);int next=0;
            foreach(string group in groups)if(colors[group]==null){string candidate;do{candidate=ChartPalette.At(next++);}while(!used.Add(candidate));colors[group]=candidate;}
            chart["group_colors"]=colors;
        }
        string signature = Signature(points);
        if (chart.S("fit_signature") != signature)
            foreach (
                string key in new[]
                {
                    "fits",
                    "frontier_points",
                    "fit_signature",
                    "selected_fit",
                    "selected_fits",
                }
            )
                chart.Remove(key);
        if (model is not null)
        {
            chart.Remove("selected_fit");
            chart.Remove("selected_fits");
            if (model < -2 || model >= families.Count)
                throw new InvalidDataException("Selecione um modelo válido.");
            int k = (int)chart.N("parameters", 3);
            if (k is < 1 or > 4)
                throw new InvalidDataException("Declare de 1 a 4 coeficientes.");
            bool custom = model == -1;
            if (custom)
                _ = new Formula(
                    chart.S("formula"),
                    Enumerable.Range(0, k).Select(i => "b" + i).Concat(["x", "x1", "x2", "x3", "t"])
                );
            var fits = new List<JsonObject>();
            var envelope = new JsonArray();
            int[]? predictorFilter = chart["fit_predictors"] is JsonArray configuredCounts
                ? configuredCounts.Select(n => (int)JsonExtensions.Number(n)).Where(n => n is >= 1 and <= 3).Distinct().ToArray()
                : null;
            var comparisonFamilies = model == -2 && predictorFilter is { Length: > 0 }
                ? families.Where(f => predictorFilter.Contains(f.Predictors)).ToArray()
                : families.ToArray();
            if (model == -2 && chart["fit_model_ids"] is JsonArray selectedModelIds)
            {
                var ids = selectedModelIds.Select(n => n?.GetValue<string>() ?? "")
                    .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (ids.Length == 0)
                    throw new InvalidDataException("Selecione ao menos um modelo para o ajuste em lote.");
                var available = comparisonFamilies.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                if (ids.Any(id => !available.Contains(id)))
                    throw new InvalidDataException("Um dos modelos selecionados não está disponível neste projeto.");
                var selected = ids.ToHashSet(StringComparer.Ordinal);
                comparisonFamilies = comparisonFamilies.Where(f => selected.Contains(f.Id)).ToArray();
            }
            if (model == -2 && comparisonFamilies.Length == 0)
                throw new InvalidDataException("Não há modelos cadastrados nos grupos selecionados.");
            var kept = points
                .Where(p => !p.Excluded)
                .OrderBy(p => p.Group, StringComparer.Ordinal)
                .ThenBy(p => p.Row)
                .GroupBy(p => p.Group)
                .ToArray();
            if (kept.Length == 0)
                throw new InvalidDataException("Não há pontos mantidos para ajustar.");
            if (kept.Length > 1000)
                throw new InvalidDataException("Há mais de 1.000 estratos. Selecione um grupo.");
            foreach (var group in kept)
            {
                int frontier = (int)chart.N("frontier");
                int[] sides = frontier switch
                {
                    1 => [1],
                    2 => [2],
                    3 => [1, 2],
                    _ => [0],
                };
                foreach (int side in sides)
                {
                    var data = group.ToArray();
                    if (side != 0)
                    {
                        if (data.Length < 4)
                            continue;
                        int bins = Math.Clamp(
                            Math.Min((int)chart.N("bins", 20), data.Length / 3),
                            4,
                            1000
                        );
                        double lo = data.Min(p => p.X),
                            hi = data.Max(p => p.X);
                        if (!(hi > lo))
                            continue;
                        data = data.GroupBy(p =>
                                Math.Min(bins - 1, (int)((p.X - lo) / (hi - lo) * bins))
                            )
                            .OrderBy(g => g.Key)
                            .Select(g =>
                            {
                                var p = side == 1 ? g.MaxBy(p => p.Y)! : g.MinBy(p => p.Y)!;
                                return p with
                                {
                                    X = lo + (g.Key + .5) * (hi - lo) / bins,
                                    Side = side,
                                };
                            })
                            .ToArray();
                        foreach (var p in data)
                            envelope.Add(
                                new JsonObject
                                {
                                    ["x"] = p.X,
                                    ["y"] = p.Y,
                                    ["group"] = p.Group,
                                    ["side"] = side,
                                }
                            );
                    }
                    if (model == -2)
                    {
                        var completed = new List<(RegressionFamily Family, JsonObject Fit)>();
                        foreach (var definition in comparisonFamilies
                            .OrderBy(f => f.Predictors)
                            .ThenBy(f => f.Parameters)
                            .ThenBy(f => f.Id, StringComparer.Ordinal))
                        {
                            var warmStarts = new List<double[]>();
                            foreach (var previous in completed)
                                if (TryNestedStart(previous.Family, previous.Fit, definition, out var start))
                                {
                                    warmStarts.Add(start);
                                    foreach (double multiplier in new[] { .5, 2d })
                                    {
                                        var ratePerturbation = (double[])start.Clone();
                                        ratePerturbation[1] *= multiplier;
                                        warmStarts.Add(ratePerturbation);
                                    }
                                    AddWarmStartPerturbations(start, previous.Family.Parameters, definition, data, warmStarts);
                                }
                            var fit = Fit(data, definition, side, warmStarts);
                            completed.Add((definition, fit));
                            fits.Add(fit);
                        }
                        if (custom)
                            fits.Add(Fit(data, new("Personalizado", chart.S("formula"), k), side));
                    }
                    else
                    {
                        int index = model.Value;
                        var definition = index < 0
                            ? new RegressionFamily("Personalizado", chart.S("formula"), k,
                                Predictors: (int)chart.N("predictor_count", 1))
                            : families[index];
                        var warmStarts = new List<double[]>();
                        foreach (var parent in warmFamilies
                            .Where(f => f.Predictors < definition.Predictors)
                            .Where(f => FormulasNestAtZero(f, definition)))
                        {
                            var parentFit = Fit(data, parent, side);
                            if (TryNestedStart(parent, parentFit, definition, out var start))
                            {
                                warmStarts.Add(start);
                                AddWarmStartPerturbations(start, parent.Parameters, definition, data, warmStarts);
                            }
                        }
                        fits.Add(Fit(data, definition, side, warmStarts));
                    }
                }
            }
            if (fits.Count == 0)
                throw new InvalidDataException(
                    "Não há pontos suficientes para formar a fronteira."
                );
            chart["fits"] = new JsonArray(
                fits.OrderBy(f => f.S("group"), StringComparer.Ordinal)
                    .ThenBy(f => f.N("frontier"))
                    .ThenByDescending(f => f.B("converged"))
                    .ThenBy(f => f.N("rmse", double.PositiveInfinity))
                    .ThenBy(f => f.S("model"), StringComparer.Ordinal)
                    .Cast<JsonNode?>()
                    .ToArray()
            );
            chart["frontier_points"] = envelope;
            chart["fit_signature"] = signature;
        }
        var curves = (chart["fits"] as JsonArray ?? [])
            .Select(f =>
            {
                if (!f.B("converged"))
                    return Array.Empty<double[]>();
                var formula = new Formula(f.S("formula"), ["b0", "b1", "b2", "b3", "x", "x1", "x2", "x3", "t"]);
                var b = Enumerable.Range(0, 4).Select(i => f.N("b" + i)).ToArray();
                return Enumerable
                    .Range(0, 201)
                    .Select(i =>
                    {
                        double x = f.N("xmin") + (f.N("xmax") - f.N("xmin")) * i / 200;
                        double y = formula.Evaluate(s => s switch
                        {
                            "x" or "x1" or "t" => x,
                            "x2" => f.N("x2_mean"),
                            "x3" => f.N("x3_mean"),
                            _ => b[s[1] - '0'],
                        });
                        return new[] { x, y };
                    })
                    .Where(p => double.IsFinite(p[1]))
                    .ToArray();
            })
            .ToArray();
        return new(chart, points.ToArray(), curves, invalid);

        int RequireColumn(string name)
        {
            int index = Array.FindIndex(columns, column => string.Equals(column, name, StringComparison.Ordinal));
            return index >= 0 ? index : throw new InvalidDataException($"Coluna obrigatória ausente: {name}.");
        }

        static object? Cell(object?[] values, int column) => column >= 0 && column < values.Length ? values[column] : null;

        static double Number(object?[] values, int column)
        {
            object? cell = Cell(values, column);
            if (cell is double d) return d;
            if (cell is int i) return i;
            if (cell is long l) return l;
            return double.TryParse(TableCursor.Text(cell).Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double number) ? number : double.NaN;
        }
    }

    private static string Signature(IEnumerable<ChartPoint> points)
    {
        ulong hash = 14695981039346656037;
        var groupBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Bytes(byte[] data)
        {
            foreach (byte b in data)
            {
                hash ^= b;
                hash *= 1099511628211;
            }
        }
        foreach (var p in points)
        {
            AddLong((ulong)BitConverter.DoubleToInt64Bits(p.X));
            // Preserve the existing signature layout: X is also predictor 1.
            AddLong((ulong)BitConverter.DoubleToInt64Bits(p.X));
            if (p.InputCount > 1) AddLong((ulong)BitConverter.DoubleToInt64Bits(p.X2));
            if (p.InputCount > 2) AddLong((ulong)BitConverter.DoubleToInt64Bits(p.X3));
            AddLong((ulong)BitConverter.DoubleToInt64Bits(p.Y));
            AddLong((uint)p.Row);
            if (!groupBytes.TryGetValue(p.Group, out var encodedGroup))
                groupBytes.Add(p.Group, encodedGroup = Encoding.UTF8.GetBytes(p.Group + "\0"));
            Bytes(encodedGroup);
            hash ^= p.Excluded ? (byte)1 : (byte)0;
            hash *= 1099511628211;
        }
        return hash.ToString("x16");

        void AddLong(ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)value;
                hash *= 1099511628211;
                value >>= 8;
            }
        }
    }

    private static bool TryNestedStart(
        RegressionFamily parent,
        JsonObject parentFit,
        RegressionFamily child,
        out double[] start
    )
    {
        start = new double[4];
        if (!parentFit.B("converged") || parent.Predictors >= child.Predictors
            || parent.Parameters > child.Parameters || parent.Parameters is < 1 or > 4
            || child.Parameters is < 1 or > 4)
            return false;

        string cacheKey = string.Join("\0", parent.Predictors, parent.Parameters, parent.Formula,
            child.Predictors, child.Parameters, child.Formula);
        if (!NestedFormulaCache.GetOrAdd(cacheKey, _ => FormulasNestAtZero(parent, child)))
            return false;

        for (int i = 0; i < parent.Parameters; i++)
            start[i] = parentFit.N("b" + i);
        return true;
    }

    private static bool FormulasNestAtZero(RegressionFamily parent, RegressionFamily child)
    {
        var parentFormula = new Formula(parent.Formula, FormulaSymbols(parent.Parameters));
        var childFormula = new Formula(child.Formula, FormulaSymbols(child.Parameters));
        double[] x1 = [.4, 1.7, 8.9, 37.2],
            x2 = [-2.3, .25, 7.8, 41.6],
            x3 = [.15, 1.4, 9.1, 30.7];
        for (int probe = 0; probe < x1.Length; probe++)
        {
            var coefficients = new double[4];
            coefficients[0] = 1.3 + .7 * probe;
            coefficients[1] = .07 + .11 * probe;
            coefficients[2] = .55 + .35 * probe;
            coefficients[3] = 0;
            double oldValue,
                newValue;
            try
            {
                oldValue = Evaluate(parentFormula, coefficients, x1[probe], x2[probe], x3[probe]);
                newValue = Evaluate(childFormula, coefficients, x1[probe], x2[probe], x3[probe]);
            }
            catch (Exception ex) when (ex is ArithmeticException or InvalidDataException)
            {
                return false;
            }
            if (!double.IsFinite(oldValue) || !double.IsFinite(newValue)
                || Math.Abs(oldValue - newValue) > 1e-10 * Math.Max(1, Math.Abs(oldValue)))
                return false;
        }
        return true;
    }

    private static string[] FormulaSymbols(int parameters) =>
        [.. Enumerable.Range(0, parameters).Select(i => "b" + i), "x", "x1", "x2", "x3", "t"];

    private static double Evaluate(Formula formula, double[] b, double x1, double x2, double x3) =>
        formula.Evaluate(symbol => symbol switch
        {
            "x" or "x1" or "t" => x1,
            "x2" => x2,
            "x3" => x3,
            _ => b[symbol[1] - '0'],
        });

    private static void AddWarmStartPerturbations(
        double[] start,
        int parentParameters,
        RegressionFamily child,
        ChartPoint[] points,
        List<double[]> starts
    )
    {
        double scale = 0;
        for (int predictor = 1; predictor < child.Predictors; predictor++)
            foreach (var point in points)
                scale = Math.Max(scale, Math.Abs(point.Input(predictor)));
        scale = scale > 0 && double.IsFinite(scale) ? 1 / scale : 1;
        for (int parameter = parentParameters; parameter < child.Parameters; parameter++)
        {
            foreach (double sign in new[] { -1d, 1d })
            {
                var perturbed = (double[])start.Clone();
                perturbed[parameter] = sign * scale;
                starts.Add(perturbed);
            }
        }
    }

    private static double[][] StartingPoints(
        int model,
        int parameters,
        double xmin,
        double xmax,
        double ymin,
        double ymax,
        double mean,
        IReadOnlyList<double[]>? warmStarts
    )
    {
        var starts = new List<double[]>();
        void Add(double[] values)
        {
            if (values.Length == 4 && values.All(double.IsFinite)
                && !starts.Any(existing => existing.SequenceEqual(values)))
                starts.Add((double[])values.Clone());
        }

        double scale = Math.Max(Math.Abs(xmax), 1);
        for (int seed = 0; seed < 5; seed++)
        {
            double[] b = [mean, (ymax - ymin) / (xmax - xmin), .01, 0];
            switch (model)
            {
                case 3:
                    b[0] = Math.Max(mean, .001);
                    b[1] = .1 / scale;
                    break;
                case 4:
                    b[0] = Math.Max(mean, .001) / Math.Pow(Math.Max(xmax, 1), .5);
                    b[1] = .5;
                    break;
                case 6:
                    b[0] = Math.Log(Math.Max(mean, .001));
                    b[1] = -scale * .3;
                    break;
                case 7:
                    b[0] = Math.Max(ymax, .001);
                    b[1] = -scale * .3;
                    break;
                case 8:
                case 9:
                    b[0] = Math.Max(ymax, 1) * 1.2;
                    b[1] = 1;
                    b[2] = 2 / scale;
                    break;
                case 10:
                case 11:
                    b[0] = Math.Max(ymax, 1) * 1.2;
                    b[1] = 1 / scale;
                    b[2] = 1.5;
                    break;
            }
            double multiplier = seed switch { 0 => 1, 1 => .1, 2 => 10, 3 => -1, _ => 2 };
            for (int j = 0; j < parameters; j++) b[j] *= multiplier;
            Add(b);
        }

        foreach (var warm in warmStarts ?? [])
        {
            if (warm.Length != 4 || warm.Any(value => !double.IsFinite(value))) continue;
            Add(warm);
        }
        return starts.ToArray();
    }

    private static JsonObject Fit(ChartPoint[] points, RegressionFamily definition, int side, IReadOnlyList<double[]>? warmStarts = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string text = definition.Formula;
        int k = definition.Parameters;
        int model = Array.FindIndex(
            Models,
            m => m.Formula == text.Replace("x1", "x", StringComparison.Ordinal) && m.Parameters == k
        );
        int n = points.Length;
        var result = new JsonObject
        {
            ["group"] = points[0].Group,
            ["model"] = definition.Name,
            ["model_id"] = definition.Id,
            ["formula"] = text,
            ["parameters"] = k,
            ["n"] = n,
            ["frontier"] = side,
            ["converged"] = false,
            ["status"] = "Sem convergência",
            ["diagnostic_status"] = "NÃO CONVERGENTE",
            ["algorithm"] = model is >= 0 and <= 2 ? "QR polinomial" : "Levenberg–Marquardt escalado",
            ["termination_reason"] = "sem solução válida",
            ["optimizer_converged"] = false,
            ["starts_attempted"] = 0,
            ["iterations"] = 0,
            ["function_evaluations"] = 0,
            ["objective_evaluations"] = 0,
            ["initial_parameters"] = null,
            ["initial_objective"] = null,
            ["objective"] = null,
            ["elapsed_ms"] = 0,
            ["diagnostics"] = new JsonArray(),
        };
        foreach (string key in new[] { "r2", "rmse", "aic", "bic", "adj" })
            result[key] = null;
        for (int i = 0; i < 4; i++)
            result["b" + i] = 0;
        if (n <= k)
        {
            result["status"] = "Observações insuficientes";
            result["diagnostic_status"] = "OBSERVAÇÕES INSUFICIENTES";
            result["termination_reason"] = "n <= parâmetros";
            timer.Stop();
            result["elapsed_ms"] = timer.ElapsedMilliseconds;
            return result;
        }
        double xmin = points.Min(p => p.X),
            xmax = points.Max(p => p.X),
            ymin = points.Min(p => p.Y),
            ymax = points.Max(p => p.Y),
            mean = 0,
            sst = 0;
        for (int i = 0; i < n; i++)
        {
            double d = points[i].Y - mean;
            mean += d / (i + 1);
            sst += d * (points[i].Y - mean);
        }
        result["xmin"] = xmin;
        result["xmax"] = xmax;
        int predictorCount = definition.Predictors;
        result["predictor_count"] = predictorCount;
        for (int i = 1; i < predictorCount; i++)
            result[$"x{i + 1}_mean"] = points.Average(p => p.Input(i));
        if (!(xmax > xmin))
        {
            result["status"] = "X sem variação";
            result["diagnostic_status"] = "PREDITOR SEM VARIAÇÃO";
            result["termination_reason"] = "x1 sem variação";
            timer.Stop();
            result["elapsed_ms"] = timer.ElapsedMilliseconds;
            return result;
        }
        var formula = new Formula(text, ["b0", "b1", "b2", "b3", "x", "x1", "x2", "x3", "t"]);
        long functionEvaluations = 0,
            objectiveEvaluations = 0,
            iterations = 0;
        double initialObjective = double.PositiveInfinity;
        double[]? initialParameters = null;
        double Predict(double[] b, ChartPoint point)
        {
            functionEvaluations++;
            return formula.Evaluate(s => s switch
            {
                "x" or "x1" or "t" => point.X,
                "x2" => point.Input(1),
                "x3" => point.Input(2),
                _ => b[s[1] - '0'],
            });
        }
        double Score(double[] b)
        {
            objectiveEvaluations++;
            double sum = 0;
            foreach (var p in points)
            {
                double y = Predict(b, p),
                    r = p.Y - y;
                if (!double.IsFinite(y) || !double.IsFinite(r) || Math.Abs(r) > 1e150)
                    return double.PositiveInfinity;
                sum += r * r;
            }
            return double.IsFinite(sum) ? sum : double.PositiveInfinity;
        }
        double best = double.PositiveInfinity;
        double[] bestB = new double[4];
        bool success = false,
            optimizerConverged = false,
            preservedNestedStart = false;
        if (model is >= 0 and <= 2)
        {
            double center = xmin + (xmax - xmin) / 2,
                span = (xmax - xmin) / 2;
            var q = new double[n, k];
            for (int i = 0; i < n; i++)
            {
                double z = (points[i].X - center) / span,
                    v = 1;
                for (int j = 0; j < k; j++)
                {
                    q[i, j] = v;
                    v *= z;
                }
            }
            var a = new double[k, k];
            var rhs = new double[k];
            var coef = new double[k];
            bool rank = true;
            for (int col = 0; col < k; col++)
            {
                for (int prev = 0; prev < col; prev++)
                {
                    double dot = 0;
                    for (int i = 0; i < n; i++)
                        dot += q[i, prev] * q[i, col];
                    a[prev, col] = dot;
                    for (int i = 0; i < n; i++)
                        q[i, col] -= dot * q[i, prev];
                }
                double norm = 0;
                for (int i = 0; i < n; i++)
                    norm = double.Hypot(norm, q[i, col]);
                if (norm < 1e-12)
                {
                    rank = false;
                    break;
                }
                a[col, col] = norm;
                for (int i = 0; i < n; i++)
                {
                    q[i, col] /= norm;
                    rhs[col] += q[i, col] * points[i].Y;
                }
            }
            if (rank)
            {
                for (int col = k - 1; col >= 0; col--)
                {
                    double v = rhs[col];
                    for (int j = col + 1; j < k; j++)
                        v -= a[col, j] * coef[j];
                    coef[col] = v / a[col, col];
                }
                int[,] binom =
                {
                    { 1, 0, 0, 0 },
                    { 1, 1, 0, 0 },
                    { 1, 2, 1, 0 },
                    { 1, 3, 3, 1 },
                };
                for (int j = 0; j < k; j++)
                for (int h = 0; h <= j; h++)
                    bestB[h] +=
                        coef[j] * binom[j, h] * Math.Pow(-center, j - h) / Math.Pow(span, j);
                best = Score(bestB);
                success = double.IsFinite(best);
                optimizerConverged = success;
                result["termination_reason"] = success ? "solução polinomial por QR" : "decomposição QR inválida";
            }
        }
        else
        {
            foreach (var warm in warmStarts ?? [])
            {
                double candidate = Score(warm);
                if (candidate < initialObjective)
                {
                    initialObjective = candidate;
                    initialParameters = (double[])warm.Clone();
                }
                if (double.IsFinite(candidate) && candidate < best)
                {
                    best = candidate;
                    bestB = (double[])warm.Clone();
                    success = true;
                    preservedNestedStart = true;
                }
            }
            string bestTerminationReason = preservedNestedStart ? "candidato aninhado disponível" : "sem convergência";
            var starts = StartingPoints(model, k, xmin, xmax, ymin, ymax, mean, warmStarts);
            result["starts_attempted"] = starts.Length;
            foreach (var startingPoint in starts)
            {
                double[] b = (double[])startingPoint.Clone();
                double score = Score(b),
                    lambda = .001;
                if (score < initialObjective)
                {
                    initialObjective = score;
                    initialParameters = (double[])b.Clone();
                }
                bool converged = false;
                string terminationReason = "limite de iterações";
                for (int iteration = 0; iteration < 100 && double.IsFinite(score); iteration++)
                {
                    iterations++;
                    var a = new double[k, k];
                    var rhs = new double[k];
                    bool valid = true;
                    foreach (var p in points)
                    {
                        double baseline = Predict(b, p),
                            res = p.Y - baseline;
                        var jac = new double[k];
                        for (int j = 0; j < k; j++)
                        {
                            double original = b[j],
                                delta = 1e-5 * Math.Max(1, Math.Abs(original));
                            b[j] = original + delta;
                            double up = Predict(b, p);
                            b[j] = original - delta;
                            double down = Predict(b, p);
                            b[j] = original;
                            jac[j] = (up - down) / (2 * delta);
                            if (!double.IsFinite(jac[j]))
                            {
                                valid = false;
                                break;
                            }
                        }
                        if (!valid)
                            break;
                        for (int j = 0; j < k; j++)
                        {
                            rhs[j] += jac[j] * res;
                            for (int h = 0; h < k; h++)
                                a[j, h] += jac[j] * jac[h];
                        }
                    }
                    if (!valid)
                    {
                        terminationReason = "jacobiana não finita";
                        break;
                    }
                    var columnScale = new double[k];
                    double gradient = 0;
                    for (int j = 0; j < k; j++)
                    {
                        columnScale[j] = double.IsFinite(a[j, j]) && a[j, j] > 1e-24
                            ? Math.Sqrt(a[j, j]) : 1;
                        gradient = Math.Max(gradient, Math.Abs(rhs[j] / columnScale[j]));
                    }
                    for (int j = 0; j < k; j++)
                    {
                        rhs[j] /= columnScale[j];
                        for (int h = 0; h < k; h++)
                            a[j, h] /= columnScale[j] * columnScale[h];
                        a[j, j] += lambda;
                    }
                    if (gradient < 1e-8 * Math.Max(1, Math.Sqrt(score)))
                    {
                        converged = true;
                        terminationReason = "tolerância de gradiente escalado";
                        break;
                    }
                    if (!InventoryMath.Solve(a, rhs, out var step))
                    {
                        lambda *= 10;
                        if (lambda > 1e15)
                        {
                            terminationReason = "sistema normal singular ou mal condicionado";
                            break;
                        }
                        continue;
                    }
                    var trial = (double[])b.Clone();
                    double relative = 0;
                    for (int j = 0; j < k; j++)
                    {
                        double parameterStep = step[j] / columnScale[j];
                        trial[j] += parameterStep;
                        relative = Math.Max(
                            relative,
                            Math.Abs(step[j]) / Math.Max(1, Math.Abs(b[j] * columnScale[j]))
                        );
                    }
                    double next = Score(trial);
                    if (next < score)
                    {
                        double improvement = score - next;
                        b = trial;
                        score = next;
                        lambda = Math.Max(lambda * .3, 1e-12);
                        if (relative < 1e-8 || improvement < 1e-12 * Math.Max(1, score))
                        {
                            converged = true;
                            terminationReason = relative < 1e-8 ? "tolerância de passo" : "tolerância de melhoria";
                            break;
                        }
                    }
                    else
                    {
                        lambda *= 10;
                        if (relative < 1e-10)
                        {
                            converged = true;
                            terminationReason = "passo pequeno sem melhoria";
                            break;
                        }
                        if (lambda > 1e15)
                        {
                            terminationReason = "amortecimento máximo";
                            break;
                        }
                    }
                }
                if (converged && score < best)
                {
                    best = score;
                    bestB = b;
                    success = true;
                    optimizerConverged = true;
                    preservedNestedStart = false;
                    bestTerminationReason = terminationReason;
                }
            }
            result["termination_reason"] = optimizerConverged ? bestTerminationReason
                : preservedNestedStart ? "candidato aninhado preservado; refinamento não melhorou o objetivo"
                : bestTerminationReason;
        }
        result["optimizer_converged"] = optimizerConverged;
        result["iterations"] = iterations;
        result["function_evaluations"] = functionEvaluations;
        result["objective_evaluations"] = objectiveEvaluations;
        result["initial_parameters"] = initialParameters == null ? null
            : new JsonArray(initialParameters.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        result["initial_objective"] = double.IsFinite(initialObjective) ? initialObjective : null;
        result["objective"] = double.IsFinite(best) ? best : null;
        result["elapsed_ms"] = timer.ElapsedMilliseconds;
        if (!double.IsFinite(initialObjective) && double.IsFinite(best))
            result["initial_objective"] = best;
        if (success)
        {
            result["converged"] = true;
            result["status"] = optimizerConverged ? "OK" : "Candidato aninhado preservado";
            for (int i = 0; i < 4; i++)
                result["b" + i] = bestB[i];
            result["rmse"] = Math.Sqrt(best / n);
            result["r2"] = JsonExtensions.Finite(sst > 0 ? 1 - best / sst : double.NaN);
            result["adj"] = JsonExtensions.Finite(
                sst > 0 ? 1 - best / sst * (n - 1) / (n - k) : double.NaN
            );
            double ll = n * Math.Log(Math.Max(best / n, 2.2250738585072014e-308));
            result["aic"] = ll + 2 * k;
            result["bic"] = ll + k * Math.Log(n);
            double predictedMean = 0,
                predictedSst = 0,
                predictedMin = double.PositiveInfinity,
                predictedMax = double.NegativeInfinity;
            int predictedCount = 0;
            foreach (var point in points)
            {
                double predicted = Predict(bestB, point);
                predictedMin = Math.Min(predictedMin, predicted);
                predictedMax = Math.Max(predictedMax, predicted);
                double delta = predicted - predictedMean;
                predictedMean += delta / ++predictedCount;
                predictedSst += delta * (predicted - predictedMean);
            }
            bool degenerate = sst > 0 && predictedCount > 0
                && Math.Sqrt(predictedSst / predictedCount) <= Math.Sqrt(sst / n) * 1e-6
                && predictedMax - predictedMin <= Math.Sqrt(sst / n) * 1e-5;
            var diagnosticMessages = new JsonArray();
            if (preservedNestedStart)
                diagnosticMessages.Add("Uma solução aninhada válida foi preservada; as tentativas não a melhoraram.");
            if (degenerate)
                diagnosticMessages.Add("As predições são praticamente constantes em relação à variação observada.");
            result["diagnostics"] = diagnosticMessages;
            result["diagnostic_status"] = degenerate ? "SOLUÇÃO DEGENERADA"
                : preservedNestedStart ? "SOLUÇÃO ANINHADA PRESERVADA"
                : optimizerConverged ? "CONVERGENTE" : "CONVERGÊNCIA SUSPEITA";
            if (degenerate)
                result["termination_reason"] = "predições praticamente constantes";
        }
        timer.Stop();
        result["elapsed_ms"] = timer.ElapsedMilliseconds;
        return result;
    }

    public static JsonObject ToJson(ChartResult result)
    {
        var output = new JsonObject
        {
            ["chart"] = result.Chart.DeepClone(),
            ["curves"] = new JsonArray(
                result
                    .Curves.Select(line =>
                        (JsonNode?)
                            new JsonArray(
                                line.Select(p => (JsonNode?)new JsonArray(p[0], p[1])).ToArray()
                            )
                    )
                    .ToArray()
            ),
            ["invalid"] = result.Invalid,
        };
        if (result.Points.Length <= 30_000)
        {
            output["points"] = new JsonArray(result.Points.Select(point => (JsonNode?)new JsonObject
            {
                ["x"] = point.X,
                ["y"] = point.Y,
                ["row"] = point.Row,
                ["group"] = point.Group,
                ["excluded"] = point.Excluded,
            }).ToArray());
            return output;
        }

        string[] groups = result.Points.Select(point => point.Group).Distinct(StringComparer.Ordinal).ToArray();
        var groupIds = groups.Select((name, id) => (name, id)).ToDictionary(item => item.name, item => item.id, StringComparer.Ordinal);
        var visibleGroups = new HashSet<string>(StringComparer.Ordinal);
        var distinctX = new HashSet<double>();
        int kept = 0;
        byte[] packed = new byte[checked(result.Points.Length * ChartPointView.PackedRecordBytes)];
        for (int index = 0; index < result.Points.Length; index++)
        {
            int offset = index * ChartPointView.PackedRecordBytes;
            var point = result.Points[index];
            BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(offset, 4), point.Row);
            BinaryPrimitives.WriteInt64LittleEndian(packed.AsSpan(offset + 4, 8), BitConverter.DoubleToInt64Bits(point.X));
            BinaryPrimitives.WriteInt64LittleEndian(packed.AsSpan(offset + 12, 8), BitConverter.DoubleToInt64Bits(point.Y));
            BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(offset + 20, 4), groupIds[point.Group]);
            if (!point.Excluded)
            {
                kept++;
                visibleGroups.Add(point.Group);
                if (distinctX.Count < 2) distinctX.Add(Math.Round(point.X, 10));
            }
        }
        output["point_format"] = ChartPointView.PackedFormat;
        output["point_count"] = result.Points.Length;
        output["point_kept_count"] = kept;
        output["point_distinct_x"] = distinctX.Count;
        output["point_groups"] = new JsonArray(groups.Select(group => (JsonNode?)JsonValue.Create(group)).ToArray());
        output["point_groups_visible"] = new JsonArray(visibleGroups.Order(StringComparer.Ordinal).Select(group => (JsonNode?)JsonValue.Create(group)).ToArray());
        output["point_blob"] = Convert.ToBase64String(packed);
        return output;
    }
}
