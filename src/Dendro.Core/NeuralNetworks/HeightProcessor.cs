using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.NeuralNetworks;

public static class HeightProcessor
{
    private sealed class PredictionState(NeuralModel model, int numericalCount, int categoricalCount, int hiddenCount)
    {
        public PredictionWorkspace Workspace { get; } = model.CreatePredictionWorkspace();
        public double[] Numerical { get; } = new double[numericalCount];
        public string[] Categorical { get; } = new string[categoricalCount];
        public float[] Hidden { get; } = new float[hiddenCount];
        public int PendingRows;
    }

    private sealed record Calibration(
        int Method = 0,
        double Intercept = 0,
        double Slope = 1,
        double[]? Weights = null
    )
    {
        public double Predict(double prediction, ReadOnlySpan<float> hidden)
        {
            if (!double.IsFinite(prediction)) return double.NaN;
            if (Method != 3) return Slope * prediction + Intercept;
            double value = Intercept;
            for (int i = 0; i < Weights!.Length; i++) value += Weights[i] * hidden[i];
            return value;
        }
    }

    private sealed class CalibrationAccumulator(int width)
    {
        private readonly object gate = new();
        private readonly double[] hiddenSum = new double[width];
        private readonly double[] hiddenTarget = new double[width];
        private readonly double[] hiddenCross = new double[checked(width * width)];
        public int Count { get; private set; }
        public double PredictionSum { get; private set; }
        public double PredictionSquareSum { get; private set; }
        public double PredictionTargetSum { get; private set; }
        public double TargetSum { get; private set; }
        public double TargetSquareSum { get; private set; }

        public void Add(double prediction, double target, ReadOnlySpan<float> hidden)
        {
            if (!(target > 0) || !double.IsFinite(prediction)) return;
            lock (gate)
                AddUnsafe(prediction, target, hidden);
        }

        public void AddUnsafe(double prediction, double target, ReadOnlySpan<float> hidden)
        {
            if (!(target > 0) || !double.IsFinite(prediction)) return;
            Count++;
            PredictionSum += prediction;
            PredictionSquareSum += prediction * prediction;
            PredictionTargetSum += prediction * target;
            TargetSum += target;
            TargetSquareSum += target * target;
            for (int j = 0; j < width; j++)
            {
                double x = hidden[j];
                hiddenSum[j] += x;
                hiddenTarget[j] += x * target;
                int row = j * width;
                for (int k = j; k < width; k++)
                    hiddenCross[row + k - j] += x * hidden[k];
            }
        }

        public void Merge(CalibrationAccumulator source)
        {
            Count += source.Count;
            PredictionSum += source.PredictionSum;
            PredictionSquareSum += source.PredictionSquareSum;
            PredictionTargetSum += source.PredictionTargetSum;
            TargetSum += source.TargetSum;
            TargetSquareSum += source.TargetSquareSum;
            for (int j = 0; j < width; j++)
            {
                hiddenSum[j] += source.hiddenSum[j];
                hiddenTarget[j] += source.hiddenTarget[j];
                int row = j * width;
                for (int k = j; k < width; k++)
                    hiddenCross[row + k - j] += source.hiddenCross[row + k - j];
            }
        }

        public Calibration Fit(JsonNode? config, bool global)
        {
            int n = Count;
            if (n == 0) return new();
            double xmean = PredictionSum / n, ymean = TargetSum / n;
            if (n >= config.N("min_amostras_finetune", 30) && width is > 0 and <= 512)
            {
                var means = new double[width];
                for (int j = 0; j < width; j++) means[j] = hiddenSum[j] / n;
                var covariance = new double[width, width];
                var matrix = new double[width, width];
                var rhs = new double[width];
                for (int j = 0; j < width; j++)
                {
                    rhs[j] = hiddenTarget[j] - hiddenSum[j] * TargetSum / n;
                    int row = j * width;
                    for (int k = j; k < width; k++)
                    covariance[j, k] = covariance[k, j] =
                            hiddenCross[row + k - j] - hiddenSum[j] * hiddenSum[k] / n;
                }
                for (int j = 0; j < width; j++)
                    for (int k = 0; k < width; k++) matrix[j, k] = covariance[j, k];
                for (int j = 0; j < width; j++)
                    matrix[j, j] += config.N("ridge_alpha", 10);
                if (InventoryMath.Solve(matrix, rhs, out var weights))
                {
                    double intercept = ymean;
                    for (int j = 0; j < width; j++) intercept -= means[j] * weights[j];
                    var fit = new Calibration(3, intercept, 1, weights);
                    double ss = TargetSquareSum - TargetSum * TargetSum / n;
                    double residual = ss;
                    for (int j = 0; j < width; j++)
                    {
                        residual -= 2 * weights[j] * rhs[j];
                        for (int k = 0; k < width; k++)
                            residual += weights[j] * covariance[j, k] * weights[k];
                    }
                    double r2 = ss > 0 ? 1 - residual / ss
                        : residual < 1e-12 ? 1
                        : 0;
                    if (n <= 1 || r2 >= (global ? 0 : .5)) return fit;
                }
            }
            if (n >= config.N("min_amostras_linear", 10))
            {
                double xx = PredictionSquareSum - PredictionSum * PredictionSum / n;
                double xy = PredictionTargetSum - PredictionSum * TargetSum / n;
                if (xx > 1e-15)
                    return new(2, ymean - xy / xx * xmean, xy / xx);
            }
            return n >= config.N("min_amostras_bias", 1)
                ? new(1, ymean - xmean)
                : new();
        }
    }

    private readonly record struct FineTuneGroupKey(string Context, string Lot);

    private static void ApplyFineTuneStreaming(
        DataTable input,
        NeuralModel model,
        JsonObject config,
        JsonNode? fineTuneConfig,
        InventoryIdentity identity,
        int[] numericalColumns,
        int[] categoricalColumns,
        int heightColumn,
        int dapColumn,
        int lotColumn,
        int observedColumn,
        int estimatedColumn,
        int finalColumn,
        int methodColumn,
        CancellationToken cancellation,
        Action<int, string>? progress
    )
    {
        int rowCount = input.Count, hiddenWidth = model.HiddenSize;
        var numericalViews = numericalColumns.Select(input.NumericView).ToArray();
        bool directNumericalReads = numericalViews.All(view => view.HasValue);
        var heightView = input.NumericView(heightColumn);
        var dapView = input.NumericView(dapColumn);
        var rowGroups = new int[rowCount];
        var groupLookup = new Dictionary<FineTuneGroupKey, int>();
        var contextLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupContexts = new List<int>();
        for (int row = 1; row < rowCount; row++)
        {
            if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            string context = identity.Context(row).Id;
            if (!contextLookup.TryGetValue(context, out int contextIndex))
                contextLookup.Add(context, contextIndex = contextLookup.Count);
            var key = new FineTuneGroupKey(context, input.Text(row, lotColumn));
            if (!groupLookup.TryGetValue(key, out int groupIndex))
            {
                groupIndex = groupLookup.Count;
                groupLookup.Add(key, groupIndex);
                groupContexts.Add(contextIndex);
            }
            rowGroups[row] = groupIndex;
        }

        int partitionCount = Math.Min(
            Math.Clamp(Environment.ProcessorCount, 1, 8),
            Math.Max(1, (int)(((long)rowCount - 1 + 199_999) / 200_000)));
        long accumulatorBytes = (long)partitionCount * groupContexts.Count
            * (32L + (long)(hiddenWidth * hiddenWidth + hiddenWidth * 2 + 5) * sizeof(double));
        bool usePrivateAccumulators = accumulatorBytes <= 192L * 1024 * 1024;
        CalibrationAccumulator[][]? partitionSums = usePrivateAccumulators
            ? Enumerable.Range(0, partitionCount)
                .Select(_ => Enumerable.Range(0, groupContexts.Count)
                    .Select(_ => new CalibrationAccumulator(hiddenWidth)).ToArray())
                .ToArray()
            : null;
        CalibrationAccumulator[]? sharedSums = usePrivateAccumulators ? null
            : Enumerable.Range(0, groupContexts.Count)
                .Select(_ => new CalibrationAccumulator(hiddenWidth)).ToArray();
        int finitePredictions = 0, processedRows = 0;
        object progressLock = new();
        var options = new ParallelOptions
        {
            CancellationToken = cancellation,
            MaxDegreeOfParallelism = partitionCount,
        };
        progress?.Invoke(25, "Executando RNA e acumulando calibração por lote…");
        Parallel.For(0, partitionCount, options, partition =>
        {
            int start = 1 + (int)((long)(rowCount - 1) * partition / partitionCount);
            int end = 1 + (int)((long)(rowCount - 1) * (partition + 1) / partitionCount);
            var state = new PredictionState(model, numericalColumns.Length, categoricalColumns.Length, hiddenWidth);
            var sums = partitionSums?[partition] ?? sharedSums!;
            for (int row = start; row < end; row++)
            {
                if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                if (directNumericalReads)
                {
                    for (int i = 0; i < numericalViews.Length; i++)
                        state.Numerical[i] = numericalViews[i]!.Value.Number(row);
                }
                else
                {
                    for (int i = 0; i < numericalColumns.Length; i++)
                        state.Numerical[i] = input.Number(row, numericalColumns[i]);
                }
                for (int i = 0; i < categoricalColumns.Length; i++)
                    state.Categorical[i] = input.Text(row, categoricalColumns[i]);
                double prediction = model.Predict(state.Numerical, state.Categorical,
                    state.Workspace, state.Hidden);
                var accumulator = sums[rowGroups[row]];
                if (usePrivateAccumulators)
                    accumulator.AddUnsafe(prediction, heightView?.Number(row) ?? input.Number(row, heightColumn), state.Hidden);
                else accumulator.Add(prediction, heightView?.Number(row) ?? input.Number(row, heightColumn), state.Hidden);
                if (double.IsFinite(prediction)) Interlocked.Increment(ref finitePredictions);
                if (++state.PendingRows >= 8192)
                {
                    int done = state.PendingRows;
                    state.PendingRows = 0;
                    int total = Interlocked.Add(ref processedRows, done);
                    int percent = 25 + (int)(3L * total / Math.Max(1, rowCount - 1));
                    lock (progressLock)
                        progress?.Invoke(Math.Min(28, percent),
                            $"RNA: {total:N0} de {rowCount - 1:N0} árvores processadas…");
                }
            }
            if (state.PendingRows > 0)
            {
                int total = Interlocked.Add(ref processedRows, state.PendingRows);
                int percent = 25 + (int)(3L * total / Math.Max(1, rowCount - 1));
                lock (progressLock)
                    progress?.Invoke(Math.Min(28, percent),
                        $"RNA: {total:N0} de {rowCount - 1:N0} árvores processadas…");
            }
        });
        cancellation.ThrowIfCancellationRequested();
        if (finitePredictions == 0)
            throw new InvalidDataException(
                "Nenhuma linha possui variáveis completas para executar a RNA. Revise as estatísticas e as variáveis do modelo.");

        CalibrationAccumulator[] localSums;
        if (partitionSums != null)
        {
            localSums = Enumerable.Range(0, groupContexts.Count)
                .Select(_ => new CalibrationAccumulator(hiddenWidth)).ToArray();
            for (int partition = 0; partition < partitionCount; partition++)
                for (int group = 0; group < localSums.Length; group++)
                    localSums[group].Merge(partitionSums[partition][group]);
            partitionSums = null;
        }
        else localSums = sharedSums!;

        progress?.Invoke(28, "RNA concluída; ajustando calibração por lote…");

        var contextSums = Enumerable.Range(0, contextLookup.Count)
            .Select(_ => new CalibrationAccumulator(hiddenWidth)).ToArray();
        for (int group = 0; group < localSums.Length; group++)
            contextSums[groupContexts[group]].Merge(localSums[group]);
        var globalFits = contextSums.Select(sum => sum.Fit(fineTuneConfig, global: true)).ToArray();
        var localFits = new Calibration[localSums.Length];
        var methodNames = new string[localSums.Length];
        for (int group = 0; group < localSums.Length; group++)
        {
            if ((group & 255) == 0) cancellation.ThrowIfCancellationRequested();
            Calibration local = localSums[group].Fit(fineTuneConfig, global: false);
            Calibration fit = local.Method != 0 ? local : globalFits[groupContexts[group]];
            localFits[group] = fit;
            methodNames[group] = (local.Method != 0 ? "" : "FALLBACK_")
                + new[] { "RAW", "BIAS", "LINEAR", "FINETUNE" }[fit.Method];
            if ((group & 255) == 255)
                progress?.Invoke(29, $"Ajuste fino: {group + 1:N0} de {localSums.Length:N0} lotes…");
        }

        progress?.Invoke(29, "Recalculando alturas com a calibração por lote…");
        var estimated = new double[rowCount];
        processedRows = 0;
        Parallel.For(1, rowCount, options,
            () => new PredictionState(model, numericalColumns.Length, categoricalColumns.Length, hiddenWidth),
            (int row, ParallelLoopState _, PredictionState state) =>
            {
                if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                if (directNumericalReads)
                {
                    for (int i = 0; i < numericalViews.Length; i++)
                        state.Numerical[i] = numericalViews[i]!.Value.Number(row);
                }
                else
                {
                    for (int i = 0; i < numericalColumns.Length; i++)
                        state.Numerical[i] = input.Number(row, numericalColumns[i]);
                }
                for (int i = 0; i < categoricalColumns.Length; i++)
                    state.Categorical[i] = input.Text(row, categoricalColumns[i]);
                double prediction = model.Predict(state.Numerical, state.Categorical,
                    state.Workspace, state.Hidden);
                estimated[row] = localFits[rowGroups[row]].Predict(prediction, state.Hidden);
                if (++state.PendingRows >= 8192)
                {
                    int batch = state.PendingRows;
                    state.PendingRows = 0;
                    int done = Interlocked.Add(ref processedRows, batch);
                    if ((done & 131_071) == 0)
                        lock (progressLock)
                            progress?.Invoke(29, $"Aplicando alturas: {done:N0} de {rowCount - 1:N0} árvores…");
                }
                return state;
            }, _ => { });
        cancellation.ThrowIfCancellationRequested();

        var heightSumsByGroup = new double[localSums.Length];
        var heightCountsByGroup = new int[localSums.Length];
        var heightSumsByContext = new double[contextSums.Length];
        var heightCountsByContext = new int[contextSums.Length];
        for (int row = 1; row < rowCount; row++)
        {
            if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            double observed = heightView?.Number(row) ?? input.Number(row, heightColumn);
            double prediction = estimated[row];
            double dap = dapView?.Number(row) ?? input.Number(row, dapColumn);
            double height = observed;
            if (!(dap > 0)) height = 0;
            if (dap > 0 && !(observed > 0) && double.IsFinite(prediction)) height = prediction;
            input.Set(row, observedColumn, observed);
            input.Set(row, estimatedColumn, prediction);
            input.Set(row, finalColumn, height);
            input.Set(row, heightColumn, height);
            int group = rowGroups[row], context = groupContexts[group];
            input.Set(row, methodColumn, methodNames[group]);
            if (dap > 0 && height > 0 && double.IsFinite(height))
            {
                heightSumsByGroup[group] += height;
                heightCountsByGroup[group]++;
                heightSumsByContext[context] += height;
                heightCountsByContext[context]++;
            }
        }

        if (!heightCountsByContext.Any(count => count > 0)) return;
        for (int row = 1; row < rowCount; row++)
        {
            if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            if (!((dapView?.Number(row) ?? input.Number(row, dapColumn)) > 0)
                || (heightView?.Number(row) ?? input.Number(row, heightColumn)) > 0) continue;
            int group = rowGroups[row], context = groupContexts[group];
            bool local = heightCountsByGroup[group] > 0;
            double height = local
                ? heightSumsByGroup[group] / heightCountsByGroup[group]
                : heightSumsByContext[context] / heightCountsByContext[context];
            input.Set(row, heightColumn, height);
            input.Set(row, finalColumn, height);
            input.Set(row, methodColumn, local ? "MEDIA_LOTE" : "MEDIA_GLOBAL");
        }
    }

    private static Calibration Fit(
        int[] rows,
        double[] observed,
        double[] predictions,
        float[] hidden,
        int hiddenWidth,
        JsonNode? config,
        bool global,
        CancellationToken cancellation,
        bool parallelize = true
    )
    {
        rows = rows.Where(r => observed[r] > 0 && double.IsFinite(predictions[r])).ToArray();
        if (rows.Length == 0)
            return new();
        int n = rows.Length, dim = hiddenWidth;
        double xsum = 0, ysum = 0;
        foreach (int r in rows)
        {
            xsum += predictions[r];
            ysum += observed[r];
        }
        double xmean = xsum / n, ymean = ysum / n;
        if (n >= config.N("min_amostras_finetune", 30) && dim is > 0 and <= 512)
        {
            int partitions = parallelize ? Math.Min(
                Math.Clamp(Environment.ProcessorCount, 1, 8),
                Math.Max(1, (int)(((long)n + 31_249) / 31_250))) : 1;
            var fitOptions = new ParallelOptions
            {
                CancellationToken = cancellation,
                MaxDegreeOfParallelism = partitions,
            };
            int start(int partition) => (int)((long)partition * n / partitions);
            int end(int partition) => (int)((long)(partition + 1) * n / partitions);
            var means = new double[dim];
            var meanParts = new double[partitions][];
            for (int part = 0; part < partitions; part++) meanParts[part] = new double[dim];
            Parallel.For(0, partitions, fitOptions, part =>
            {
                double[] local = meanParts[part];
                for (int i = start(part); i < end(part); i++)
                {
                    if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                    int offset = rows[i] * hiddenWidth;
                    for (int j = 0; j < dim; j++) local[j] += hidden[offset + j];
                }
            });
            for (int part = 0; part < partitions; part++)
                for (int j = 0; j < dim; j++) means[j] += meanParts[part][j];
            for (int j = 0; j < dim; j++) means[j] /= n;
            var matrix = new double[dim, dim];
            var rhs = new double[dim];
            var matrixParts = new double[partitions][];
            var rhsParts = new double[partitions][];
            for (int part = 0; part < partitions; part++)
            {
                matrixParts[part] = new double[checked(dim * dim)];
                rhsParts[part] = new double[dim];
            }
            Parallel.For(0, partitions, fitOptions, part =>
            {
                double[] localMatrix = matrixParts[part];
                double[] localRhs = rhsParts[part];
                for (int i = start(part); i < end(part); i++)
                {
                    if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                    int r = rows[i], offset = r * hiddenWidth;
                    double y = observed[r] - ymean;
                    for (int j = 0; j < dim; j++)
                    {
                        double x = hidden[offset + j] - means[j];
                        localRhs[j] += x * y;
                        int matrixRow = j * dim;
                        for (int k = j; k < dim; k++)
                            localMatrix[matrixRow + k] += x * (hidden[offset + k] - means[k]);
                    }
                }
            });
            for (int part = 0; part < partitions; part++)
                for (int j = 0; j < dim; j++)
                {
                    rhs[j] += rhsParts[part][j];
                    for (int k = j; k < dim; k++)
                        matrix[j, k] += matrixParts[part][j * dim + k];
                }
            for (int j = 0; j < dim; j++)
                for (int k = j + 1; k < dim; k++) matrix[k, j] = matrix[j, k];
            for (int j = 0; j < dim; j++)
                matrix[j, j] += config.N("ridge_alpha", 10);
            if (InventoryMath.Solve(matrix, rhs, out var weights))
            {
                var fit = new Calibration(
                    3,
                    ymean - means.Select((v, i) => v * weights[i]).Sum(),
                    1,
                    weights
                );
                double residual = 0, ss = 0;
                foreach (int r in rows)
                {
                    double error = observed[r] - fit.Predict(
                        predictions[r], hidden.AsSpan(r * hiddenWidth, hiddenWidth));
                    double centered = observed[r] - ymean;
                    residual += error * error;
                    ss += centered * centered;
                }
                double r2 = ss > 0 ? 1 - residual / ss
                    : residual < 1e-12 ? 1
                    : 0;
                if (n <= 1 || r2 >= (global ? 0 : .5))
                    return fit;
            }
        }
        if (n >= config.N("min_amostras_linear", 10))
        {
            double xx = rows.Sum(r => Math.Pow(predictions[r] - xmean, 2)),
                xy = rows.Sum(r => (predictions[r] - xmean) * (observed[r] - ymean));
            if (xx > 1e-15)
                return new(2, ymean - xy / xx * xmean, xy / xx);
        }
        return n >= config.N("min_amostras_bias", 1) ? new(1, ymean - xmean) : new();
    }

    public static void Apply(DataTable input, DataTable registry, JsonObject cfg, NeuralModel? loadedModel = null,
        CancellationToken cancellation = default, Action<int, string>? progress = null)
    {
        var h = cfg["ajuste_altura"]!;
        var model = loadedModel ?? NeuralModel.Load(h["modelo_geral"].S("caminho"));
        var identification = new InventoryIdentity(input, cfg);
        FeaturePreparation.Prepare(input, registry, cfg,
            model.Numerical.Concat(model.Categorical).ToHashSet(StringComparer.Ordinal));
        int[] numeric = model.Numerical.Select(input.Require).ToArray(),
            categorical = model.Categorical.Select(input.Require).ToArray();
        var cols = cfg["arquivo_entrada"]?["colunas"];
        int hc = input.Require(cols.S("altura", "H")),
            dc = input.Require(cols.S("dap", "DAP")),
            lc = input.Require(cols.S("lote", "Lote"));
        int obs = input.Ensure("H_obs"),
            est = input.Ensure("H_est"),
            final = input.Ensure("H_final"),
            method = input.Ensure("H_ajuste");
        bool fine = h.S("h_mode") == "FINETUNE_MODEL";
        if (fine && model.ModelType != "neural_net")
            throw new InvalidDataException("O ajuste fino está disponível apenas para modelos de rede neural Dense.");
        if (
            fine
            && (
                model.HiddenSize > 512
                || (long)input.Count * model.HiddenSize * 4 > 256 * 1024 * 1024
            )
        )
            throw new InvalidDataException(
                "Representação oculta excede o limite do finetune (512 variáveis, 256 MB)."
            );
        int hiddenWidth = fine ? model.HiddenSize : 0;
        int n = input.Count;
        if (fine)
        {
            ApplyFineTuneStreaming(input, model, h.AsObject(), h["finetune"], identification,
                numeric, categorical, hc, dc, lc, obs, est, final, method, cancellation, progress);
            return;
        }
        var observed = new double[n];
        var predictions = new double[n];
        double[] estimated = fine ? new double[n] : predictions;
        float[] hidden = fine ? new float[checked(n * hiddenWidth)] : [];
        int completed = 0, lastProgress = 25;
        object progressLock = new();
        void Report(int count)
        {
            if (count <= 0) return;
            int done = Interlocked.Add(ref completed, count);
            int percent = 25 + (int)(3L * done / Math.Max(1, n - 1));
            lock (progressLock)
                if (percent > lastProgress)
                {
                    lastProgress = percent;
                    progress?.Invoke(Math.Min(28, percent), $"RNA: {done:N0} de {n - 1:N0} árvores processadas…");
                }
        }
        var options = new ParallelOptions
        {
            CancellationToken = cancellation,
            MaxDegreeOfParallelism = n < 50_000 ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 8),
        };
        Parallel.For(1, n, options,
            () => new PredictionState(model, numeric.Length, categorical.Length, hiddenWidth),
            (r, _, state) =>
            {
                if ((r & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                observed[r] = input.Number(r, hc);
                for (int i = 0; i < numeric.Length; i++) state.Numerical[i] = input.Number(r, numeric[i]);
                for (int i = 0; i < categorical.Length; i++) state.Categorical[i] = input.Text(r, categorical[i]);
                predictions[r] = estimated[r] = model.Predict(
                    state.Numerical, state.Categorical, state.Workspace, fine ? state.Hidden : null);
                if (fine) Array.Copy(state.Hidden, 0, hidden, r * hiddenWidth, hiddenWidth);
                if (++state.PendingRows >= 8192)
                {
                    Report(state.PendingRows);
                    state.PendingRows = 0;
                }
                return state;
            },
            state => Report(state.PendingRows));
        cancellation.ThrowIfCancellationRequested();
        progress?.Invoke(28, "RNA concluída; aplicando alturas e calibração…");
        if (!predictions.Skip(1).Any(double.IsFinite))
            throw new InvalidDataException(
                "Nenhuma linha possui variáveis completas para executar a RNA. Revise as estatísticas e as variáveis do modelo."
            );
        if (fine)
        {
            var rows = Enumerable.Range(1, n - 1).ToArray();
            progress?.Invoke(29, "Ajustando regressões de altura por lote…");
            foreach (var scope in rows.GroupBy(r => identification.Context(r).Id))
            {
                var localFits = new List<(int[] Rows, Calibration Fit)>();
                bool needsGlobalFallback = false;
                foreach (var group in scope.GroupBy(r => input.Text(r, lc)))
                {
                    var groupRows = group.ToArray();
                    var local = Fit(groupRows, observed, predictions, hidden, hiddenWidth,
                        h["finetune"], false, cancellation, parallelize: false);
                    needsGlobalFallback |= local.Method == 0;
                    localFits.Add((groupRows, local));
                }
                Calibration global = needsGlobalFallback
                    ? Fit(scope.ToArray(), observed, predictions, hidden, hiddenWidth, h["finetune"], true, cancellation)
                    : new();
                foreach (var (groupRows, local) in localFits)
                {
                    var fit = local.Method != 0 ? local : global;
                    string name = (local.Method != 0 ? "" : "FALLBACK_") + new[] { "RAW", "BIAS", "LINEAR", "FINETUNE" }[fit.Method];
                    foreach (int r in groupRows)
                    {
                        estimated[r] = fit.Predict(predictions[r], hidden.AsSpan(r * hiddenWidth, hiddenWidth));
                        input.Set(r, method, name);
                    }
                }
            }
        }
        for (int r = 1; r < n; r++)
        {
            double measured = observed[r],
                prediction = estimated[r],
                dap = input.Number(r, dc),
                height = measured;
            if (!(dap > 0))
                height = 0;
            if (dap > 0 && !(measured > 0) && double.IsFinite(prediction))
                height = prediction;
            if (!fine && measured > 0)
                height = measured;
            input.Set(r, obs, measured);
            input.Set(r, est, prediction);
            input.Set(r, final, height);
            input.Set(r, hc, height);
            if (!fine)
                input.Set(r, method, "MODEL_GENERAL");
        }
        var valid = Enumerable
            .Range(1, n - 1)
            .Where(r => input.Number(r, dc) > 0 && input.Number(r, hc) > 0)
            .ToArray();
        if (valid.Length == 0)
            return;
        var means = valid.GroupBy(r => identification.Context(r).Id).ToDictionary(g => g.Key, g => g.Average(r => input.Number(r, hc)));
        var lots = valid
            .GroupBy(r => (Scope: identification.Context(r).Id, Lot: input.Text(r, lc)))
            .ToDictionary(
                g => g.Key,
                g => g.Average(r => input.Number(r, hc))
            );
        for (int r = 1; r < n; r++)
            if (input.Number(r, dc) > 0 && !(input.Number(r, hc) > 0))
            {
                string scope = identification.Context(r).Id;
                bool local = lots.TryGetValue((scope, input.Text(r, lc)), out double height);
                if (!local && !means.TryGetValue(scope, out height)) continue;
                input.Set(r, hc, height);
                input.Set(r, final, height);
                input.Set(r, method, local ? "MEDIA_LOTE" : "MEDIA_GLOBAL");
            }
    }
}
