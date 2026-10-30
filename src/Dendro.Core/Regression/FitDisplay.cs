using System.Text.Json.Nodes;

namespace Dendro.Core.Regression;

/// <summary>Shared curve selection and styling for the screen and exported charts.</summary>
public static class FitDisplay
{
    private static readonly string[] Colors =
    [
        "#21669D",
        "#1C8965",
        "#D56D2C",
        "#8155B4",
        "#C33E5E",
        "#008A99",
        "#B85BA0",
        "#5555A4",
        "#788C27",
        "#9D4C36",
        "#378FAE",
        "#747B42",
        "#4F6375",
    ];

    public static int[] Visible(JsonObject chart)
    {
        var fits = chart["fits"] as JsonArray ?? [];
        if (chart["selected_fits"] is JsonArray selected)
            return selected
                .Select(n => (int)JsonExtensions.Number(n))
                .Distinct()
                .Where(i => i >= 0 && i < fits.Count && fits[i].B("converged"))
                .Order()
                .ToArray();
        int legacy = (int)chart.N("selected_fit", -1);
        if (legacy >= 0)
            return legacy < fits.Count && fits[legacy].B("converged") ? [legacy] : [];
        var groups = new HashSet<(string, int)>();
        return Enumerable
            .Range(0, fits.Count)
            .Where(i =>
                fits[i].B("converged")
                && groups.Add((fits[i].S("group"), (int)fits[i].N("frontier")))
            )
            .ToArray();
    }

    public static string Color(JsonNode fit)
    {
        string explicitColor = fit.S("curve_color");
        if (
            explicitColor.Length == 7
            && explicitColor[0] == '#'
            && explicitColor.Skip(1).All(Uri.IsHexDigit)
        )
            return explicitColor;
        int index = Array.FindIndex(
            RegressionEngine.Models,
            m =>
                m.Name == fit.S("model")
                || fit.S("formula").Replace("x1", "x", StringComparison.Ordinal) == m.Formula
        );
        if (index >= 0)
            return Colors[index % Colors.Length];
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                fit.S("model_id") + fit.S("model") + fit.S("formula")
            )
        );
        return $"#{40 + hash[0] % 140:X2}{55 + hash[1] % 120:X2}{60 + hash[2] % 130:X2}";
    }
}
