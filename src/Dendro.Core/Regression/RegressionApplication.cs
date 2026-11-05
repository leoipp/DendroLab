using System.Globalization;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Tabular;

namespace Dendro.Core.Regression;

public sealed record SavedRegressionModel(
    string Id,
    string Name,
    int Predictors,
    string Formula,
    double[] Coefficients,
    string[] SourcePredictors
);

public sealed record RegressionApplicationSpec(
    SavedRegressionModel Model,
    string[] PredictorColumns,
    string InitialValueColumn = "",
    string[]? TargetPredictorColumns = null
);

public sealed record RegressionApplicationResult(TableResult Table, long InvalidPredictions, long Rows);

/// <summary>Stores fitted equations and applies them to new tabular data.</summary>
public static class RegressionApplication
{
    public const string StorageKey = "saved_regressions";

    public static bool CanSave(JsonObject fit) =>
        fit.B("converged")
        && fit.N("predictor_count", 1) is >= 1 and <= 3
        && fit.N("parameters") is >= 1 and <= 4
        && !string.IsNullOrWhiteSpace(fit.S("formula"));

    public static SavedRegressionModel FromFit(JsonObject chart, JsonObject fit, string name)
    {
        if (!CanSave(fit))
            throw new InvalidDataException("Salve apenas um ajuste convergido com 1 a 3 variáveis e 1 a 4 coeficientes.");
        int predictors = (int)fit.N("predictor_count", 1);
        int coefficientCount = (int)fit.N("parameters");
        var coefficients = Enumerable.Range(0, coefficientCount).Select(i => fit.N("b" + i, double.NaN)).ToArray();
        string[] sourcePredictors = chart["x_variables"] is JsonArray xVariables
            ? xVariables.Select(x => x?.GetValue<string>() ?? "").Take(predictors).ToArray()
            : [chart.S("x")];
        if (sourcePredictors.Length < predictors)
            sourcePredictors = [.. sourcePredictors, .. Enumerable.Range(sourcePredictors.Length + 1, predictors - sourcePredictors.Length).Select(i => "x" + i)];
        var model = new SavedRegressionModel(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            predictors,
            fit.S("formula"),
            coefficients,
            sourcePredictors
        );
        Validate(model);
        return model;
    }

    public static SavedRegressionModel Read(JsonObject value)
    {
        var model = new SavedRegressionModel(
            value.S("id"),
            value.S("name"),
            (int)value.N("predictors"),
            value.S("formula"),
            (value["coefficients"] as JsonArray ?? []).Select(n => JsonExtensions.Number(n)).ToArray(),
            (value["source_predictors"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "").ToArray()
        );
        Validate(model);
        return model;
    }

    public static JsonObject Write(SavedRegressionModel model)
    {
        Validate(model);
        return new JsonObject
        {
            ["id"] = model.Id,
            ["name"] = model.Name,
            ["predictors"] = model.Predictors,
            ["formula"] = model.Formula,
            ["coefficients"] = new JsonArray(model.Coefficients.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["source_predictors"] = new JsonArray(model.SourcePredictors.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["created"] = DateTimeOffset.Now.ToString("O"),
        };
    }

    public static void Validate(SavedRegressionModel model)
    {
        if (model.Id.Length is < 1 or > 64 || model.Name.Trim().Length is < 1 or > 120
            || model.Predictors is < 1 or > 3 || model.Coefficients.Length is < 1 or > 4
            || model.Coefficients.Any(v => !double.IsFinite(v))
            || model.SourcePredictors.Length != model.Predictors)
            throw new InvalidDataException("A regressão salva tem identificação, preditores ou coeficientes inválidos.");
        _ = new Formula(model.Formula, Symbols(model));
    }

    public static void ValidateCatalog(JsonArray models)
    {
        if (models.Count > 1000)
            throw new InvalidDataException("O catálogo permite até 1.000 regressões ajustadas por projeto.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in models)
        {
            if (node is not JsonObject value)
                throw new InvalidDataException("Há uma regressão salva inválida no projeto.");
            var model = Read(value);
            if (!ids.Add(model.Id))
                throw new InvalidDataException("O catálogo contém IDs de regressão duplicados.");
        }
    }

    public static RegressionApplicationResult Apply(
        TableSource source,
        IReadOnlyList<RegressionApplicationSpec> specifications,
        string folder,
        string tableName,
        CancellationToken cancellation = default,
        IProgress<TableProgress>? progress = null,
        bool projection = false
    )
    {
        if (specifications.Count is < 1 or > 100)
            throw new InvalidDataException("Selecione de 1 a 100 regressões para aplicar.");
        using var cursor = new TableCursor(source);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        string[] outputHeaders = EnsureVisibleUniqueHeaders(cursor.Columns);
        var usedColumns = outputHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(SavedRegressionModel Model, int[] Indexes, int[] TargetIndexes, Formula Formula, string Output, int InitialValueIndex)>();
        foreach (var specification in specifications)
        {
            Validate(specification.Model);
            if (!usedIds.Add(specification.Model.Id))
                throw new InvalidDataException("Uma regressão foi selecionada mais de uma vez.");
            if (specification.PredictorColumns.Length != specification.Model.Predictors)
                throw new InvalidDataException($"Mapeie X1 a X{specification.Model.Predictors} para {specification.Model.Name}.");
            int[] indexes = specification.PredictorColumns.Select(column => Array.IndexOf(cursor.Columns, column)).ToArray();
            if (indexes.Any(i => i < 0))
                throw new InvalidDataException($"Uma coluna explicativa de {specification.Model.Name} não existe na base selecionada.");
            int initialValueIndex = -1;
            int[] targetIndexes = [];
            if (projection)
            {
                initialValueIndex = Array.IndexOf(cursor.Columns, specification.InitialValueColumn);
                if (initialValueIndex < 0 || specification.TargetPredictorColumns?.Length != specification.Model.Predictors)
                    throw new InvalidDataException($"Mapeie Y1 e os preditores da ocasião i2 para projetar {specification.Model.Name}.");
                targetIndexes = specification.TargetPredictorColumns.Select(column => Array.IndexOf(cursor.Columns, column)).ToArray();
                if (targetIndexes.Any(i => i < 0))
                    throw new InvalidDataException($"Mapeie todos os preditores da ocasião i2 para projetar {specification.Model.Name}.");
            }
            string baseName = TableTools.SuggestedName((projection ? "Proj_" : "Prev_") + specification.Model.Name);
            string output = baseName;
            for (int suffix = 2; !usedColumns.Add(output); suffix++)
                output = TableTools.SuggestedName(baseName + "_" + suffix);
            planned.Add((specification.Model, indexes, targetIndexes, new Formula(specification.Model.Formula, Symbols(specification.Model)), output, initialValueIndex));
        }

        string outputPath = Path.Combine(folder, $"{Guid.NewGuid():N}.dlb");
        long rows = 0, invalidPredictions = 0;
        var reporter = new TableProgressReporter(progress);
        try
        {
            using (var writer = new CompactBankWriter(outputPath))
            {
                string table = writer.AddTable([.. outputHeaders, .. planned.Select(p => p.Output)], TableTools.SuggestedName(tableName));
                foreach (var sourceRow in cursor.Rows())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var row = new object?[sourceRow.Length + planned.Count];
                    Array.Copy(sourceRow, row, sourceRow.Length);
                    for (int index = 0; index < planned.Count; index++)
                    {
                        var item = planned[index];
                        var values = new double[item.Model.Predictors];
                        bool valid = true;
                        for (int predictor = 0; predictor < values.Length; predictor++)
                            if (!TryNumber(sourceRow[item.Indexes[predictor]], out values[predictor]))
                            {
                                valid = false;
                                break;
                            }
                        double Evaluate(double[] inputs) => item.Formula.Evaluate(symbol =>
                            symbol switch
                            {
                                "x" or "x1" or "t" => inputs[0],
                                "x2" => inputs[1],
                                "x3" => inputs[2],
                                _ => item.Model.Coefficients[int.Parse(symbol[1..], CultureInfo.InvariantCulture)],
                            });
                        double predicted = double.NaN;
                        if (valid && projection)
                        {
                            double initialValue = double.NaN;
                            valid = TryNumber(sourceRow[item.InitialValueIndex], out initialValue);
                            var targetValues = new double[values.Length];
                            for (int predictor = 0; valid && predictor < targetValues.Length; predictor++)
                                valid = TryNumber(sourceRow[item.TargetIndexes[predictor]], out targetValues[predictor]);
                            if (valid)
                            {
                                double f1 = Evaluate(values);
                                double f2 = Evaluate(targetValues);
                                if (double.IsFinite(initialValue) && double.IsFinite(f1) && f1 != 0 && double.IsFinite(f2))
                                    predicted = initialValue * f2 / f1;
                            }
                        }
                        else if (valid)
                            predicted = Evaluate(values);
                        if (double.IsFinite(predicted)) row[sourceRow.Length + index] = predicted;
                        else { row[sourceRow.Length + index] = null; invalidPredictions++; }
                    }
                    writer.AddOwned(table, row);
                    rows++;
                    if ((rows & 2047) == 0)
                        reporter.Report(cursor.TotalRows > 0 ? 90d * rows / cursor.TotalRows.Value : null,
                            $"Aplicando regressões · {rows:N0} linhas", rows);
                }
                cancellation.ThrowIfCancellationRequested();
                writer.Complete();
            }
            reporter.Report(100, $"Aplicação concluída · {rows:N0} linhas", rows, true);
            return new(new(outputPath, TableTools.SuggestedName(tableName), rows), invalidPredictions, rows);
        }
        catch
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }

    private static string[] Symbols(SavedRegressionModel model) =>
        [.. Enumerable.Range(0, model.Coefficients.Length).Select(i => "b" + i),
            .. Enumerable.Range(1, model.Predictors).Select(i => "x" + i), "x", "x1", "t"];

    private static string[] EnsureVisibleUniqueHeaders(string[] headers)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new string[headers.Length];
        for (int index = 0; index < headers.Length; index++)
        {
            string candidate = new((headers[index] ?? "")
                .Where(character => !char.IsControl(character)
                    && char.GetUnicodeCategory(character) != UnicodeCategory.Format)
                .ToArray());
            candidate = candidate.Trim();
            if (candidate.Length == 0) candidate = $"Campo_{index + 1}";
            string unique = candidate;
            for (int suffix = 2; !used.Add(unique); suffix++)
                unique = $"{candidate}_{suffix}";
            result[index] = unique;
        }
        return result;
    }

    private static bool TryNumber(object? value, out double number)
    {
        if (value is double d && double.IsFinite(d)) { number = d; return true; }
        if (value == null) { number = double.NaN; return false; }
        string text = TableCursor.Text(value).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number) && double.IsFinite(number);
    }
}
