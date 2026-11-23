using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Tabular;

namespace Dendro.Core.NeuralNetworks;

public sealed record NativeModelTrainingOptions(
    string ModelType,
    string ValidationMethod = "split",
    int Folds = 5,
    int Seed = 42,
    int[]? HiddenLayers = null,
    string Activation = "relu",
    int Epochs = 300,
    int BatchSize = 32,
    int Patience = 30,
    double LearningRate = 0.001,
    int Estimators = 200,
    int MaxDepth = 0,
    int MinSamplesSplit = 2,
    string MaxFeatures = "1.0",
    double Subsample = 0.8,
    string Kernel = "rbf",
    double C = 1,
    double Epsilon = 0.1,
    int Degree = 3);

public sealed record NativeModelBundle(string Path, string Stratum, int Rows, double R2, double Rmse, double Mae);

/// <summary>In-process C# regression training and a portable model format consumed by NeuralModel.</summary>
public static class NativeModelTrainer
{
    private const int MaximumEncodedFeatures = 512;
    private const int MaximumSupportVectors = 256;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record RawRow(object?[] Cells);
    private sealed record Sample(double[] Numeric, string[] Categorical, double[] Encoded, double Target, string Stratum);
    private sealed record Metrics(double R2, double Rmse, double Mae);
    private sealed record Tree(int[] Left, int[] Right, int[] Feature, double[] Threshold, double[] Value);
    private sealed record FitResult(JsonObject Payload, double[] Mean, double[] Scale, double OutputMean,
        double OutputScale, Func<double[], double> Predict);

    public static NativeModelBundle[] Train(TableSource source, string target, string[] features,
        string[] stratifiers, string outputDirectory, NativeModelTrainingOptions options,
        CancellationToken cancellation = default, Action<string>? progress = null)
    {
        ValidateOptions(options);
        if (features.Length == 0 || features.Distinct(StringComparer.Ordinal).Count() != features.Length
            || features.Contains(target, StringComparer.Ordinal))
            throw new InvalidDataException("Escolha preditoras únicas, diferentes da resposta Y.");
        using var cursor = new TableCursor(source);
        int sourceTargetIndex = FindColumn(cursor.Columns, target);
        int[] sourceFeatureIndexes = features.Select(name => FindColumn(cursor.Columns, name)).ToArray();
        int[] sourceStrataIndexes = stratifiers.Select(name => FindColumn(cursor.Columns, name)).ToArray();
        int[] projection = [sourceTargetIndex, .. sourceFeatureIndexes, .. sourceStrataIndexes];
        int targetIndex = 0;
        int[] featureIndexes = Enumerable.Range(1, features.Length).ToArray();
        int[] strataIndexes = Enumerable.Range(1 + features.Length, stratifiers.Length).ToArray();
        var raw = new List<RawRow>();
        long rowNumber = 0;
        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            rowNumber++;
            object?[] selected = projection.Select(index => row[index]).ToArray();
            raw.Add(new(selected));
            if (rowNumber % 100_000 == 0)
                progress?.Invoke($"Lendo tabela · {rowNumber:N0} linhas carregadas para treinamento.");
        }
        if (raw.Count < 10) throw new InvalidDataException("São necessárias pelo menos 10 linhas para treinar um modelo.");

        bool[] numeric = new bool[featureIndexes.Length];
        for (int f = 0; f < featureIndexes.Length; f++)
        {
            numeric[f] = raw.Select(r => r.Cells[featureIndexes[f]])
                .Where(value => !Missing(value)).All(TryNumber);
        }
        string[] numericNames = features.Where((_, i) => numeric[i]).ToArray();
        string[] categoricalNames = features.Where((_, i) => !numeric[i]).ToArray();
        int[] numericPositions = Enumerable.Range(0, features.Length).Where(i => numeric[i]).ToArray();
        int[] categoricalPositions = Enumerable.Range(0, features.Length).Where(i => !numeric[i]).ToArray();

        var categorySets = categoricalPositions.Select(position => raw.Select(r => Text(r.Cells[featureIndexes[position]]))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()).ToArray();
        if (categorySets.Sum(items => items.Length) + numericNames.Length > MaximumEncodedFeatures)
            throw new InvalidDataException($"O modelo excede o limite de {MaximumEncodedFeatures} variáveis codificadas.");

        var categoryIndexes = categorySets.Select(items => items.Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index, StringComparer.Ordinal)).ToArray();
        var strata = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        int excluded = 0;
        foreach (var item in raw)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!TryNumber(item.Cells[targetIndex], out double y) || !double.IsFinite(y)) { excluded++; continue; }
            string[] categoricalValues = new string[categoricalPositions.Length];
            double[] numericValues = new double[numericPositions.Length];
            bool valid = true;
            for (int i = 0; i < numericPositions.Length; i++)
            {
                object? value = item.Cells[featureIndexes[numericPositions[i]]];
                if (!TryNumber(value, out numericValues[i]) || !double.IsFinite(numericValues[i])) { valid = false; break; }
            }
            for (int i = 0; valid && i < categoricalPositions.Length; i++)
            {
                string value = Text(item.Cells[featureIndexes[categoricalPositions[i]]]);
                if (string.IsNullOrWhiteSpace(value)) { valid = false; break; }
                categoricalValues[i] = value;
            }
            if (!valid) { excluded++; continue; }
            string stratum = strataIndexes.Length == 0 ? "geral" : string.Join(" · ", strataIndexes.Select(index => Text(item.Cells[index])));
            if (strataIndexes.Length > 0 && strataIndexes.Any(index => Missing(item.Cells[index]))) { excluded++; continue; }
            double[] x = new double[MaximumEncodedFeatures];
            Array.Copy(numericValues, x, numericValues.Length);
            int offset = numericValues.Length;
            for (int i = 0; i < categoricalValues.Length; i++)
            {
                if (!categoryIndexes[i].TryGetValue(categoricalValues[i], out int encoded)) { valid = false; break; }
                x[offset + encoded] = 1;
                offset += categorySets[i].Length;
            }
            if (!valid) { excluded++; continue; }
            if (!strata.TryGetValue(stratum, out var group)) strata[stratum] = group = [];
            group.Add(new(numericValues, categoricalValues, x[..(numericValues.Length + categorySets.Sum(values => values.Length))], y, stratum));
        }
        progress?.Invoke($"Dados preparados · {raw.Count - excluded:N0} linhas válidas de {rowNumber:N0} lidas.");
        if (strata.Count == 0) throw new InvalidDataException("Não restaram linhas completas para treinamento.");

        Directory.CreateDirectory(outputDirectory);
        var bundles = new List<NativeModelBundle>();
        foreach (var (stratum, rows) in strata)
        {
            cancellation.ThrowIfCancellationRequested();
            if (rows.Count < 10) throw new InvalidDataException($"O estrato '{stratum}' tem menos de 10 linhas completas.");
            progress?.Invoke($"Treinando {options.ModelType} · {stratum} · {rows.Count:N0} linhas…");
            double[][] x = rows.Select(row => Encode(row, categoryIndexes, categorySets)).ToArray();
            double[] yValues = rows.Select(row => row.Target).ToArray();
            var (model, metrics) = ValidateAndFit(x, yValues, options, cancellation);
            var metadata = new JsonObject
            {
                ["model_type"] = options.ModelType,
                ["model_format"] = "dendro-native-v1",
                ["format_version"] = 2,
                ["target"] = target,
                ["features"] = new JsonArray(features.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["cols_numericas"] = new JsonArray(numericNames.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["cols_categoricas"] = new JsonArray(categoricalNames.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["estrato"] = stratum,
                ["n_samples"] = rows.Count,
                ["sampled_from_rows"] = rowNumber,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["validation"] = options.ValidationMethod,
                ["metricas"] = new JsonObject { ["r2"] = metrics.R2, ["rmse"] = metrics.Rmse, ["mae"] = metrics.Mae },
            };
            var preprocessing = new JsonObject
            {
                ["numerical"] = new JsonArray(numericNames.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["categorical"] = new JsonArray(categoricalNames.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["categories"] = new JsonArray(categorySets.Select(values => (JsonNode?)new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())).ToArray()),
                ["mean"] = new JsonArray(model.Mean.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["scale"] = new JsonArray(model.Scale.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["output_mean"] = model.OutputMean,
                ["output_scale"] = model.OutputScale,
            };
            string baseName = SafeName(target + "_" + stratum);
            string prefix = baseName + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);
            string zip = Path.Combine(outputDirectory, prefix + ".zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, prefix + "_metadata.json", metadata);
                WriteEntry(archive, prefix + "_preprocessing.json", preprocessing);
                WriteEntry(archive, prefix + "_model.json", model.Payload);
            }
            bundles.Add(new(zip, stratum, rows.Count, metrics.R2, metrics.Rmse, metrics.Mae));
        }
        return bundles.ToArray();
    }

    private static double[] Encode(Sample row, Dictionary<string, int>[] indexes, string[][] categories)
    {
        return row.Encoded;
    }

    private static (FitResult Model, Metrics Metrics) ValidateAndFit(double[][] x, double[] y,
        NativeModelTrainingOptions options, CancellationToken cancellation)
    {
        Metrics metrics;
        if (options.ValidationMethod == "full")
        {
            var final = Fit(x, y, options, cancellation);
            metrics = Measure(y, x.Select(final.Predict).ToArray());
            return (final, metrics);
        }
        var folds = new List<Metrics>();
        if (options.ValidationMethod == "kfold")
        {
            if (options.Folds < 2 || options.Folds > x.Length) throw new InvalidDataException("K-Fold precisa de 2 a N partições.");
            int[] order = Enumerable.Range(0, x.Length).ToArray();
            Shuffle(order, new Random(options.Seed));
            for (int fold = 0; fold < options.Folds; fold++)
            {
                cancellation.ThrowIfCancellationRequested();
                int[] test = order.Where((_, index) => index % options.Folds == fold).ToArray();
                var testSet = test.ToHashSet();
                int[] train = order.Where(index => !testSet.Contains(index)).ToArray();
                var fit = Fit(Select(x, train), Select(y, train), options, cancellation);
                folds.Add(Measure(Select(y, test), Select(x, test).Select(fit.Predict).ToArray()));
            }
        }
        else
        {
            int[] order = Enumerable.Range(0, x.Length).ToArray();
            Shuffle(order, new Random(options.Seed));
            int testCount = Math.Max(1, (int)Math.Round(x.Length * 0.2));
            int[] test = order[..testCount], train = order[testCount..];
            var fit = Fit(Select(x, train), Select(y, train), options, cancellation);
            folds.Add(Measure(Select(y, test), Select(x, test).Select(fit.Predict).ToArray()));
        }
        var finalModel = Fit(x, y, options, cancellation);
        metrics = new(folds.Average(item => item.R2), folds.Average(item => item.Rmse), folds.Average(item => item.Mae));
        return (finalModel, metrics);
    }

    private static FitResult Fit(double[][] x, double[] y, NativeModelTrainingOptions options, CancellationToken cancellation)
    {
        bool scale = options.ModelType is "neural_net" or "svr";
        int inputs = x[0].Length;
        double[] mean = scale ? Enumerable.Range(0, inputs).Select(column => x.Average(row => row[column])).ToArray() : new double[inputs];
        double[] std = scale ? Enumerable.Range(0, inputs).Select(column =>
        {
            double average = mean[column];
            double deviation = Math.Sqrt(x.Average(row => (row[column] - average) * (row[column] - average)));
            return deviation > 1e-12 ? deviation : 1;
        }).ToArray() : Enumerable.Repeat(1d, inputs).ToArray();
        double[][] matrix = x.Select(row => row.Select((value, index) => (value - mean[index]) / std[index]).ToArray()).ToArray();
        double outputMean = scale ? y.Average() : 0;
        double outputScale = scale ? Math.Sqrt(y.Average(value => (value - outputMean) * (value - outputMean))) : 1;
        if (outputScale < 1e-12) outputScale = 1;
        double[] output = y.Select(value => (value - outputMean) / outputScale).ToArray();
        var trained = options.ModelType switch
        {
            "random_forest" => FitForest(matrix, output, options, mean, std, outputMean, outputScale, cancellation),
            "gradient_boosting" => FitBoosting(matrix, output, options, mean, std, outputMean, outputScale, cancellation),
            "svr" => FitSvr(matrix, output, options, mean, std, outputMean, outputScale, cancellation),
            "neural_net" => FitNetwork(matrix, output, options, mean, std, outputMean, outputScale, cancellation),
            _ => throw new InvalidDataException($"Tipo de modelo não suportado: {options.ModelType}.")
        };
        double PredictRaw(double[] row)
        {
            double[] transformed = new double[inputs];
            for (int i = 0; i < inputs; i++) transformed[i] = (row[i] - mean[i]) / std[i];
            double result = trained.Predict(transformed);
            return scale ? result * outputScale + outputMean : result;
        }
        return trained with { Predict = PredictRaw };
    }

    private static FitResult FitForest(double[][] x, double[] y, NativeModelTrainingOptions options,
        double[] mean, double[] scale, double outputMean, double outputScale, CancellationToken cancellation)
    {
        var trees = new List<Tree>(options.Estimators);
        int[] all = Enumerable.Range(0, x.Length).ToArray();
        for (int tree = 0; tree < options.Estimators; tree++)
        {
            cancellation.ThrowIfCancellationRequested();
            var random = new Random(options.Seed + tree * 7919);
            int[] sample = Enumerable.Range(0, x.Length).Select(_ => random.Next(x.Length)).ToArray();
            trees.Add(DecisionTree.Build(x, y, sample, options, random, cancellation));
        }
        var payload = new JsonObject { ["kind"] = "random_forest", ["trees"] = new JsonArray(trees.Select(TreeJson).ToArray()) };
        double Predict(double[] row) => trees.Average(tree => PredictTree(tree, row));
        return new(payload, mean, scale, outputMean, outputScale, Predict);
    }

    private static FitResult FitBoosting(double[][] x, double[] y, NativeModelTrainingOptions options,
        double[] mean, double[] scale, double outputMean, double outputScale, CancellationToken cancellation)
    {
        double initial = y.Average();
        double[] fitted = Enumerable.Repeat(initial, y.Length).ToArray();
        var trees = new List<Tree>(options.Estimators);
        for (int tree = 0; tree < options.Estimators; tree++)
        {
            cancellation.ThrowIfCancellationRequested();
            double[] residual = y.Select((value, i) => value - fitted[i]).ToArray();
            var random = new Random(options.Seed + tree * 7919);
            int sampleCount = Math.Clamp((int)Math.Round(x.Length * options.Subsample), 2, x.Length);
            int[] sample = Enumerable.Range(0, x.Length).ToArray(); Shuffle(sample, random); sample = sample[..sampleCount];
            Tree trained = DecisionTree.Build(x, residual, sample, options, random, cancellation);
            trees.Add(trained);
            for (int i = 0; i < fitted.Length; i++) fitted[i] += options.LearningRate * PredictTree(trained, x[i]);
        }
        var payload = new JsonObject { ["kind"] = "gradient_boosting", ["initial"] = initial,
            ["learning_rate"] = options.LearningRate, ["trees"] = new JsonArray(trees.Select(TreeJson).ToArray()) };
        double Predict(double[] row) => initial + options.LearningRate * trees.Sum(tree => PredictTree(tree, row));
        return new(payload, mean, scale, outputMean, outputScale, Predict);
    }

    private static FitResult FitSvr(double[][] x, double[] y, NativeModelTrainingOptions options,
        double[] mean, double[] scale, double outputMean, double outputScale, CancellationToken cancellation)
    {
        int supportCount = Math.Min(x.Length, Math.Min(MaximumSupportVectors, Math.Max(16, x[0].Length * 2 + 16)));
        int[] supportIndices = Enumerable.Range(0, x.Length).ToArray(); Shuffle(supportIndices, new Random(options.Seed)); supportIndices = supportIndices[..supportCount];
        double[][] supports = supportIndices.Select(index => x[index]).ToArray();
        double gamma = 1.0 / Math.Max(1, x[0].Length);
        double[] coefficients = new double[supportCount];
        double intercept = 0;
        double regularization = 1.0 / Math.Max(options.C, 1e-9);
        int epochs = Math.Clamp(options.Epochs, 20, 1000);
        double rate = Math.Min(0.1, Math.Max(0.0001, options.LearningRate * 10));
        var random = new Random(options.Seed);
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            cancellation.ThrowIfCancellationRequested();
            int[] order = Enumerable.Range(0, x.Length).ToArray(); Shuffle(order, random);
            foreach (int i in order)
            {
                double[] kernels = supports.Select(support => KernelValue(x[i], support, options.Kernel, gamma, options.Degree)).ToArray();
                double prediction = intercept;
                for (int j = 0; j < supportCount; j++) prediction += coefficients[j] * kernels[j];
                double error = prediction - y[i];
                double gradient = error > options.Epsilon ? 1 : error < -options.Epsilon ? -1 : 0;
                for (int j = 0; j < supportCount; j++)
                    coefficients[j] -= rate * (regularization * coefficients[j] + options.C * gradient * kernels[j]);
                intercept -= rate * options.C * gradient;
            }
            rate *= 0.995;
        }
        var payload = new JsonObject { ["kind"] = "svr", ["kernel"] = options.Kernel,
            ["support_vectors"] = new JsonArray(supports.Select(row => (JsonNode?)new JsonArray(row.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())).ToArray()),
            ["dual_coef"] = new JsonArray(coefficients.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()), ["intercept"] = intercept,
            ["gamma"] = gamma, ["coef0"] = 0, ["degree"] = options.Degree };
        double Predict(double[] row)
        {
            double result = intercept;
            for (int j = 0; j < supportCount; j++) result += coefficients[j] * KernelValue(row, supports[j], options.Kernel, gamma, options.Degree);
            return result;
        }
        return new(payload, mean, scale, outputMean, outputScale, Predict);
    }

    private static FitResult FitNetwork(double[][] x, double[] y, NativeModelTrainingOptions options,
        double[] mean, double[] scale, double outputMean, double outputScale, CancellationToken cancellation)
    {
        int[] widths = [x[0].Length, .. (options.HiddenLayers ?? [16, 16, 8]), 1];
        var random = new Random(options.Seed);
        var weights = new float[widths.Length - 1][];
        var biases = new float[widths.Length - 1][];
        for (int layer = 0; layer < weights.Length; layer++)
        {
            int fanIn = widths[layer], fanOut = widths[layer + 1];
            weights[layer] = new float[fanIn * fanOut];
            biases[layer] = new float[fanOut];
            double limit = Math.Sqrt(6.0 / (fanIn + fanOut));
            for (int i = 0; i < weights[layer].Length; i++) weights[layer][i] = (float)((random.NextDouble() * 2 - 1) * limit);
        }
        double bestLoss = double.PositiveInfinity;
        var bestWeights = weights.Select(layer => layer.ToArray()).ToArray();
        var bestBiases = biases.Select(layer => layer.ToArray()).ToArray();
        int stale = 0, batchSize = Math.Clamp(options.BatchSize, 1, x.Length);
        int validationCount = x.Length >= 15 ? Math.Max(1, x.Length / 5) : 0;
        int[] order = Enumerable.Range(0, x.Length).ToArray(); Shuffle(order, random);
        int[] validation = order[..validationCount], training = order[validationCount..];
        if (training.Length < 2) training = order;
        double[][] firstMomentW = weights.Select(layer => new double[layer.Length]).ToArray();
        double[][] secondMomentW = weights.Select(layer => new double[layer.Length]).ToArray();
        double[][] firstMomentB = biases.Select(layer => new double[layer.Length]).ToArray();
        double[][] secondMomentB = biases.Select(layer => new double[layer.Length]).ToArray();
        int step = 0;
        for (int epoch = 0; epoch < options.Epochs; epoch++)
        {
            cancellation.ThrowIfCancellationRequested();
            Shuffle(training, random);
            for (int start = 0; start < training.Length; start += batchSize)
            {
                int count = Math.Min(batchSize, training.Length - start);
                var gradW = weights.Select(layer => new double[layer.Length]).ToArray();
                var gradB = biases.Select(layer => new double[layer.Length]).ToArray();
                for (int row = start; row < start + count; row++)
                    AccumulateGradient(x[training[row]], y[training[row]], weights, biases, gradW, gradB, options.Activation);
                step++;
                for (int layer = 0; layer < weights.Length; layer++)
                {
                    for (int i = 0; i < weights[layer].Length; i++)
                        weights[layer][i] = (float)Adam(weights[layer][i], gradW[layer][i] / count, firstMomentW[layer], secondMomentW[layer], i, step, options.LearningRate);
                    for (int i = 0; i < biases[layer].Length; i++)
                        biases[layer][i] = (float)Adam(biases[layer][i], gradB[layer][i] / count, firstMomentB[layer], secondMomentB[layer], i, step, options.LearningRate);
                }
            }
            double loss = validationCount > 0 ? MeanSquaredError(x, y, validation, weights, biases, options.Activation)
                : MeanSquaredError(x, y, training, weights, biases, options.Activation);
            if (loss < bestLoss - 1e-8)
            {
                bestLoss = loss; stale = 0;
                for (int layer = 0; layer < weights.Length; layer++) { weights[layer].CopyTo(bestWeights[layer], 0); biases[layer].CopyTo(bestBiases[layer], 0); }
            }
            else if (++stale >= options.Patience) break;
        }
        weights = bestWeights; biases = bestBiases;
        var layers = new JsonArray();
        var dense = new NeuralModel.DenseLayer[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            string activation = i == weights.Length - 1 ? "linear" : options.Activation;
            dense[i] = new(widths[i], widths[i + 1], weights[i], biases[i], activation);
            layers.Add(new JsonObject { ["inputs"] = widths[i], ["outputs"] = widths[i + 1],
                ["weights"] = new JsonArray(weights[i].Select(value => (JsonNode?)JsonValue.Create((double)value)).ToArray()),
                ["bias"] = new JsonArray(biases[i].Select(value => (JsonNode?)JsonValue.Create((double)value)).ToArray()), ["activation"] = activation });
        }
        var payload = new JsonObject { ["kind"] = "neural_net", ["layers"] = layers };
        double Predict(double[] row) => PredictDense(row, dense, options.Activation);
        return new(payload, mean, scale, outputMean, outputScale, Predict);
    }

    private static void AccumulateGradient(double[] input, double expected, float[][] weights, float[][] biases,
        double[][] gradientWeights, double[][] gradientBiases, string activation)
    {
        int layerCount = weights.Length;
        var values = new float[layerCount + 1][]; values[0] = input.Select(value => (float)value).ToArray();
        for (int layer = 0; layer < layerCount; layer++)
        {
            int inputs = values[layer].Length, outputs = biases[layer].Length;
            values[layer + 1] = new float[outputs];
            for (int j = 0; j < outputs; j++)
            {
                double sum = biases[layer][j];
                for (int i = 0; i < inputs; i++) sum += values[layer][i] * weights[layer][i * outputs + j];
                values[layer + 1][j] = Activate((float)sum, layer == layerCount - 1 ? "linear" : activation);
            }
        }
        var delta = new float[1][]; delta[0] = [2 * (values[^1][0] - (float)expected)];
        for (int layer = layerCount - 1; layer >= 0; layer--)
        {
            int inputs = values[layer].Length, outputs = biases[layer].Length;
            for (int j = 0; j < outputs; j++)
            {
                gradientBiases[layer][j] += delta[0][j];
                for (int i = 0; i < inputs; i++) gradientWeights[layer][i * outputs + j] += values[layer][i] * delta[0][j];
            }
            if (layer == 0) break;
            var previous = new float[inputs];
            for (int i = 0; i < inputs; i++)
            {
                double sum = 0;
                for (int j = 0; j < outputs; j++) sum += delta[0][j] * weights[layer][i * outputs + j];
                previous[i] = (float)(sum * Derivative(values[layer][i], activation));
            }
            delta[0] = previous;
        }
    }

    private static double Adam(double value, double gradient, double[] first, double[] second, int index, int step, double rate)
    {
        first[index] = 0.9 * first[index] + 0.1 * gradient;
        second[index] = 0.999 * second[index] + 0.001 * gradient * gradient;
        double m = first[index] / (1 - Math.Pow(0.9, step)), v = second[index] / (1 - Math.Pow(0.999, step));
        return value - rate * m / (Math.Sqrt(v) + 1e-8);
    }

    private static double MeanSquaredError(double[][] x, double[] y, int[] indexes, float[][] weights, float[][] biases, string activation)
    {
        double sum = 0;
        foreach (int index in indexes) { double error = PredictDense(x[index], weights, biases, activation) - y[index]; sum += error * error; }
        return sum / Math.Max(1, indexes.Length);
    }

    private static double PredictDense(double[] row, NeuralModel.DenseLayer[] layers, string activation)
    {
        float[] current = row.Select(value => (float)value).ToArray();
        foreach (var layer in layers)
        {
            var next = new float[layer.Outputs];
            for (int j = 0; j < layer.Outputs; j++)
            {
                double sum = layer.Bias[j];
                for (int i = 0; i < layer.Inputs; i++) sum += current[i] * layer.Weights[i * layer.Outputs + j];
                next[j] = Activate((float)sum, layer.Activation);
            }
            current = next;
        }
        return current[0];
    }

    private static double PredictDense(double[] row, float[][] weights, float[][] biases, string activation)
    {
        float[] current = row.Select(value => (float)value).ToArray();
        for (int layer = 0; layer < weights.Length; layer++)
        {
            int outputs = biases[layer].Length, inputs = current.Length;
            var next = new float[outputs];
            for (int j = 0; j < outputs; j++)
            {
                double sum = biases[layer][j];
                for (int i = 0; i < inputs; i++) sum += current[i] * weights[layer][i * outputs + j];
                next[j] = Activate((float)sum, layer == weights.Length - 1 ? "linear" : activation);
            }
            current = next;
        }
        return current[0];
    }

    private static float Activate(float x, string kind) => kind switch
    {
        "relu" => MathF.Max(0, x),
        "tanh" => MathF.Tanh(x),
        "sigmoid" => x >= 0 ? 1 / (1 + MathF.Exp(-x)) : MathF.Exp(x) / (1 + MathF.Exp(x)),
        _ => x,
    };
    private static double Derivative(float activated, string kind) => kind switch
    {
        "relu" => activated > 0 ? 1 : 0,
        "tanh" => 1 - activated * activated,
        "sigmoid" => activated * (1 - activated),
        _ => 1,
    };

    private static JsonObject TreeJson(Tree tree) => new()
    {
        ["left"] = new JsonArray(tree.Left.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["right"] = new JsonArray(tree.Right.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["feature"] = new JsonArray(tree.Feature.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["threshold"] = new JsonArray(tree.Threshold.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["value"] = new JsonArray(tree.Value.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
    };

    private static double PredictTree(Tree tree, double[] x)
    {
        int node = 0, steps = 0;
        while (tree.Feature[node] >= 0)
        {
            if (++steps > tree.Feature.Length) return double.NaN;
            int feature = tree.Feature[node];
            node = x[feature] <= tree.Threshold[node] ? tree.Left[node] : tree.Right[node];
            if (node < 0 || node >= tree.Feature.Length) return double.NaN;
        }
        return tree.Value[node];
    }

    private static double KernelValue(double[] x, double[] support, string kernel, double gamma, int degree)
    {
        double dot = 0, squared = 0;
        for (int i = 0; i < x.Length; i++) { dot += x[i] * support[i]; double delta = x[i] - support[i]; squared += delta * delta; }
        return kernel switch { "linear" => dot, "rbf" => Math.Exp(-gamma * squared), "poly" => Math.Pow(gamma * dot + 1, degree), _ => throw new InvalidDataException("Kernel SVR inválido.") };
    }

    private static Metrics Measure(double[] expected, double[] predicted)
    {
        double average = expected.Average();
        double sse = 0, sst = 0, absolute = 0;
        for (int i = 0; i < expected.Length; i++) { double delta = expected[i] - predicted[i]; sse += delta * delta; sst += Math.Pow(expected[i] - average, 2); absolute += Math.Abs(delta); }
        return new(sst > 0 ? 1 - sse / sst : sse == 0 ? 1 : 0, Math.Sqrt(sse / expected.Length), absolute / expected.Length);
    }

    private static double[][] Select(double[][] source, int[] indices) => indices.Select(index => source[index]).ToArray();
    private static double[] Select(double[] source, int[] indices) => indices.Select(index => source[index]).ToArray();
    private static void Shuffle<T>(T[] values, Random random) { for (int i = values.Length - 1; i > 0; i--) { int j = random.Next(i + 1); (values[i], values[j]) = (values[j], values[i]); } }
    private static int FindColumn(string[] columns, string name) { int index = Array.IndexOf(columns, name); return index >= 0 ? index : throw new InvalidDataException($"Coluna ausente: {name}."); }
    private static bool Missing(object? value) => value is null || string.IsNullOrWhiteSpace(Text(value));
    private static string Text(object? value) => TableCursor.Text(value);
    private static bool TryNumber(object? value) => TryNumber(value, out _);
    private static bool TryNumber(object? value, out double number)
    {
        if (value is double d) { number = d; return double.IsFinite(d); }
        if (value is int i) { number = i; return true; }
        string text = Text(value);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number) && double.IsFinite(number);
    }
    private static void ValidateOptions(NativeModelTrainingOptions options)
    {
        if (options.ModelType is not ("neural_net" or "random_forest" or "gradient_boosting" or "svr")) throw new InvalidDataException("Tipo de modelo nativo inválido.");
        if (options.ValidationMethod is not ("split" or "kfold" or "full")) throw new InvalidDataException("Método de validação inválido.");
        if (options.EstimatorCheck() < 1) throw new InvalidDataException("Informe ao menos um estimador.");
        if (options.EstimatorCheck() > 10000 || options.MaxDepth < 0 || options.MaxDepth > 64
            || options.MinSamplesSplit < 2 || options.Folds is < 2 or > 100
            || !double.IsFinite(options.LearningRate) || !double.IsFinite(options.Subsample)
            || options.Subsample is <= 0 or > 1 || !double.IsFinite(options.C) || options.C is <= 0 or > 1_000_000
            || !double.IsFinite(options.Epsilon) || options.Epsilon < 0 || options.Degree is < 1 or > 12)
            throw new InvalidDataException("Parâmetros do treinamento fora dos limites permitidos.");
        if (options.ModelType is "gradient_boosting" or "neural_net" && options.LearningRate is <= 0 or > 1)
            throw new InvalidDataException("Taxa de aprendizado deve estar entre 0 e 1.");
        if (!double.TryParse(options.MaxFeatures, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction)
            && options.MaxFeatures is not ("sqrt" or "log2"))
            throw new InvalidDataException("max_features deve ser uma fração, sqrt ou log2.");
        if (double.TryParse(options.MaxFeatures, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction) && fraction is <= 0 or > 1)
            throw new InvalidDataException("max_features deve estar no intervalo (0, 1].");
        if (options.ModelType == "svr" && options.Kernel is not ("rbf" or "linear" or "poly")) throw new InvalidDataException("Kernel SVR não suportado.");
        if (options.ModelType == "neural_net" && (options.HiddenLayers is not { Length: > 0 and <= 16 }
            || options.HiddenLayers.Any(width => width is < 1 or > 512) || options.Epochs is < 1 or > 10000
            || options.BatchSize is < 1 or > 65536 || options.Patience is < 1 or > 10000
            || options.LearningRate is <= 0 or > 1 || options.Activation is not ("relu" or "tanh" or "sigmoid")))
            throw new InvalidDataException("Parâmetros da rede neural fora dos limites.");
    }
    private static int EstimatorCheck(this NativeModelTrainingOptions options) => options.ModelType is "random_forest" or "gradient_boosting" ? options.Estimators : 1;
    private static string SafeName(string value) => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim(' ', '.', '_') is { Length: > 0 } name ? name[..Math.Min(90, name.Length)] : "modelo";
    private static void WriteEntry(ZipArchive archive, string name, JsonNode node)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(node.ToJsonString(JsonOptions));
    }

    private static class DecisionTree
    {
        private sealed class Node { public int Left = -1, Right = -1, Feature = -1; public double Threshold, Value; }
        internal static Tree Build(double[][] x, double[] y, int[] samples, NativeModelTrainingOptions options, Random random, CancellationToken cancellation)
        {
            int maxDepth = options.MaxDepth <= 0 ? 32 : Math.Min(options.MaxDepth, 64);
            var nodes = new List<Node>();
            int BuildNode(int[] rows, int depth)
            {
                cancellation.ThrowIfCancellationRequested();
                int index = nodes.Count; var node = new Node { Value = rows.Average(row => y[row]) }; nodes.Add(node);
                if (depth >= maxDepth || rows.Length < options.MinSamplesSplit) return index;
                if (!FindSplit(x, y, rows, options, random, out int feature, out double threshold, out int[] left, out int[] right)) return index;
                node.Feature = feature; node.Threshold = threshold;
                node.Left = BuildNode(left, depth + 1); node.Right = BuildNode(right, depth + 1);
                return index;
            }
            BuildNode(samples, 0);
            return new(nodes.Select(node => node.Left).ToArray(), nodes.Select(node => node.Right).ToArray(),
                nodes.Select(node => node.Feature).ToArray(), nodes.Select(node => node.Threshold).ToArray(), nodes.Select(node => node.Value).ToArray());
        }

        private static bool FindSplit(double[][] x, double[] y, int[] rows, NativeModelTrainingOptions options,
            Random random, out int bestFeature, out double bestThreshold, out int[] bestLeft, out int[] bestRight)
        {
            bestFeature = -1; bestThreshold = 0; bestLeft = []; bestRight = [];
            double totalSum = rows.Sum(row => y[row]), totalSq = rows.Sum(row => y[row] * y[row]);
            double parentSse = totalSq - totalSum * totalSum / rows.Length, bestSse = parentSse;
            int featureCount = Math.Max(1, (int)Math.Round(ParseFeatureCount(options.MaxFeatures, x[0].Length)));
            int[] features = Enumerable.Range(0, x[0].Length).ToArray(); Shuffle(features, random); features = features[..Math.Min(featureCount, features.Length)];
            foreach (int feature in features)
            {
                int[] sorted = rows.ToArray();
                Array.Sort(sorted, (a, b) => x[a][feature].CompareTo(x[b][feature]));
                double leftSum = 0, leftSq = 0;
                for (int split = 1; split < sorted.Length; split++)
                {
                    double value = y[sorted[split - 1]]; leftSum += value; leftSq += value * value;
                    int leftN = split, rightN = sorted.Length - split;
                    if (leftN < 1 || rightN < 1 || x[sorted[split - 1]][feature] >= x[sorted[split]][feature]) continue;
                    if (options.MinSamplesSplit > 2 && (leftN < 1 || rightN < 1)) continue;
                    double rightSum = totalSum - leftSum, rightSq = totalSq - leftSq;
                    double sse = leftSq - leftSum * leftSum / leftN + rightSq - rightSum * rightSum / rightN;
                    if (sse + 1e-12 >= bestSse) continue;
                    bestSse = sse; bestFeature = feature;
                    bestThreshold = x[sorted[split - 1]][feature] + (x[sorted[split]][feature] - x[sorted[split - 1]][feature]) / 2;
                    bestLeft = sorted[..split]; bestRight = sorted[split..];
                }
            }
            return bestFeature >= 0 && bestSse < parentSse - 1e-12;
        }
    }

    private static double ParseFeatureCount(string setting, int inputs)
    {
        if (double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction))
            return Math.Clamp(fraction, 0.01, 1) * inputs;
        return setting switch { "sqrt" => Math.Sqrt(inputs), "log2" => Math.Log2(Math.Max(2, inputs)), _ => inputs };
    }
}
