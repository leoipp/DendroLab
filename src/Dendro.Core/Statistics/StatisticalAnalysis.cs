using System.Globalization;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Statistics;

public enum StatisticalTest
{
    Identity,
    Welch,
    PairedT,
    VarianceF,
    Anova,
    BrownForsythe,
}

public sealed record StatisticalRequest(
    StatisticalTest Test,
    string ValueColumn,
    string SecondColumn = "",
    string GroupColumn = "",
    string[]? Groups = null,
    string XColumn = "",
    RegressionFamily? Model = null,
    double Alpha = .05
);

public static class StatisticalAnalysis
{
    public static AnalysisReport Run(
        TableSource source,
        StatisticalRequest request,
        CancellationToken cancellation = default
    )
    {
        HypothesisTests.ValidateAlpha(request.Alpha);
        using var cursor = new TableCursor(source);
        int Require(string name)
        {
            int index = Array.IndexOf(cursor.Columns, name);
            return index >= 0
                ? index
                : throw new InvalidDataException($"Selecione uma coluna válida: {name}.");
        }
        int valueColumn = Require(request.ValueColumn);
        bool paired = request.Test == StatisticalTest.PairedT;
        bool grouped = !paired && request.GroupColumn.Length > 0;
        if (
            request.Test
                is StatisticalTest.Identity
                    or StatisticalTest.Anova
                    or StatisticalTest.BrownForsythe
            && !grouped
        )
            throw new InvalidDataException("Este teste exige uma coluna de grupos.");
        int second = !grouped ? Require(request.SecondColumn) : -1,
            gc = grouped ? Require(request.GroupColumn) : -1,
            xc = request.Test == StatisticalTest.Identity ? Require(request.XColumn) : -1;
        var selected = request.Groups?.ToHashSet(StringComparer.Ordinal);
        var moments = new Dictionary<string, SampleMoments>(StringComparer.Ordinal);
        var values = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var identity = new DataTable(["X", "Y", "Grupo"]);
        var differences = new SampleMoments();
        var a = new SampleMoments();
        var b = new SampleMoments();
        long omitted = 0,
            omittedA = 0,
            omittedB = 0;
        static bool Number(object? value, out double number) =>
            double.TryParse(
                TableCursor.Text(value).Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number
            ) && double.IsFinite(number);
        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            if (grouped)
            {
                string group = TableCursor.Text(row[gc]);
                if (selected is { Count: > 0 } && !selected.Contains(group))
                    continue;
                if (!Number(row[valueColumn], out double value))
                {
                    omitted++;
                    continue;
                }
                double x = 0;
                if (xc >= 0 && !Number(row[xc], out x))
                {
                    omitted++;
                    continue;
                }
                if (!moments.TryGetValue(group, out var sample))
                {
                    if (moments.Count >= 25)
                        throw new InvalidDataException(
                            "Selecione no máximo 25 grupos por análise."
                        );
                    moments[group] = sample = new();
                    values[group] = new();
                }
                sample.Add(value);
                if (request.Test == StatisticalTest.BrownForsythe)
                    values[group].Add(value);
                if (xc >= 0)
                    identity.Add(x, value, group);
            }
            else
            {
                bool validA = Number(row[valueColumn], out double va),
                    validB = Number(row[second], out double vb);
                if (paired)
                {
                    if (validA && validB)
                        differences.Add(va - vb);
                    else
                        omitted++;
                }
                else
                {
                    if (validA)
                        a.Add(va);
                    else
                        omittedA++;
                    if (validB)
                        b.Add(vb);
                    else
                        omittedB++;
                }
            }
        }
        if (request.Test == StatisticalTest.Identity)
        {
            var report = ModelIdentity.Test(
                identity,
                "X",
                "Y",
                "Grupo",
                request.Model
                    ?? throw new InvalidDataException("Selecione o modelo 1P do projeto."),
                request.Alpha
            );
            report.Summary["omitted"] = omitted;
            report.Plot!.Chart["x_label"] = request.XColumn;
            report.Plot.Chart["y_label"] = request.ValueColumn;
            return report;
        }
        var details = new DataTable(["Amostra / grupo", "N", "Média", "Variância"]);
        if (grouped)
        {
            if (
                request.Test is StatisticalTest.Welch or StatisticalTest.VarianceF
                && moments.Count != 2
            )
                throw new InvalidDataException("Selecione exatamente dois grupos para este teste.");
            var ordered = (
                request.Groups is { Length: > 0 }
                    ? request.Groups.Where(moments.ContainsKey)
                    : moments.Keys
            )
                .Distinct()
                .Select(key => new KeyValuePair<string, SampleMoments>(key, moments[key]))
                .ToArray();
            foreach (var entry in ordered)
                details.Add(
                    entry.Key.Length == 0 ? "(vazio)" : entry.Key,
                    (double)entry.Value.Count,
                    entry.Value.Mean,
                    entry.Value.Variance
                );
            var samples = ordered.Select(p => p.Value).ToArray();
            if (samples.Length < 2)
                throw new InvalidDataException("É necessário ter pelo menos dois grupos válidos.");
            a = samples[0];
            b = samples[1];
        }
        else if (paired)
            details.Add(
                "Diferenças A − B",
                (double)differences.Count,
                differences.Mean,
                differences.Variance
            );
        else
        {
            details.Add(request.ValueColumn, (double)a.Count, a.Mean, a.Variance);
            details.Add(request.SecondColumn, (double)b.Count, b.Mean, b.Variance);
        }
        var result = request.Test switch
        {
            StatisticalTest.Welch => HypothesisTests.Welch(a, b, request.Alpha),
            StatisticalTest.PairedT => HypothesisTests.Paired(differences, request.Alpha),
            StatisticalTest.VarianceF => HypothesisTests.Variances(a, b, request.Alpha),
            StatisticalTest.Anova => HypothesisTests.Anova(moments.Values.ToArray(), request.Alpha),
            StatisticalTest.BrownForsythe => HypothesisTests.BrownForsythe(
                values.Values.Select(v => v.ToArray()).ToArray(),
                request.Alpha
            ),
            _ => throw new InvalidDataException("Teste não disponível."),
        };
        result["omitted"] = omitted;
        result["omitted_a"] = omittedA;
        result["omitted_b"] = omittedB;
        if (
            request.Test
            is StatisticalTest.Welch
                or StatisticalTest.VarianceF
                or StatisticalTest.PairedT
        )
        {
            result["sample_a"] = grouped ? details.Text(1, 0) : request.ValueColumn;
            result["sample_b"] = grouped ? details.Text(2, 0) : request.SecondColumn;
        }
        return new(result, details);
    }
}
