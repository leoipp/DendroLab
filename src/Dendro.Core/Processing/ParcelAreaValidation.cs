using System.Globalization;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

public static class ParcelAreaValidation
{
    public const string DiagnosticsKey = "Dendro.ParcelAreaIssues";
    private readonly record struct IssueKey(string Stand, string Plot, string Lot, string Date, string Length, string Width,
        string SlopeLength, string SlopeWidth, string Source);

    public static double[] Compute(DataTable input, System.Text.Json.Nodes.JsonObject cfg, CancellationToken cancellation = default,
        Action<int, string>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var columns = cfg.At("arquivo_entrada.colunas");
        int length = input.Require(columns.S("comprimento_parcela", "Compr")), width = input.Require(columns.S("largura_parcela", "Larg")),
            slopeLength = input.Require(columns.S("declinacao_comprimento", "Dec_Compr")), slopeWidth = input.Require(columns.S("declinacao_largura", "Dec_Larg")),
            stand = input.Require(columns.S("talhao", "Talhão")), plot = input.Require(columns.S("parcela", "Parcela")),
            lot = input.Require(columns.S("lote", "Lote")), date = input.Require(columns.S("data_medicao", "Data")), source = input.Column("Fonte");
        var areas = new double[input.Count];
        bool blankLengthZero = cfg.At("arquivo_entrada.declinacoes").B("comprimento_vazio_zero"), blankWidthZero = cfg.At("arquivo_entrada.declinacoes").B("largura_vazio_zero");
        var issues = new Dictionary<IssueKey, (int First, long Count, double Area, string Reason)>();
        for (int row = 1; row < input.Count; row++)
        {
            if ((row & 8191) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(17, $"Verificando área das parcelas: {row:N0} de {input.Count - 1:N0} linhas · {issues.Count:N0} pendências…");
            }
            if (blankLengthZero && string.IsNullOrWhiteSpace(input.Text(row, slopeLength))) input.Set(row, slopeLength, 0d);
            if (blankWidthZero && string.IsNullOrWhiteSpace(input.Text(row, slopeWidth))) input.Set(row, slopeWidth, 0d);
            double l = input.Number(row, length), w = input.Number(row, width), sl = input.Number(row, slopeLength), sw = input.Number(row, slopeWidth);
            double area = areas[row] = InventoryMath.Area(l, w, sl, sw);
            if (area > 0 && double.IsFinite(area)) continue;
            var reasons = new List<string>();
            void Check(double value, string name, bool positive)
            {
                if (!double.IsFinite(value)) reasons.Add(name + " vazio, não numérico ou fora do intervalo.");
                else if (positive && value <= 0) reasons.Add(name + " menor ou igual a zero.");
            }
            Check(l, "Comprimento", true); Check(w, "Largura", true); Check(sl, "Declinação no comprimento", false); Check(sw, "Declinação na largura", false);
            if (reasons.Count == 0) reasons.Add(double.IsFinite(area) ? "A correção pelas declinações resultou em área nula ou negativa." : "O cálculo resultou em área não finita.");
            var key = new IssueKey(input.Text(row, stand), input.Text(row, plot), input.Text(row, lot), input.Text(row, date),
                input.Text(row, length), input.Text(row, width), input.Text(row, slopeLength), input.Text(row, slopeWidth), input.Text(row, source));
            var previous = issues.GetValueOrDefault(key);
            issues[key] = (previous.Count == 0 ? row : previous.First, previous.Count + 1, area, string.Join(" ", reasons));
        }
        cancellation.ThrowIfCancellationRequested();
        if (issues.Count == 0) return areas;
        var details = new DataTable(["Primeira linha processada", "Talhão", "Parcela", "Lote", "Data de medição", "Fonte",
            "Coluna de comprimento", "Comprimento original", "Coluna de largura", "Largura original", "Coluna de declinação comprimento", "Declinação comprimento original",
            "Coluna de declinação largura", "Declinação largura original", "Área corrigida (m²)", "Ocorrências", "Motivo"]);
        foreach (var item in issues.OrderBy(i => i.Value.First))
        {
            int row = item.Value.First;
            details.Add((row + 1).ToString(CultureInfo.InvariantCulture), item.Key.Stand, item.Key.Plot, item.Key.Lot, input.Value(row, date), item.Key.Source,
                input.Text(0, length), input.Value(row, length), input.Text(0, width), input.Value(row, width),
                input.Text(0, slopeLength), input.Value(row, slopeLength), input.Text(0, slopeWidth), input.Value(row, slopeWidth),
                double.IsFinite(item.Value.Area) ? item.Value.Area : null, item.Value.Count.ToString(CultureInfo.InvariantCulture), item.Value.Reason);
        }
        var first = issues.First();
        string values = $"{input.Text(0, length)}='{first.Key.Length}', {input.Text(0, width)}='{first.Key.Width}', {input.Text(0, slopeLength)}='{first.Key.SlopeLength}', {input.Text(0, slopeWidth)}='{first.Key.SlopeWidth}'";
        var error = new InvalidDataException($"Área corrigida inválida: {issues.Count:N0} pendência(s), {issues.Values.Sum(i => i.Count):N0} linha(s). "
            + $"Primeira: linha {first.Value.First + 1:N0}, talhão '{first.Key.Stand}', parcela '{first.Key.Plot}'. {values}. {first.Value.Reason}");
        error.Data[DiagnosticsKey] = details;
        throw error;
    }
}
