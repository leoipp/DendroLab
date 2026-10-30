using System.Text.Json.Nodes;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Statistics;

namespace Dendro.Core.Regression;

public enum FrontierMethod { Extreme, Percentile, TailMean }
public enum FrontierPartition { EqualWidth, EqualCount }
public sealed record FrontierOptions(FrontierMethod Method = FrontierMethod.Extreme,
    FrontierPartition Partition = FrontierPartition.EqualWidth, int Bins = 20,
    int MinimumPerBin = 3, int Side = 1, double Percentile = 90, double TailPercent = 10,
    bool ShowObservations = true);

/// <summary>Empirical envelopes by X intervals. This is not stochastic frontier analysis or DEA.</summary>
public static class FrontierAnalysis
{
    public static AnalysisReport Run(DataTable table, JsonObject source, FrontierOptions options)
    {
        if (!Enum.IsDefined(options.Method) || !Enum.IsDefined(options.Partition)
            || options.Bins is < 1 or > 1000 || options.MinimumPerBin < 1
            || options.Side is < 1 or > 3 || !double.IsFinite(options.Percentile)
            || options.Percentile is < 50 or > 100 || !double.IsFinite(options.TailPercent)
            || options.TailPercent is <= 0 or > 50)
            throw new InvalidDataException("Faixas: 1–1.000; mínimo por faixa ≥ 1; percentil: 50–100; cauda: > 0 até 50%.");
        var config = source.DeepClone().AsObject();
        foreach (string key in new[] { "fits", "fit_signature", "frontier_points", "selected_fit", "selected_fits" })
            config.Remove(key);
        config["frontier"] = 0;
        var original = new RegressionEngine().Generate(table, config);
        config = original.Chart;
        var details = new DataTable(["Estrato", "Fronteira", "Faixa", "X mínimo", "X máximo", "N", "X", "Y", "Linha de origem"]);
        var markers = new JsonArray();
        var fits = new JsonArray();
        var curves = new List<double[][]>();
        int skipped = 0;
        var groups = original.Points.Where(p => !p.Excluded).GroupBy(p => p.Group).ToArray();
        if (groups.Length > 1000)
            throw new InvalidDataException("Selecione até 1.000 estratos.");
        foreach (var group in groups)
        {
            var sorted = group.OrderBy(p => p.X).ThenBy(p => p.Row).ToArray();
            double lo = sorted[0].X, hi = sorted[^1].X;
            var bins = sorted.Select((p, i) => (Point: p, Bin: options.Partition == FrontierPartition.EqualCount
                ? (int)((long)i * options.Bins / sorted.Length)
                : hi == lo ? 0 : Math.Min(options.Bins - 1, (int)((p.X - lo) / (hi - lo) * options.Bins))))
                .GroupBy(p => p.Bin).OrderBy(b => b.Key).ToArray();
            skipped += options.Bins - bins.Length;
            var accepted = bins.Where(b => b.Count() >= options.MinimumPerBin).ToArray();
            skipped += bins.Length - accepted.Length;
            foreach (int side in options.Side == 3 ? new[] { 1, 2 } : new[] { options.Side })
            {
                var line = new List<double[]>();
                foreach (var bin in accepted)
                {
                    var points = bin.Select(p => p.Point).OrderBy(p => p.Y).ThenBy(p => p.Row).ToArray();
                    double x, y;
                    int row = -1;
                    if (options.Method == FrontierMethod.Extreme)
                    {
                        var point = side == 1 ? points[^1] : points[0];
                        x = point.X; y = point.Y; row = point.Row + (int)source.N("header_row", 1);
                    }
                    else if (options.Method == FrontierMethod.Percentile)
                    {
                        double rank = (points.Length - 1) * (side == 1 ? options.Percentile : 100 - options.Percentile) / 100;
                        int lower = (int)Math.Floor(rank), upper = (int)Math.Ceiling(rank);
                        y = points[lower].Y + (rank - lower) * (points[upper].Y - points[lower].Y);
                        x = points.Average(p => p.X);
                    }
                    else
                    {
                        int count = Math.Max(1, (int)Math.Ceiling(points.Length * options.TailPercent / 100));
                        var tail = side == 1 ? points.TakeLast(count).ToArray() : points.Take(count).ToArray();
                        x = tail.Average(p => p.X); y = tail.Average(p => p.Y);
                    }
                    line.Add([x, y]);
                    details.Add(group.Key, side == 1 ? "Superior" : "Inferior", (double)(bin.Key + 1),
                        points.Min(p => p.X), points.Max(p => p.X), (double)points.Length, x, y, row < 0 ? null : (object)(double)row);
                    markers.Add(new JsonObject { ["group"] = group.Key, ["side"] = side, ["x"] = x, ["y"] = y });
                }
                if (line.Count == 0) continue;
                fits.Add(new JsonObject { ["group"] = group.Key, ["model"] = side == 1 ? "Fronteira superior" : "Fronteira inferior",
                    ["formula"] = "0", ["frontier"] = side, ["converged"] = true,
                    ["curve_color"] = side == 1 ? "#138467" : "#D16D2C", ["line_dashed"] = side == 2 });
                curves.Add(line.OrderBy(p => p[0]).ToArray());
            }
        }
        if (markers.Count == 0)
            throw new InvalidDataException("Nenhuma faixa atingiu o mínimo de observações. Reduza as faixas ou o mínimo por faixa.");
        config["fits"] = fits;
        config["frontier_points"] = markers;
        config["title"] = "Fronteira de eficiência · " + source.S("title");
        config["show_removed"] = false;
        var visible = options.ShowObservations ? original.Points.Where(p => !p.Excluded).ToArray()
            : markers.Select((p, i) => new ChartPoint(p.N("x"), p.N("y"), i + 1, p.S("group"))).ToArray();
        return new(new JsonObject {
            ["test"] = "Fronteira de eficiência", ["method"] = options.Method switch {
                FrontierMethod.Extreme => "Máximo/mínimo observado", FrontierMethod.Percentile => "Percentil com interpolação linear",
                _ => "Média dos extremos da cauda" },
            ["n"] = original.Points.Count(p => !p.Excluded), ["groups"] = groups.Length,
            ["points"] = markers.Count, ["skipped_bins"] = skipped,
            ["notes"] = "Envelope empírico por faixas de X, calculado separadamente por estrato. Respeita exclusões, grupo e escalas do gráfico de origem. Não é DEA nem fronteira estocástica. Faixas vazias ou insuficientes são omitidas; linhas ligam os pontos disponíveis.",
        }, details, new ChartResult(config, visible, curves.ToArray(), original.Invalid));
    }
}
