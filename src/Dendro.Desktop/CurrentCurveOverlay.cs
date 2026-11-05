using System.Text.Json.Nodes;
using System.IO;
using Dendro.Core;
using Dendro.Core.Mathematics;

namespace Dendro.Desktop;

internal static class CurrentCurveOverlay
{
    private sealed record CurveModel(JsonObject Value, string Id, string Label, int Predictors, double[] Coefficients);

    internal static string Add(JsonObject project, JsonObject chart, JsonObject data, string chartName)
    {
        string title = chartName.Trim();
        if (title.Length == 0)
            return "Curva vigente não exibida: o nome do gráfico precisa ser o ID do modelo.";

        int source = Math.Clamp((int)chart.N("current_curve_source"), 0, 2);
        string sourceLabel = source switch { 1 => "Índice de sítio", 2 => "Crescimento e produção", _ => "Regressão salva" };
        CurveModel? model = Find(project, source, title);
        if (model == null)
            return $"Curva vigente não encontrada em {sourceLabel} para o ID ‘{title}’.";

        var fits = (chart["fits"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(item => item.B("converged"))
            .ToArray();
        var fit = fits.FirstOrDefault(item => item.N("predictor_count", 1) == model.Predictors)
            ?? fits.FirstOrDefault();
        if (fit == null)
            return $"Curva vigente ‘{model.Label}’ não exibida: o ajuste não gerou uma curva válida.";
        if (model.Predictors > fit.N("predictor_count", 1))
            return $"Curva vigente ‘{model.Label}’ exige {model.Predictors} variável(is), mas o gráfico foi ajustado com {fit.N("predictor_count", 1):N0}.";
        if (source == 1 && model.Value.S("source_y").Length > 0
            && !model.Value.S("source_y").Equals(chart.S("y"), StringComparison.OrdinalIgnoreCase))
            return $"Curva vigente ‘{model.Label}’ usa Y={model.Value.S("source_y")}; o gráfico usa Y={chart.S("y")}.";

        double min = fit.N("xmin", double.NaN), max = fit.N("xmax", double.NaN);
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
            return $"Curva vigente ‘{model.Label}’ não exibida: intervalo de X inválido.";

        string formulaText = model.Value.S("formula");
        Formula formula;
        try { formula = new Formula(formulaText, ["b0", "b1", "b2", "b3", "x", "x1", "x2", "x3", "t"]); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        { return $"Curva vigente ‘{model.Label}’ não exibida: {ex.Message}"; }

        var fixedInputs = model.Value["fixed_inputs"] as JsonObject;
        double x2 = fit["x2_mean"] != null ? fit.N("x2_mean") : fixedInputs?.N("x2", double.NaN) ?? double.NaN;
        double x3 = fit["x3_mean"] != null ? fit.N("x3_mean") : fixedInputs?.N("x3", double.NaN) ?? double.NaN;
        string unit = model.Value.S("unidade_idade", model.Value.S("age_unit"));
        double unitScale = AgeScale(chart.S("x"), unit);
        var points = new JsonArray();
        for (int index = 0; index <= 200; index++)
        {
            double axisX = min + (max - min) * index / 200;
            double modelX = axisX * unitScale;
            try
            {
                double y = formula.Evaluate(symbol => symbol switch
                {
                    "x" or "x1" or "t" => modelX,
                    "x2" when double.IsFinite(x2) => x2,
                    "x3" when double.IsFinite(x3) => x3,
                    _ when symbol.Length == 2 && symbol[0] == 'b' && int.TryParse(symbol.AsSpan(1), out int coefficient)
                        && coefficient >= 0 && coefficient < model.Coefficients.Length => model.Coefficients[coefficient],
                    _ => throw new InvalidDataException($"A entrada ‘{symbol}’ não está disponível no ajuste."),
                });
                if (double.IsFinite(y)) points.Add(new JsonArray(JsonValue.Create(axisX), JsonValue.Create(y)));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArithmeticException or ArgumentException)
            { return $"Curva vigente ‘{model.Label}’ não exibida: {ex.Message}"; }
        }
        if (points.Count < 2)
            return $"Curva vigente ‘{model.Label}’ não gerou pontos finitos no intervalo do gráfico.";

        data["reference_curves"] = new JsonArray(new JsonObject
        {
            ["label"] = $"Vigente · {model.Label}",
            ["color"] = "#8E44AD",
            ["points"] = points,
        });
        return "";
    }

    private static CurveModel? Find(JsonObject project, int source, string title)
    {
        JsonArray items;
        if (source == 0)
            items = project["_dendro"]?["saved_regressions"] as JsonArray ?? [];
        else if (source == 1)
            items = project["_dendro"]?["site_index_models"] as JsonArray ?? [];
        else
            items = project["curvas_crescimento"]?["equacoes"] as JsonArray ?? [];

        var models = items.OfType<JsonObject>().Where(item =>
        {
            string id = source == 0 ? item.S("id") : item.S("codigo");
            if (!id.Equals(title, StringComparison.OrdinalIgnoreCase) &&
                (source != 0 || !item.S("name").Equals(title, StringComparison.OrdinalIgnoreCase))) return false;
            if (source != 0 && !item.B("vigente", true)) return false;
            return true;
        }).ToArray();

        JsonObject? selected = source == 0
            ? models.LastOrDefault(item => item.S("id").Equals(title, StringComparison.OrdinalIgnoreCase))
                ?? models.LastOrDefault()
            : models.OrderBy(item => item.N("ano")).LastOrDefault();
        if (selected == null) return null;

        double[] coefficients = selected["coefficients"] is JsonArray savedCoefficients
            ? savedCoefficients.Select(value => JsonExtensions.Number(value)).ToArray()
            : Enumerable.Range(0, 4).Where(index => selected[$"b{index}"] != null)
                .Select(index => selected.N($"b{index}")).ToArray();
        int predictors = (int)selected.N("predictors",
            selected["entradas"] is JsonArray inputs ? inputs.Count : 1);
        string idValue = source == 0 ? selected.S("id") : selected.S("codigo");
        string label = selected.S("name", selected.S("modelo_nome", selected.S("modelo", selected.S("nome", idValue))));
        if (coefficients.Length == 0 || selected.S("formula").Length == 0) return null;
        return new(selected, idValue, label, predictors, coefficients);
    }

    private static double AgeScale(string xColumn, string modelUnit)
    {
        bool xMonths = xColumn.Contains("mes", StringComparison.OrdinalIgnoreCase);
        bool xYears = xColumn.Contains("ano", StringComparison.OrdinalIgnoreCase);
        bool modelMonths = modelUnit.Equals("meses", StringComparison.OrdinalIgnoreCase);
        bool modelYears = modelUnit.Equals("anos", StringComparison.OrdinalIgnoreCase);
        if (xMonths && modelYears) return 1.0 / 12;
        if (xYears && modelMonths) return 12;
        return 1;
    }
}
