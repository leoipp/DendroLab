using System.Text.Json.Nodes;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Statistics;

public sealed record KMeansOptions(int MinK = 2, int MaxK = 8, int? SelectedK = null,
    int Seed = 42, int MaxIterations = 100, int Restarts = 3);

public sealed record KMeansScore(int K, double Inertia, double Silhouette);

/// <summary>Standardized Euclidean k-means with k-means++ initialization and exact silhouette.</summary>
public static class KMeansClustering
{
    public static AnalysisReport Run(DataTable data, string[] features, string x, string y,
        KMeansOptions options, CancellationToken cancellation = default,
        Action<double, string>? progress = null)
    {
        ValidateOptions(options, features);
        int[] columns = features.Select(data.Require).ToArray();
        int xColumn = data.Require(x), yColumn = data.Require(y);
        if (x == y) throw new InvalidDataException("Selecione duas variáveis diferentes para os eixos do gráfico.");

        var eligibleRows = new List<int>();
        int incomplete = 0;
        for (int row = 1; row < data.Count; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            bool valid = true;
            for (int column = 0; column < columns.Length; column++)
            {
                if (!double.IsFinite(data.Number(row, columns[column]))) { valid = false; break; }
            }
            if (valid) eligibleRows.Add(row); else incomplete++;
        }
        if (eligibleRows.Count < options.MaxK)
            throw new InvalidDataException($"K máximo ({options.MaxK:N0}) não pode ser maior que o número de linhas completas ({eligibleRows.Count:N0}).");

        int dimensions = features.Length;
        var means = new double[dimensions];
        var scales = new double[dimensions];
        foreach (int row in eligibleRows)
            for (int j = 0; j < dimensions; j++) means[j] += data.Number(row, columns[j]) / eligibleRows.Count;
        foreach (int row in eligibleRows)
            for (int j = 0; j < dimensions; j++)
            {
                double delta = data.Number(row, columns[j]) - means[j];
                scales[j] += delta * delta / eligibleRows.Count;
            }
        for (int j = 0; j < dimensions; j++)
        {
            scales[j] = Math.Sqrt(scales[j]);
            if (!double.IsFinite(scales[j]) || scales[j] <= 1e-12)
                throw new InvalidDataException($"A variável '{features[j]}' é constante; remova-a da seleção.");
        }

        double[] Normalize(int row)
        {
            var values = new double[dimensions];
            for (int j = 0; j < dimensions; j++) values[j] = (data.Number(row, columns[j]) - means[j]) / scales[j];
            return values;
        }
        var training = eligibleRows.Select(Normalize).ToArray();
        var scores = new List<KMeansScore>();
        var solutions = new Dictionary<int, (double[][] Centers, int[] Labels)>();
        int total = options.MaxK - options.MinK + 1;
        for (int k = options.MinK; k <= options.MaxK; k++)
        {
            cancellation.ThrowIfCancellationRequested();
            var solution = BestSolution(training, k, options, cancellation);
            double inertia = Inertia(training, solution.Labels, solution.Centers, cancellation);
            double silhouette = Silhouette(training, solution.Labels, k, cancellation);
            scores.Add(new(k, inertia, silhouette));
            solutions[k] = solution;
            progress?.Invoke(10 + 75d * scores.Count / total, $"Avaliando k={k}: inertia {inertia:G5}, silhouette {silhouette:G4}…");
        }

        int selectedK = options.SelectedK ?? scores.OrderByDescending(score => score.Silhouette).ThenBy(score => score.K).First().K;
        if (!solutions.ContainsKey(selectedK)) throw new InvalidDataException("O K final deve estar entre o K mínimo e o K máximo avaliados.");
        var selected = solutions[selectedK];
        var details = data.Copy();
        int clusterColumn = details.Ensure("KMeans_Cluster");
        var points = new List<ChartPoint>();
        int invalidAxes = 0;
        var clusterSizes = new int[selectedK];
        for (int i = 0; i < eligibleRows.Count; i++)
        {
            if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int sourceRow = eligibleRows[i];
            int label = Nearest(Normalize(sourceRow), selected.Centers);
            clusterSizes[label]++;
            string cluster = $"Cluster {label + 1}";
            details.Set(sourceRow, clusterColumn, cluster);
            double px = data.Number(sourceRow, xColumn), py = data.Number(sourceRow, yColumn);
            if (double.IsFinite(px) && double.IsFinite(py)) points.Add(new(px, py, sourceRow + 1, cluster));
            else invalidAxes++;
        }
        var summary = new JsonObject
        {
            ["test"] = "K-means · análise de agrupamentos",
            ["decision"] = $"K final = {selectedK}; maior silhouette no intervalo avaliado: k={scores.OrderByDescending(score => score.Silhouette).ThenBy(score => score.K).First().K}.",
            ["method"] = "K-means com inicialização k-means++, distância euclidiana e padronização z-score. Inertia e silhouette exatas são calculadas com todas as linhas completas.",
            ["notes"] = "A padronização dá peso comparável às variáveis e as médias/desvios são calculados nas linhas completas. O ajuste, a inertia e a silhouette usam todas as linhas completas; linhas incompletas ficam sem grupo. O cálculo exato de silhouette compara todas as distâncias entre observações e cresce quadraticamente com o tamanho da base. O elbow é uma heurística visual; confirme o K pelo gráfico e pelo contexto dos dados. Silhouette maior indica grupos mais coesos e separados, mas não prova que os grupos sejam reais.",
            ["K final"] = selectedK,
            ["Critério automático"] = options.SelectedK == null ? "Maior silhouette média" : "K informado pelo usuário",
            ["Linhas completas"] = eligibleRows.Count,
            ["Linhas incompletas"] = incomplete,
            ["Linhas usadas no ajuste e métricas"] = training.Length,
            ["Linhas usadas no cálculo da silhouette"] = training.Length,
            ["Variáveis padronizadas"] = string.Join("; ", features),
            ["Semente"] = options.Seed,
            ["Iterações máximas"] = options.MaxIterations,
            ["Reinícios por K"] = options.Restarts,
            ["Linhas sem coordenadas no gráfico"] = invalidAxes,
            ["Tamanhos dos grupos"] = string.Join("; ", clusterSizes.Select((count, index) => $"Cluster {index + 1}: {count:N0}")),
            ["Diagnóstico por K"] = new JsonArray(scores.Select(score => (JsonNode?)new JsonObject
            {
                ["k"] = score.K, ["inertia"] = score.Inertia, ["silhouette"] = score.Silhouette,
            }).ToArray()),
        };
        foreach (var score in scores)
        {
            summary[$"Inertia · k={score.K}"] = score.Inertia;
            summary[$"Silhouette · k={score.K}"] = score.Silhouette;
        }
        var plot = new ChartResult(new JsonObject
        {
            ["title"] = $"K-means · k={selectedK}", ["x_label"] = x, ["y_label"] = y,
            ["stratum"] = "KMeans_Cluster", ["point_size"] = 3, ["point_alpha"] = .65,
            ["group_colors"] = new JsonObject
            {
                ["Cluster 1"] = "#21669D", ["Cluster 2"] = "#C33E5E", ["Cluster 3"] = "#2A8C66",
                ["Cluster 4"] = "#C18B24", ["Cluster 5"] = "#7651A8", ["Cluster 6"] = "#258C9A",
                ["Cluster 7"] = "#A35D2A", ["Cluster 8"] = "#5B6E35", ["Cluster 9"] = "#9A4F87",
                ["Cluster 10"] = "#64748B", ["Cluster 11"] = "#A23D3D", ["Cluster 12"] = "#338B46",
            },
        }, points.ToArray(), [], incomplete + invalidAxes);
        var elbow = LinePlot("Método elbow · inertia", "Inertia (soma dos quadrados intragrupo)", scores, score => score.Inertia, "#21669D");
        var silhouettePlot = LinePlot("Silhouette médio por K", "Silhouette médio", scores, score => score.Silhouette, "#1C8965");
        progress?.Invoke(100, $"K-means concluído: {selectedK} grupos em {eligibleRows.Count:N0} linhas completas.");
        return new(summary, details, plot, [elbow, silhouettePlot]);
    }

    private static ChartResult LinePlot(string title, string yLabel, IReadOnlyList<KMeansScore> scores, Func<KMeansScore, double> value, string color)
    {
        var chart = new JsonObject
        {
            ["title"] = title, ["x_label"] = "Número de grupos (k)", ["y_label"] = yLabel,
            // ScatterPlot renders curves through FitDisplay, so each diagnostic line
            // needs a visible fit descriptor even though it has no observed points.
            ["fits"] = new JsonArray(new JsonObject { ["model"] = title, ["converged"] = true, ["curve_color"] = color }),
        };
        var curve = scores.Select(score => new[] { (double)score.K, value(score) }).ToArray();
        var points = scores.Select((score, index) => new ChartPoint(score.K, value(score), index + 1, title)).ToArray();
        return new(chart, points, [curve], 0);
    }

    private static void ValidateOptions(KMeansOptions options, string[] features)
    {
        if (features == null || features.Length is < 2 or > 64 || features.Any(string.IsNullOrWhiteSpace) || features.Distinct(StringComparer.Ordinal).Count() != features.Length)
            throw new InvalidDataException("Selecione de 2 a 64 variáveis numéricas distintas para o K-means.");
        if (options.MinK < 2 || options.MaxK <= options.MinK ||
            options.SelectedK is int selected && (selected < options.MinK || selected > options.MaxK) ||
            options.MaxIterations is < 10 or > 1000 || options.Restarts is < 1 or > 10)
            throw new InvalidDataException("Informe K mínimo a partir de 2 e K máximo maior que K mínimo. O K máximo também deve caber no número de linhas completas.");
    }

    private static (double[][] Centers, int[] Labels) BestSolution(double[][] rows, int k, KMeansOptions options, CancellationToken cancellation)
    {
        (double[][] Centers, int[] Labels)? best = null;
        double bestInertia = double.PositiveInfinity;
        for (int restart = 0; restart < options.Restarts; restart++)
        {
            cancellation.ThrowIfCancellationRequested();
            var centers = Initialize(rows, k, options.Seed + k * 7919 + restart * 104729, cancellation);
            int[] labels = new int[rows.Length];
            for (int iteration = 0; iteration < options.MaxIterations; iteration++)
            {
                cancellation.ThrowIfCancellationRequested();
                bool changed = AssignInto(rows, centers, labels, cancellation);
                var sums = Enumerable.Range(0, k).Select(_ => new double[centers[0].Length]).ToArray();
                var counts = new int[k];
                for (int i = 0; i < rows.Length; i++)
                {
                    int cluster = labels[i]; counts[cluster]++;
                    for (int j = 0; j < rows[i].Length; j++) sums[cluster][j] += rows[i][j];
                }
                for (int cluster = 0; cluster < k; cluster++)
                {
                    if (counts[cluster] == 0) { changed = true; continue; }
                    for (int j = 0; j < sums[cluster].Length; j++) centers[cluster][j] = sums[cluster][j] / counts[cluster];
                }
                if (!changed) break;
            }
            AssignInto(rows, centers, labels, cancellation);
            int[] countsFinal = new int[k]; foreach (int label in labels) countsFinal[label]++;
            if (countsFinal.Any(count => count == 0)) continue;
            double inertia = Inertia(rows, labels, centers, cancellation);
            if (inertia < bestInertia) { bestInertia = inertia; best = (centers.Select(center => (double[])center.Clone()).ToArray(), (int[])labels.Clone()); }
        }
        return best ?? throw new InvalidDataException($"Não foi possível formar {k} grupos distintos. Reduza o K máximo ou verifique variáveis repetidas.");
    }

    private static double[][] Initialize(double[][] rows, int k, int seed, CancellationToken cancellation)
    {
        var random = new Random(seed);
        var centers = new List<double[]>(k) { (double[])rows[random.Next(rows.Length)].Clone() };
        var nearest = Enumerable.Repeat(double.PositiveInfinity, rows.Length).ToArray();
        while (centers.Count < k)
        {
            double total = 0;
            for (int i = 0; i < rows.Length; i++) { if ((i & 2047) == 0) cancellation.ThrowIfCancellationRequested(); nearest[i] = Math.Min(nearest[i], SquaredDistance(rows[i], centers[^1])); total += nearest[i]; }
            int selected = -1;
            if (total > 0 && double.IsFinite(total))
            {
                double threshold = random.NextDouble() * total;
                for (int i = 0; i < rows.Length; i++) if ((threshold -= nearest[i]) <= 0) { selected = i; break; }
            }
            if (selected < 0 || centers.Any(center => SquaredDistance(rows[selected], center) <= 1e-24))
                selected = Enumerable.Range(0, rows.Length).Where(i => centers.All(center => SquaredDistance(rows[i], center) > 1e-24))
                    .OrderByDescending(i => nearest[i]).FirstOrDefault(-1);
            if (selected < 0) break;
            centers.Add((double[])rows[selected].Clone());
        }
        if (centers.Count < k) throw new InvalidDataException($"Os dados têm menos de {k} perfis distintos; reduza o K máximo.");
        return centers.ToArray();
    }

    private static int[] Assign(double[][] rows, double[][] centers, CancellationToken cancellation)
    {
        var labels = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++) { if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested(); labels[i] = Nearest(rows[i], centers); }
        return labels;
    }

    private static bool AssignInto(double[][] rows, double[][] centers, int[] labels, CancellationToken cancellation)
    {
        bool changed = false;
        for (int i = 0; i < rows.Length; i++) { if ((i & 2047) == 0) cancellation.ThrowIfCancellationRequested(); int label = Nearest(rows[i], centers); if (label != labels[i]) { labels[i] = label; changed = true; } }
        return changed;
    }

    private static int Nearest(double[] row, double[][] centers)
    {
        int selected = 0; double best = SquaredDistance(row, centers[0]);
        for (int c = 1; c < centers.Length; c++) { double distance = SquaredDistance(row, centers[c]); if (distance < best) { best = distance; selected = c; } }
        return selected;
    }

    private static double Inertia(double[][] rows, int[] labels, double[][] centers, CancellationToken cancellation)
    {
        double result = 0;
        for (int i = 0; i < rows.Length; i++) { if ((i & 2047) == 0) cancellation.ThrowIfCancellationRequested(); result += SquaredDistance(rows[i], centers[labels[i]]); }
        return result;
    }

    private static double Silhouette(double[][] rows, int[] labels, int k, CancellationToken cancellation)
    {
        var members = Enumerable.Range(0, k).Select(cluster => Enumerable.Range(0, labels.Length).Where(index => labels[index] == cluster).ToArray()).ToArray();
        if (members.Any(cluster => cluster.Length == 0)) return double.NaN;
        double total = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if ((i & 127) == 0) cancellation.ThrowIfCancellationRequested();
            int own = labels[i];
            double a = 0;
            if (members[own].Length > 1)
                foreach (int other in members[own]) if (other != i) a += Math.Sqrt(SquaredDistance(rows[i], rows[other]));
            a = members[own].Length > 1 ? a / (members[own].Length - 1) : 0;
            double b = double.PositiveInfinity;
            for (int cluster = 0; cluster < k; cluster++) if (cluster != own)
            {
                double mean = 0; foreach (int other in members[cluster]) mean += Math.Sqrt(SquaredDistance(rows[i], rows[other]));
                mean /= members[cluster].Length; b = Math.Min(b, mean);
            }
            total += members[own].Length == 1 ? 0 : (b - a) / Math.Max(a, b);
        }
        return total / rows.Length;
    }

    private static double SquaredDistance(double[] first, double[] second)
    {
        double result = 0;
        for (int j = 0; j < first.Length; j++) { double delta = first[j] - second[j]; result += delta * delta; }
        return result;
    }
}
