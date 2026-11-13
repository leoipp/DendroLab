using System.IO;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

/// <summary>Builds nine anamorphic site-index curves around a registered guide equation.</summary>
internal static class SiteIndexCurveOverlay
{
    private static readonly string[] Colors = ["#277DA1", "#43AA8B", "#F9C74F"];
    private static readonly string[] Classes = ["Classe 1 · inferior", "Classe 2 · intermediária", "Classe 3 · superior"];

    internal static void Add(JsonObject chart, JsonObject data, JsonObject model, double referenceAge, double deviationPercent)
    {
        if (!chart.S("y").Equals(model.S("source_y"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A equação selecionada usa Y={model.S("source_y")}, mas o gráfico usa Y={chart.S("y")}.");
        if (!double.IsFinite(referenceAge) || referenceAge <= 0)
            throw new InvalidOperationException("A idade-base deve ser positiva.");
        if (!double.IsFinite(deviationPercent) || deviationPercent <= 0 || deviationPercent > 90)
            throw new InvalidOperationException("O desvio máximo deve ficar entre 0 e 90%.");

        var coefficients = (model["coefficients"] as JsonArray ?? []).Select(value => JsonExtensions.Number(value)).ToArray();
        var formula = new Formula(model.S("formula"), Enumerable.Range(0, coefficients.Length).Select(i => "b" + i).Concat(["x", "x1", "t"]));
        string unit = model.S("age_unit", "anos");
        if (unit is not ("anos" or "meses")) throw new InvalidDataException("A unidade de idade da equação deve ser anos ou meses.");
        double scale = unit == "meses" ? 12 : 1;
        double axisScale = AgeScale(chart.S("x"), unit);
        double baseX = referenceAge * scale;
        double baseY = Evaluate(baseX);
        if (!double.IsFinite(baseY) || baseY <= 0) throw new InvalidOperationException("A equação não retorna um valor positivo na idade-base informada.");

        var points = ChartPointView.Open(data, chart).Points().Where(p => !p.Excluded && double.IsFinite(p.X)).Select(p => p.X).ToArray();
        if (points.Length == 0) throw new InvalidOperationException("O gráfico não tem pontos válidos para definir o intervalo de idade.");
        double min = chart.N("xmin", points.Min()), max = chart.N("xmax", points.Max());
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) throw new InvalidOperationException("O intervalo de idade do gráfico é inválido.");

        var curves = new JsonArray();
        for (int i = 0; i < 9; i++)
        {
            int classIndex = i / 3;
            double deviation = -deviationPercent + 2 * deviationPercent * i / 8;
            double targetAtBase = baseY * (1 + deviation / 100);
            var curvePoints = new JsonArray();
            for (int step = 0; step <= 200; step++)
            {
                double axisX = min + (max - min) * step / 200;
                double y = Evaluate(axisX * axisScale) * targetAtBase / baseY;
                if (double.IsFinite(y)) curvePoints.Add(new JsonArray(axisX, y));
            }
            curves.Add(new JsonObject
            {
                ["label"] = $"{Classes[classIndex]} · curva {i + 1}",
                ["color"] = Colors[classIndex],
                ["points"] = curvePoints,
            });
        }
        data["reference_curves"] = curves;
        chart["site_index_overlay"] = true;
        chart["site_index_model"] = model.S("codigo");
        chart["site_index_reference_age"] = referenceAge;
        chart["site_index_deviation_percent"] = deviationPercent;

        double Evaluate(double age) => formula.Evaluate(symbol => symbol switch
        {
            "x" or "x1" or "t" => age,
            _ when symbol.Length > 1 && symbol[0] == 'b' && int.TryParse(symbol.AsSpan(1), out int index)
                && index >= 0 && index < coefficients.Length => coefficients[index],
            _ => throw new InvalidDataException($"Símbolo não reconhecido na equação: {symbol}."),
        });
    }

    private static double AgeScale(string xColumn, string modelUnit)
    {
        bool xMonths = xColumn.Contains("mes", StringComparison.OrdinalIgnoreCase);
        bool xYears = xColumn.Contains("ano", StringComparison.OrdinalIgnoreCase);
        if (xMonths && modelUnit == "anos") return 1.0 / 12;
        if (xYears && modelUnit == "meses") return 12;
        return 1;
    }
}
