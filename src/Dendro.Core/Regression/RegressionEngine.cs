using System.Text;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Regression;

public sealed record RegressionFamily(string Name, string Formula, int Parameters, string Id = "");

public sealed record ChartPoint(
    double X,
    double Y,
    int Row,
    string Group,
    bool Excluded = false,
    int Side = 0
);

public sealed record ChartResult(
    JsonObject Chart,
    ChartPoint[] Points,
    double[][][] Curves,
    int Invalid
);

public sealed class RegressionEngine
{
    private readonly IReadOnlyList<RegressionFamily> families;

    public RegressionEngine(IReadOnlyList<RegressionFamily>? families = null) =>
        this.families = families ?? Models;

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
    {
        var chart = source.DeepClone().AsObject();
        int xc = table.Require(chart.S("x")),
            yc = table.Require(chart.S("y")),
            gc = chart.S("stratum").Length > 0 ? table.Require(chart.S("stratum")) : -1,
            invalid = 0;
        var excluded = (chart["excluded"] as JsonArray ?? [])
            .Select(n => (int)JsonExtensions.Number(n))
            .ToHashSet();
        var points = new List<ChartPoint>();
        for (int row = 1; row < table.Count; row++)
        {
            string group = gc >= 0 ? table.Text(row, gc) : "Global";
            if (
                (chart.B("group_selected") || chart.S("group").Length > 0)
                && chart.S("group") != group
            )
                continue;
            double x = table.Number(row, xc),
                y = table.Number(row, yc);
            if (
                !double.IsFinite(x)
                || !double.IsFinite(y)
                || (chart.B("log_x") && x <= 0)
                || (chart.B("log_y") && y <= 0)
            )
            {
                invalid++;
                continue;
            }
            points.Add(new(x, y, row, group, excluded.Contains(row)));
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
                    Enumerable.Range(0, k).Select(i => "b" + i).Concat(["x", "x1", "t"])
                );
            var fits = new List<JsonObject>();
            var envelope = new JsonArray();
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
                        foreach (var definition in families)
                            fits.Add(Fit(data, definition, side));
                        if (custom)
                            fits.Add(Fit(data, new("Personalizado", chart.S("formula"), k), side));
                    }
                    else
                    {
                        int index = model.Value;
                        fits.Add(
                            Fit(
                                data,
                                index < 0
                                    ? new RegressionFamily("Personalizado", chart.S("formula"), k)
                                    : families[index],
                                side
                            )
                        );
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
                var formula = new Formula(f.S("formula"), ["b0", "b1", "b2", "b3", "x", "x1", "t"]);
                var b = Enumerable.Range(0, 4).Select(i => f.N("b" + i)).ToArray();
                return Enumerable
                    .Range(0, 201)
                    .Select(i =>
                    {
                        double x = f.N("xmin") + (f.N("xmax") - f.N("xmin")) * i / 200;
                        double y = formula.Evaluate(s =>
                            s is "x" or "x1" or "t" ? x : b[s[1] - '0']
                        );
                        return new[] { x, y };
                    })
                    .Where(p => double.IsFinite(p[1]))
                    .ToArray();
            })
            .ToArray();
        return new(chart, points.ToArray(), curves, invalid);
    }

    private static string Signature(IEnumerable<ChartPoint> points)
    {
        ulong hash = 14695981039346656037;
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
            Bytes(BitConverter.GetBytes(p.X));
            Bytes(BitConverter.GetBytes(p.Y));
            Bytes(BitConverter.GetBytes((ulong)p.Row));
            Bytes(Encoding.UTF8.GetBytes(p.Group + "\0"));
            Bytes([p.Excluded ? (byte)1 : (byte)0]);
        }
        return hash.ToString("x16");
    }

    private static JsonObject Fit(ChartPoint[] points, RegressionFamily definition, int side)
    {
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
        };
        foreach (string key in new[] { "r2", "rmse", "aic", "bic", "adj" })
            result[key] = null;
        for (int i = 0; i < 4; i++)
            result["b" + i] = 0;
        if (n <= k)
        {
            result["status"] = "Observações insuficientes";
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
        if (!(xmax > xmin))
        {
            result["status"] = "X sem variação";
            return result;
        }
        var formula = new Formula(text, ["b0", "b1", "b2", "b3", "x", "x1", "t"]);
        double Predict(double[] b, double x) =>
            formula.Evaluate(s => s is "x" or "x1" or "t" ? x : b[s[1] - '0']);
        double Score(double[] b)
        {
            double sum = 0;
            foreach (var p in points)
            {
                double y = Predict(b, p.X),
                    r = p.Y - y;
                if (!double.IsFinite(y) || !double.IsFinite(r) || Math.Abs(r) > 1e150)
                    return double.PositiveInfinity;
                sum += r * r;
            }
            return sum;
        }
        double best = double.PositiveInfinity;
        double[] bestB = new double[4];
        bool success = false;
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
            }
        }
        else
            for (int seed = 0; seed < 5; seed++)
            {
                double scale = Math.Max(Math.Abs(xmax), 1);
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
                double multiplier = seed switch
                {
                    0 => 1,
                    1 => .1,
                    2 => 10,
                    3 => -1,
                    _ => 2,
                };
                for (int j = 0; j < k; j++)
                    b[j] *= multiplier;
                double score = Score(b),
                    lambda = .001;
                bool converged = false;
                for (int iteration = 0; iteration < 100 && double.IsFinite(score); iteration++)
                {
                    var a = new double[k, k];
                    var rhs = new double[k];
                    bool valid = true;
                    foreach (var p in points)
                    {
                        double baseline = Predict(b, p.X),
                            res = p.Y - baseline;
                        var jac = new double[k];
                        for (int j = 0; j < k; j++)
                        {
                            double original = b[j],
                                delta = 1e-5 * Math.Max(1, Math.Abs(original));
                            b[j] = original + delta;
                            double up = Predict(b, p.X);
                            b[j] = original - delta;
                            double down = Predict(b, p.X);
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
                        break;
                    double gradient = 0;
                    for (int j = 0; j < k; j++)
                    {
                        gradient = Math.Max(gradient, Math.Abs(rhs[j]));
                        a[j, j] += lambda * Math.Max(a[j, j], 1);
                    }
                    if (gradient < 1e-9)
                    {
                        converged = true;
                        break;
                    }
                    if (!InventoryMath.Solve(a, rhs, out var step))
                    {
                        lambda *= 10;
                        if (lambda > 1e15)
                            break;
                        continue;
                    }
                    var trial = (double[])b.Clone();
                    double relative = 0;
                    for (int j = 0; j < k; j++)
                    {
                        trial[j] += step[j];
                        relative = Math.Max(
                            relative,
                            Math.Abs(step[j]) / Math.Max(1, Math.Abs(b[j]))
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
                            break;
                        }
                    }
                    else
                    {
                        lambda *= 10;
                        if (relative < 1e-10)
                        {
                            converged = true;
                            break;
                        }
                        if (lambda > 1e15)
                            break;
                    }
                }
                if (converged && score < best)
                {
                    best = score;
                    bestB = b;
                    success = true;
                }
            }
        if (success)
        {
            result["converged"] = true;
            result["status"] = "OK";
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
        }
        return result;
    }

    public static JsonObject ToJson(ChartResult result) =>
        new()
        {
            ["chart"] = result.Chart.DeepClone(),
            ["points"] = new JsonArray(
                result
                    .Points.Select(p =>
                        (JsonNode?)
                            new JsonObject
                            {
                                ["x"] = p.X,
                                ["y"] = p.Y,
                                ["row"] = p.Row,
                                ["group"] = p.Group,
                                ["excluded"] = p.Excluded,
                            }
                    )
                    .ToArray()
            ),
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
}
