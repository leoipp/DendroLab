using System.Text.Json.Nodes;
using Dendro.Core.Regression;

namespace Dendro.Core.Processing;

public sealed record TaperBuckingResult(double Total, double Commercial, int AcceptedSections, int DiscardedSections);

/// <summary>Applies a frozen taper fit and log bucking rules to one measured tree.</summary>
public sealed class TaperBuckingCalculator
{
    private readonly TaperModel model;
    private readonly Dictionary<string, double[]> fits;
    private readonly double stump, maxLength, minLength, minDiameter;
    private readonly bool discardShort;
    private readonly string stratumColumn;

    public TaperBuckingCalculator(JsonObject config)
    {
        var settings = config.At("processador.taper") as JsonObject
            ?? throw new InvalidDataException("As configurações de taper do processamento estão ausentes.");
        if (!settings.B("enabled")) throw new InvalidDataException("O cálculo de taper não está ativado.");
        string modelId = settings.S("model");
        var definitions = TaperAdjustment.ReadModels(config);
        var definition = definitions.FirstOrDefault(item => item.Id == modelId)
            ?? throw new InvalidDataException("O modelo de taper selecionado não existe mais na configuração.");
        model = definition with { Formula = settings.S("formula", definition.Formula) };
        stump = settings.N("stump_m", .3);
        maxLength = settings.N("section_length_m", 2.2);
        minLength = settings.N("minimum_length_m", 1.0);
        minDiameter = settings.N("minimum_diameter_cm", 0);
        discardShort = settings.B("discard_short", true);
        stratumColumn = settings.S("stratum_column");
        fits = new(StringComparer.Ordinal);
        foreach (var fit in settings.Arr("fits").OfType<JsonObject>())
        {
            string group = fit.S("group", "Global");
            double[] coefficients = fit.Arr("coefficients").Select(value => value?.GetValue<double>() ?? double.NaN).ToArray();
            if (fit.S("status") != "OK" || coefficients.Length != model.Parameters.Length || coefficients.Any(value => !double.IsFinite(value))) continue;
            fits[group] = coefficients;
        }
        if (fits.Count == 0) throw new InvalidDataException("O ajuste de taper não contém coeficientes válidos.");
        if (stump < 0 || maxLength <= 0 || minLength <= 0 || minDiameter < 0
            || !OneDecimal(stump) || !OneDecimal(maxLength) || !OneDecimal(minLength))
            throw new InvalidDataException("Altura do toco e comprimentos devem ser válidos e usar incrementos de 0,1 m; Dmin deve ser zero ou maior.");
        if (stratumColumn.Length == 0 && !fits.ContainsKey("Global"))
            throw new InvalidDataException("O ajuste de taper é estratificado, mas não tem coluna de estrato mapeada.");
    }

    public string StratumColumn => stratumColumn;

    public TaperBuckingResult Calculate(double dapCm, double totalHeightM, string stratum)
    {
        if (!double.IsFinite(dapCm) || dapCm <= 0 || !double.IsFinite(totalHeightM) || totalHeightM <= 1.3 || stump >= totalHeightM)
            return new(double.NaN, double.NaN, 0, 0);
        string group = stratumColumn.Length == 0 ? "Global" : stratum.Trim();
        if (!fits.TryGetValue(group, out var coefficients))
            throw new InvalidDataException($"O ajuste de taper não contém o estrato '{group}'. Confira a coluna de estrato ou selecione um ajuste compatível com todos os inventários.");
        double Diameter(double at)
        {
            if (at >= totalHeightM - 1e-9) return 0;
            double value = TaperAdjustment.Predict(model, coefficients, dapCm, totalHeightM, at);
            if (!double.IsFinite(value) || value < 0) throw new InvalidDataException($"O taper gerou diâmetro inválido a {at:G3} m para o estrato '{group}'.");
            return value;
        }
        double total = 0, commercial = 0;
        int accepted = 0, discarded = 0;
        for (double start = stump; start < totalHeightM - 1e-9;)
        {
            double proposedEnd = start + maxLength;
            // Keep the measured tree top exactly. Rounding a short final remainder
            // to one decimal could round back to `start`, leaving this loop stuck.
            double nominalEnd = proposedEnd >= totalHeightM - 1e-9
                ? totalHeightM
                : Math.Round(proposedEnd, 1);
            if (nominalEnd <= start + 1e-9)
                nominalEnd = Math.Min(totalHeightM, proposedEnd);
            if (!double.IsFinite(nominalEnd) || nominalEnd <= start + 1e-9)
                throw new InvalidDataException($"O seccionamento não avançou na altura {start:G6} m para uma árvore de {totalHeightM:G6} m.");
            double end = nominalEnd;
            double dStart = Diameter(start), nominalTop = nominalEnd >= totalHeightM - 1e-9 ? 0 : Diameter(nominalEnd);
            if (minDiameter > 0 && dStart >= minDiameter - 1e-9 && nominalTop < minDiameter - 1e-9)
            {
                double shortest = discardShort ? minLength : .1;
                double candidateTop = Math.Floor((nominalEnd + 1e-9) * 10) / 10;
                if (candidateTop >= nominalEnd - 1e-9) candidateTop = Math.Round(candidateTop - .1, 1);
                for (double candidate = candidateTop; candidate >= start + shortest - 1e-9; candidate = Math.Round(candidate - .1, 1))
                    if (Diameter(candidate) >= minDiameter - 1e-9) { end = candidate; break; }
            }
            double length = end - start, dEnd = end >= totalHeightM - 1e-9 ? 0 : Diameter(end);
            if (!double.IsFinite(length) || length <= 1e-9)
                throw new InvalidDataException($"O seccionamento gerou uma tora sem comprimento na altura {start:G6} m.");
            bool usable = dEnd >= minDiameter - 1e-9 && !(discardShort && length < minLength - 1e-9);
            double sectionVolume = 0;
            for (double h = start; h < end - 1e-9;)
            {
                double next = Math.Min(end, h + .1), d1 = Diameter(h), d2 = next >= totalHeightM - 1e-9 ? 0 : Diameter(next);
                sectionVolume += Math.PI / 40000 * ((d1 * d1 + d2 * d2) / 2) * (next - h);
                h = next;
            }
            if (!double.IsFinite(sectionVolume) || sectionVolume < 0) throw new InvalidDataException("O taper gerou volume inválido.");
            total += sectionVolume;
            if (usable) { commercial += sectionVolume; accepted++; } else discarded++;
            start = end;
        }
        return new(total, commercial, accepted, discarded);
    }

    private static bool OneDecimal(double value) => Math.Abs(value * 10 - Math.Round(value * 10)) < 1e-8;
}
