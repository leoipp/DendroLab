using System.Globalization;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

public sealed record HeightObservation(double Observed, double Estimated, double Residual, string Lot);
public sealed record HeightPresentation(DataTable Metrics, DataTable Lots, HeightObservation[] Sample, long Observations);
public sealed record InventoryPresentation(DataTable Lots, DataTable Stands, DataTable Totals);

/// <summary>Reads existing report sections without changing their calculations or materializing all height pairs.</summary>
public static class ProcessingPresentation
{
    public static HeightPresentation Height(TableSource source, CancellationToken cancellation = default, int sampleLimit = 20000)
    {
        if (sampleLimit < 1) throw new ArgumentOutOfRangeException(nameof(sampleLimit));
        var metrics = new DataTable(["Indicador", "Valor", "Unidade"]);
        var lots = new DataTable();
        var sample = new List<HeightObservation>();
        var random = new Random(173);
        long observations = 0;
        string section = "";
        using var cursor = new TableCursor(source);
        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            string first = Text(row, 0);
            if (first == "Métrica") { section = "metrics"; continue; }
            if (first == "Lote" && Text(row, 1) == "Método") { lots.Add(row.Select(v => (object?)TableCursor.Text(v)).ToArray()); section = "lots"; continue; }
            if (first == "H_obs" && Text(row, 1) == "H_est") { section = "pairs"; continue; }
            if (first.Length == 0 || first.StartsWith("ESTATÍSTICAS", StringComparison.Ordinal)) { section = ""; continue; }
            if (section == "metrics") metrics.Add(first, Value(row, 1), Text(row, 2));
            else if (section == "lots") lots.Add(row);
            else if (section == "pairs")
            {
                double observed = Number(row, 0), estimated = Number(row, 1), residual = Number(row, 2);
                if (!(observed > 0) || !double.IsFinite(estimated) || !double.IsFinite(residual)) continue;
                // Processing stores height in dm; report error metrics are already in metres.
                var point = new HeightObservation(observed / 10, estimated / 10, residual, Text(row, 3));
                observations++;
                if (sample.Count < sampleLimit) sample.Add(point);
                else { long slot = random.NextInt64(observations); if (slot < sampleLimit) sample[(int)slot] = point; }
            }
        }
        return new(metrics, lots, sample.ToArray(), observations);
    }

    public static InventoryPresentation Inventory(TableSource source, CancellationToken cancellation = default)
    {
        var lots = new DataTable(); var stands = new DataTable(); var totals = new DataTable();
        DataTable? current = null;
        using var cursor = new TableCursor(source);
        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            string first = Text(row, 0);
            if (first is "Lot" or "Stand")
            {
                current = first == "Lot" ? lots : stands;
                if (current.Count == 0) current.Add(row.Select(v => (object?)TableCursor.Text(v)).ToArray());
                continue;
            }
            if (first.StartsWith("INVENTORY STATISTICS", StringComparison.Ordinal)) { current = null; continue; }
            if (current == null || first.Length == 0) continue;
            if (first == "TOTAL")
            {
                if (totals.Count == 0) totals.Add(current.Rows[0].ToArray());
                var total = row.ToArray(); total[0] = ReferenceEquals(current, lots) ? "Por lote" : "Por talhão";
                totals.Add(total); current = null;
            }
            else current.Add(row);
        }
        return new(lots, stands, totals);
    }

    public static object? Value(object?[] row, int column) => column < row.Length ? row[column] : null;
    public static string Text(object?[] row, int column) => TableCursor.Text(Value(row, column));
    public static double Number(object?[] row, int column) => double.TryParse(Text(row, column).Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
}
