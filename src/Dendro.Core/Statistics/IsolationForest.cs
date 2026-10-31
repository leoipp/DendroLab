using System.Text.Json.Nodes;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Statistics;

public sealed record IsolationForestOptions(int Trees = 100, int SampleSize = 256, int MaxDepth = 0,
    int MaxFeatures = 0, double Contamination = .02, double? FixedThreshold = null, int Seed = 42, int TrainingLimit = 50000);
public sealed record IsolationNode(int Feature, double Split, int Left, int Right, int Size);
public sealed record IsolationForestModel(int Version, string Name, string[] Features, IsolationForestOptions Options,
    int EffectiveSampleSize, int TrainingRows, int EligibleRows, double Threshold, IsolationNode[][] Trees);

/// <summary>Axis-aligned isolation trees; s(x) = 2^(-mean path length / c(sample size)).</summary>
public static class IsolationForest
{
    public const long TrainingPoolMemoryBudgetBytes = 512L * 1024 * 1024;
    public static long EstimateTrainingPoolBytes(int rows, int dimensions)
    {
        if (rows < 0 || dimensions < 0) throw new ArgumentOutOfRangeException();
        return checked((long)rows * (32L + dimensions * sizeof(double)));
    }

    public static void ValidateTrainingFootprint(int rows, int dimensions)
    {
        if (dimensions is < 1 or > 256)
            throw new InvalidDataException("Selecione de 1 a 256 variáveis numéricas para treinar o modelo.");
        if (EstimateTrainingPoolBytes(rows, dimensions) > TrainingPoolMemoryBudgetBytes)
            throw new InvalidDataException($"A amostra de treinamento excede o limite estimado de memória de {TrainingPoolMemoryBudgetBytes / (1024 * 1024)} MB. Reduza o máximo de linhas ou o número de variáveis.");
    }

    private static readonly double[] Corrections = BuildCorrections();
    private static double[] BuildCorrections()
    {
        var values = new double[4097]; double harmonic = 0;
        for (int size = 2; size < values.Length; size++) { harmonic += 1d / (size - 1); values[size] = 2 * harmonic - 2d * (size - 1) / size; }
        return values;
    }
    private static void ValidateOptions(IsolationForestOptions o, int dimensions)
    {
        if (o.Trees is < 1 or > 500 || o.SampleSize is < 2 or > 4096 || o.MaxDepth is < 0 or > 30 ||
            o.MaxFeatures < 0 || o.MaxFeatures > dimensions || o.TrainingLimit is < 2 or > 500000 ||
            !double.IsFinite(o.Contamination) || o.Contamination is <= 0 or > .5 ||
            (o.FixedThreshold is double threshold && (!double.IsFinite(threshold) || threshold is < 0 or > 1)) ||
            (long)o.Trees * o.SampleSize > 250000)
            throw new InvalidDataException("Parâmetros inválidos. Use 1–500 árvores, 2–4096 amostras/árvore, profundidade 0–30, contaminação > 0 até 50%, limiar 0–1 e até 250.000 amostras somadas entre árvores.");
    }

    public static double ExpectedPath(int size)
    {
        if (size <= 1) return 0;
        return size < Corrections.Length ? Corrections[size] : 2 * (Math.Log(size - 1) + .5772156649015329) - 2d * (size - 1) / size;
    }

    public static IsolationForestModel Fit(DataTable data, string[] features, string name, IsolationForestOptions options,
        CancellationToken cancellation = default, Action<double, string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(name) || features.Length is < 1 or > 256 ||
            features.Distinct(StringComparer.Ordinal).Count() != features.Length)
            throw new InvalidDataException("Informe o nome e de 1 a 256 variáveis numéricas distintas.");
        ValidateOptions(options, features.Length);
        ValidateTrainingFootprint(options.TrainingLimit, features.Length);
        int[] columns = features.Select(data.Require).ToArray();
        var random = new Random(options.Seed);
        var pool = new List<double[]>(); int valid = 0;
        for (int r = 1; r < data.Count; r++)
        {
            cancellation.ThrowIfCancellationRequested();
            double[] values = columns.Select(c => data.Number(r, c)).ToArray();
            if (values.Any(v => !double.IsFinite(v))) continue;
            valid++;
            if (pool.Count < options.TrainingLimit) pool.Add(values);
            else { int at = random.Next(valid); if (at < pool.Count) pool[at] = values; }
        }
        if (pool.Count < 2) throw new InvalidDataException("O treinamento exige pelo menos duas linhas completas com valores numéricos finitos.");
        if (!Enumerable.Range(0, features.Length).Any(c => pool.Any(row => row[c] != pool[0][c])))
            throw new InvalidDataException("Todas as variáveis selecionadas são constantes na amostra de treinamento.");
        int sampleSize = Math.Min(options.SampleSize, pool.Count);
        int depthLimit = options.MaxDepth == 0 ? (int)Math.Ceiling(Math.Log2(sampleSize)) : options.MaxDepth;
        var trees = new IsolationNode[options.Trees][];
        for (int tree = 0; tree < trees.Length; tree++)
        {
            cancellation.ThrowIfCancellationRequested();
            // A sparse partial shuffle samples without replacement without copying the full pool per tree.
            var swaps = new Dictionary<int, int>();
            int[] indices = new int[sampleSize];
            for (int i = 0; i < sampleSize; i++)
            {
                int j = random.Next(i, pool.Count);
                indices[i] = swaps.GetValueOrDefault(j, j);
                swaps[j] = swaps.GetValueOrDefault(i, i);
            }
            int[] dimensions = Enumerable.Range(0, features.Length).ToArray();
            random.Shuffle(dimensions);
            dimensions = dimensions.Take(options.MaxFeatures == 0 ? dimensions.Length : options.MaxFeatures).ToArray();
            var nodes = new List<IsolationNode>();
            int Build(int[] rows, int depth)
            {
                cancellation.ThrowIfCancellationRequested();
                int id = nodes.Count; nodes.Add(new(-1, 0, -1, -1, rows.Length));
                if (rows.Length <= 1 || depth >= depthLimit) return id;
                var varying = new List<(int Feature, double Min, double Max)>();
                foreach (int feature in dimensions)
                {
                    double min = rows.Min(r => pool[r][feature]), max = rows.Max(r => pool[r][feature]);
                    if (max > min) varying.Add((feature, min, max));
                }
                if (varying.Count == 0) return id;
                var choice = varying[random.Next(varying.Count)];
                double u = random.NextDouble();
                double split = choice.Min * (1 - u) + choice.Max * u;
                if (split <= choice.Min) split = Math.BitIncrement(choice.Min);
                split = Math.Min(split, choice.Max);
                int[] left = rows.Where(r => pool[r][choice.Feature] < split).ToArray();
                int[] right = rows.Where(r => pool[r][choice.Feature] >= split).ToArray();
                if (left.Length == 0 || right.Length == 0) return id;
                int a = Build(left, depth + 1), b = Build(right, depth + 1);
                nodes[id] = new(choice.Feature, split, a, b, rows.Length); return id;
            }
            Build(indices, 0); trees[tree] = nodes.ToArray();
            progress?.Invoke(10 + (tree + 1) * 65d / trees.Length, $"Treinando árvore {tree + 1}/{trees.Length}…");
        }
        var model = new IsolationForestModel(1, name.Trim(), features.ToArray(), options, sampleSize, pool.Count, valid, .5, trees);
        double[] scores = pool.Select(row => { cancellation.ThrowIfCancellationRequested(); return Score(model, row); }).Order().ToArray();
        double cutoff = options.FixedThreshold ?? scores[Math.Clamp((int)Math.Ceiling((1 - options.Contamination) * scores.Length) - 1, 0, scores.Length - 1)];
        progress?.Invoke(80, "Modelo treinado. Calculando os escores…");
        return model with { Threshold = cutoff };
    }

    public static void ValidateModel(IsolationForestModel model)
    {
        if (model.Version != 1 || string.IsNullOrWhiteSpace(model.Name) || model.Features == null || model.Features.Length is < 1 or > 256 ||
            model.Features.Any(string.IsNullOrWhiteSpace) || model.Features.Distinct(StringComparer.Ordinal).Count() != model.Features.Length ||
            model.Options == null || model.Trees == null || model.Trees.Length != model.Options.Trees ||
            model.EffectiveSampleSize is < 2 or > 4096 || model.EffectiveSampleSize > model.Options.SampleSize || model.TrainingRows < model.EffectiveSampleSize ||
            model.TrainingRows > model.Options.TrainingLimit || model.EligibleRows < model.TrainingRows ||
            !double.IsFinite(model.Threshold) || model.Threshold is < 0 or > 1)
            throw new InvalidDataException("Modelo Isolation Forest inválido ou incompatível.");
        ValidateOptions(model.Options, model.Features.Length);
        foreach (var tree in model.Trees)
        {
            if (tree == null || tree.Length == 0 || tree.Length > 2 * model.EffectiveSampleSize - 1 || tree[0] == null || tree[0].Size != model.EffectiveSampleSize)
                throw new InvalidDataException("Árvore de isolamento inválida.");
            var parents = new int[tree.Length]; var depths = new int[tree.Length];
            for (int i = 0; i < tree.Length; i++)
            {
                var n = tree[i];
                if (n == null || n.Size < 1 || depths[i] > 30 || !double.IsFinite(n.Split)) throw new InvalidDataException("Nó inválido no modelo.");
                if (n.Feature == -1) { if (n.Left != -1 || n.Right != -1) throw new InvalidDataException("Folha inválida."); continue; }
                if (n.Feature < 0 || n.Feature >= model.Features.Length || n.Left <= i || n.Right <= i || n.Left == n.Right || n.Left >= tree.Length || n.Right >= tree.Length)
                    throw new InvalidDataException("Divisão inválida na árvore.");
                foreach (int child in new[] { n.Left, n.Right })
                {
                    if (++parents[child] != 1) throw new InvalidDataException("Árvore com vínculos repetidos.");
                    depths[child] = depths[i] + 1;
                }
                if (tree[n.Left] == null || tree[n.Right] == null || tree[n.Left].Size + tree[n.Right].Size != n.Size) throw new InvalidDataException("Contagem inválida de amostras na árvore.");
            }
            if (parents.Skip(1).Any(p => p != 1)) throw new InvalidDataException("Nó sem vínculo na árvore.");
        }
    }

    public static double Score(IsolationForestModel model, double[] values)
    {
        if (values.Length != model.Features.Length || values.Any(v => !double.IsFinite(v))) return double.NaN;
        double paths = 0;
        foreach (var tree in model.Trees)
        {
            int at = 0, depth = 0;
            while (tree[at].Feature >= 0) { var node = tree[at]; at = values[node.Feature] < node.Split ? node.Left : node.Right; depth++; }
            paths += depth + ExpectedPath(tree[at].Size);
        }
        return Math.Pow(2, -paths / model.Trees.Length / ExpectedPath(model.EffectiveSampleSize));
    }

    public static AnalysisReport Report(DataTable data, IsolationForestModel model, string x, string y,
        CancellationToken cancellation = default, Action<double, string>? progress = null)
    {
        ValidateModel(model);
        int[] columns = model.Features.Select(data.Require).ToArray();
        int X = x is "$row" or "$score" ? -1 : data.Require(x), Y = y is "$row" or "$score" ? -1 : data.Require(y);
        var details = data.Copy();
        int scoreColumn = details.Ensure("IF_Score"), classColumn = details.Ensure("IF_Classificacao");
        var points = new List<ChartPoint>(); int invalid = 0, anomalies = 0, missingAxes = 0;
        for (int row = 1; row < data.Count; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            double score = Score(model, columns.Select(c => data.Number(row, c)).ToArray());
            bool valid = double.IsFinite(score), anomaly = valid && score > model.Threshold;
            string label = valid ? anomaly ? "Anomalia" : "Normal" : "Incompleta";
            if (!valid) invalid++; if (anomaly) anomalies++;
            details.Set(row, scoreColumn, valid ? score : null); details.Set(row, classColumn, label);
            double Axis(string symbol, int column) => symbol == "$score" ? score : symbol == "$row" ? row + 1 : data.Number(row, column);
            double a = Axis(x, X), b = Axis(y, Y);
            if (valid && double.IsFinite(a) && double.IsFinite(b)) points.Add(new(a, b, row + 1, label));
            else if (valid) missingAxes++;
            if (row % 1000 == 0) progress?.Invoke(80 + row * 18d / Math.Max(1, data.Count - 1), $"Avaliando linha {row:N0}/{data.Count - 1:N0}…");
        }
        var chart = new JsonObject { ["title"] = "Isolation Forest · " + model.Name, ["x_label"] = x == "$row" ? "Linha de origem" : x == "$score" ? "Escore de anomalia" : x,
            ["y_label"] = y == "$row" ? "Linha de origem" : y == "$score" ? "Escore de anomalia" : y,
            ["stratum"] = "IF_Classificacao", ["point_size"] = 3, ["point_alpha"] = .55,
            ["group_colors"] = new JsonObject { ["Normal"] = "#21669D", ["Anomalia"] = "#C33E5E" } };
        var summary = new JsonObject {
            ["test"] = "Isolation Forest · " + model.Name,
            ["decision"] = $"{anomalies:N0} linha(s) sinalizada(s) entre {data.Count - 1 - invalid:N0} linhas válidas.",
            ["method"] = "Árvores de isolamento com cortes aleatórios e subamostras sem reposição. Escore = 2^(−caminho médio/c(n)). Anomalia quando escore > limiar salvo no treinamento.",
            ["notes"] = "Escore alto indica maior isolamento; não é probabilidade de erro nem comprova defeito. A contaminação define o quantil de referência, não uma taxa real conhecida; empates no limiar não são sinalizados. Aplique o mesmo modelo/limiar em novas bases para comparabilidade. Linhas incompletas ficam sem escore. Nenhuma observação é excluída automaticamente.",
            ["Modelo"] = model.Name, ["Variáveis"] = string.Join("; ", model.Features), ["Árvores"] = model.Trees.Length,
            ["Amostras por árvore (efetivas)"] = model.EffectiveSampleSize, ["Linhas completas na base de treinamento"] = model.EligibleRows,
            ["Linhas na amostra de treinamento"] = model.TrainingRows, ["Limiar salvo"] = model.Threshold,
            ["Contaminação de referência (%)"] = model.Options.Contamination * 100, ["Limiar manual"] = model.Options.FixedThreshold != null,
            ["Profundidade máxima (0 = automática)"] = model.Options.MaxDepth, ["Variáveis por árvore (0 = todas)"] = model.Options.MaxFeatures,
            ["Semente aleatória"] = model.Options.Seed, ["Linhas sem escore"] = invalid, ["Linhas sem eixos numéricos no gráfico"] = missingAxes,
        };
        return new(summary, details, new(chart, points.ToArray(), [], invalid));
    }
}
