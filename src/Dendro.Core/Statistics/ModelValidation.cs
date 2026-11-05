using System.Globalization;
using Dendro.Core.Tabular;

namespace Dendro.Core.Statistics;

public sealed record ModelValidationMetrics(
    string EstimateColumn,
    long N,
    long Omitted,
    long RelativeErrorN,
    double? R2,
    double? MeanErrorPercent,
    double? MeanAbsoluteError,
    double? Bias,
    double? Rmse,
    double? MeanAbsolutePercentError
);

public sealed record ModelValidationPoint(double Observed, double Estimated);

public sealed record ModelValidationHistogramBin(double Lower, double Upper, double Center, long Count, double RelativeFrequency);

public sealed record ModelValidationSeries(
    ModelValidationMetrics Metrics,
    ModelValidationPoint[] ScatterSample,
    ModelValidationHistogramBin[] Histogram
);

public sealed record ModelValidationReport(
    string ObservedColumn,
    ModelValidationSeries[] Series,
    int HistogramBinCount,
    int ScatterPointLimit
);

/// <summary>Descriptive validation of one or more estimated columns against observed values.</summary>
public static class ModelValidation
{
    private const int DefaultHistogramBins = 20;
    private const int DefaultScatterPointLimit = 20000;

    private sealed class Accumulator(string name, int sampleLimit, int seed)
    {
        private readonly Random random = new(seed);
        private readonly List<ModelValidationPoint> sample = new(sampleLimit);
        public string Name { get; } = name;
        public long N { get; private set; }
        public long Omitted { get; set; }
        public long RelativeN { get; private set; }
        public double MeanObserved { get; private set; }
        public double ObservedM2 { get; private set; }
        public double SumSquareError { get; private set; }
        public double SumAbsoluteError { get; private set; }
        public double SumError { get; private set; }
        public double SumRelativeError { get; private set; }
        public double SumAbsoluteRelativeError { get; private set; }
        public IReadOnlyList<ModelValidationPoint> Sample => sample;

        public void Add(double observed, double estimated)
        {
            double error = estimated - observed;
            N++;
            double delta = observed - MeanObserved;
            MeanObserved += delta / N;
            ObservedM2 += delta * (observed - MeanObserved);
            SumSquareError += error * error;
            SumAbsoluteError += Math.Abs(error);
            SumError += error;
            if (observed != 0)
            {
                double relative = error / observed * 100;
                if (double.IsFinite(relative))
                {
                    RelativeN++;
                    SumRelativeError += relative;
                    SumAbsoluteRelativeError += Math.Abs(relative);
                }
            }

            var point = new ModelValidationPoint(observed, estimated);
            if (sample.Count < sampleLimit) sample.Add(point);
            else
            {
                long index = random.NextInt64(N);
                if (index < sampleLimit) sample[(int)index] = point;
            }
        }

        public ModelValidationMetrics Finish() => new(
            Name,
            N,
            Omitted,
            RelativeN,
            N > 0 && ObservedM2 > 0 ? 1 - SumSquareError / ObservedM2 : null,
            RelativeN > 0 ? SumRelativeError / RelativeN : null,
            N > 0 ? SumAbsoluteError / N : null,
            N > 0 ? SumError / N : null,
            N > 0 ? Math.Sqrt(SumSquareError / N) : null,
            RelativeN > 0 ? SumAbsoluteRelativeError / RelativeN : null
        );
    }

    public static ModelValidationReport Run(
        TableSource source,
        string observedColumn,
        IReadOnlyList<string> estimateColumns,
        CancellationToken cancellation = default,
        int histogramBins = DefaultHistogramBins,
        int scatterPointLimit = DefaultScatterPointLimit
    )
    {
        if (estimateColumns.Count is < 1 or > 100)
            throw new InvalidDataException("Selecione de 1 a 100 colunas estimadas.");
        if (histogramBins is < 5 or > 100 || scatterPointLimit < 100)
            throw new ArgumentOutOfRangeException(nameof(histogramBins));

        var requestedColumns = new[] { observedColumn }.Concat(estimateColumns)
            .Distinct(StringComparer.Ordinal).ToArray();
        using var cursor = TableCursor.Projected(source, requestedColumns);
        int observedIndex = Array.IndexOf(cursor.Columns, observedColumn);
        if (observedIndex < 0) throw new InvalidDataException("Selecione a coluna de valores observados (Y).");
        string[] estimates = estimateColumns.ToArray();
        if (estimates.Any(string.IsNullOrWhiteSpace)
            || estimates.Contains(observedColumn, StringComparer.Ordinal)
            || estimates.Distinct(StringComparer.Ordinal).Count() != estimates.Length)
            throw new InvalidDataException("As colunas estimadas devem ser distintas de Y e não podem estar repetidas.");
        int[] estimateIndexes = estimates.Select(name => Array.IndexOf(cursor.Columns, name)).ToArray();
        if (estimateIndexes.Any(index => index < 0)) throw new InvalidDataException("Uma coluna estimada não existe na tabela selecionada.");

        var accumulators = estimates.Select((name, index) => new Accumulator(name, scatterPointLimit, 712367 + index * 104729)).ToArray();
        var counts = estimates.Select(_ => new long[histogramBins]).ToArray();
        static bool TryNumber(object? value, out double number)
        {
            string text = TableCursor.Text(value).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number) && double.IsFinite(number);
        }

        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            for (int model = 0; model < accumulators.Length; model++)
            {
                if (TryNumber(row.ElementAtOrDefault(observedIndex), out double observed)
                    && TryNumber(row.ElementAtOrDefault(estimateIndexes[model]), out double estimated))
                {
                    accumulators[model].Add(observed, estimated);
                    if (observed != 0)
                    {
                        double relative = (estimated - observed) / observed;
                        if (double.IsFinite(relative) && relative >= -1 && relative <= 1)
                        {
                            int bin = (int)((relative + 1) / 2 * histogramBins);
                            counts[model][Math.Clamp(bin, 0, histogramBins - 1)]++;
                        }
                    }
                }
                else accumulators[model].Omitted++;
            }
        }

        var series = accumulators.Select((accumulator, model) =>
        {
            const double min = -1, max = 1;
            double width = (max - min) / histogramBins;
            long inRange = counts[model].Sum();
            var bins = Enumerable.Range(0, histogramBins).Select(index =>
            {
                long count = counts[model][index];
                double lower = min + index * width;
                double upper = min + (index + 1) * width;
                double frequency = inRange > 0 ? (double)count / inRange : 0;
                return new ModelValidationHistogramBin(lower, upper, (lower + upper) / 2, count, frequency);
            }).ToArray();
            return new ModelValidationSeries(accumulator.Finish(), accumulator.Sample.ToArray(), bins);
        }).ToArray();
        if (series.All(item => item.Metrics.N == 0))
            throw new InvalidDataException("Não há pares numéricos válidos entre Y observado e as colunas estimadas.");
        return new(observedColumn, series, histogramBins, scatterPointLimit);
    }
}
