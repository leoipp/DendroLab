using System.Globalization;
using System.Text.Json.Nodes;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

/// <summary>Relative DAP and volume distributions, normalized within parcels before rollup.</summary>
public sealed class DiameterClassDistribution
{
    private sealed class Parcel(string id, string stand, string plot, string lot, int year, string context)
    {
        public string Id = id, Stand = stand, Plot = plot, Lot = lot, Context = context;
        public int Year = year;
        public InventoryContext? Identity;
        public long DapCount;
        public long VolumeCount;
        public readonly Dictionary<int, long> Counts = [];
        public readonly Dictionary<int, long> Volumes = [];
    }

    private readonly Dictionary<string, Parcel> parcels = new(StringComparer.Ordinal);
    private readonly double classWidthMm, volumeInterval;

    public DiameterClassDistribution(double classWidthMm, double volumeInterval = 0.1)
    {
        if (!double.IsFinite(classWidthMm) || classWidthMm < 1 || classWidthMm > 1000)
            throw new InvalidDataException("O intervalo de classe de DAP deve estar entre 1 e 1000 mm.");
        if (!double.IsFinite(volumeInterval) || volumeInterval <= 0)
            throw new InvalidDataException("O intervalo de classe de volume deve ser maior que zero, em m³.");
        this.classWidthMm = classWidthMm;
        this.volumeInterval = volumeInterval;
    }

    public void Add(string stand, string plot, string lot, int year, string parcelId, string contextId,
        double dapMm, double volume, InventoryContext? identity = null)
    {
        string id = contextId + "\u001f" + parcelId;
        if (!parcels.TryGetValue(id, out var parcel))
            parcels.Add(id, parcel = new(parcelId, stand, plot, lot, year, contextId));
        parcel.Identity = identity;
        if (double.IsFinite(dapMm) && dapMm > 0)
        {
            int bin = Bin(dapMm, classWidthMm);
            parcel.DapCount++;
            parcel.Counts[bin] = parcel.Counts.GetValueOrDefault(bin) + 1;
        }
        if (double.IsFinite(volume) && volume >= 0)
        {
            int bin = Bin(volume, volumeInterval);
            parcel.VolumeCount++;
            parcel.Volumes[bin] = parcel.Volumes.GetValueOrDefault(bin) + 1;
        }
    }

    private static int Bin(double value, double interval)
    {
        double bin = Math.Floor(value / interval);
        if (!double.IsFinite(bin) || bin > int.MaxValue - 1) throw new InvalidDataException("Valor fora do intervalo permitido para as classes.");
        return (int)bin;
    }

    public IReadOnlyList<(string Name, DataTable Table)> CreateTables(JsonObject? levels = null)
    {
        if (parcels.Count == 0) return [];
        int[] Range(IEnumerable<int> keys)
        {
            var values = keys.ToArray();
            if (values.Length == 0) return [];
            int min = values.Min(), max = values.Max();
            if ((long)max - min > 10_000) throw new InvalidDataException("Mais de 10.001 classes. Aumente o intervalo de classe ou confira os dados.");
            return Enumerable.Range(min, max - min + 1).ToArray();
        }
        var bins = Range(parcels.Values.SelectMany(p => p.Counts.Keys));
        var volumeBins = Range(parcels.Values.SelectMany(p => p.Volumes.Keys));
        string Class(int bin, double interval) => string.Create(CultureInfo.InvariantCulture,
            $"{bin * interval:G}-{(bin + 1) * interval:G}");
        var keyColumns = parcels.Values.SelectMany(p => p.Identity?.KeyFields.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).Where(c => c is not ("Unidade" or "Talhão" or "Lote" or "Parcela" or "Parcela_ID" or "Ano_Medicao" or "Contexto_ID" or "Tipo_Inventario")).ToArray();
        string[] columns = ["Unidade", "Talhão", "Lote", "Parcela", "Parcela_ID", "Ano_Medicao", "Contexto_ID", "Tipo_Inventario", .. keyColumns, .. bins.Select(b => $"DAP_{Class(b, classWidthMm / 10)}cm_%"), .. volumeBins.Select(b => $"Volume_{Class(b, volumeInterval)}m3_%")];
        var result = new List<(string, DataTable)>();
        Add("parcela", "DISTRIBUICAO - PARCELAS", parcels.Values.Select(p => (p.Id, new[] { p })));
        Add("talhao", "DISTRIBUICAO - TALHOES", parcels.Values.GroupBy(p => (p.Context, p.Stand, p.Year)).Select(g => (g.Key.Stand, g.ToArray())));
        Add("lote", "DISTRIBUICAO - LOTES", parcels.Values.GroupBy(p => (p.Context, p.Lot, p.Year)).Select(g => (g.Key.Lot, g.ToArray())));
        Add("projeto", "DISTRIBUICAO - PROJETO", parcels.Values.GroupBy(p => (p.Context, p.Year)).Select(g => ("Projeto", g.ToArray())));
        return result;

        void Add(string key, string name, IEnumerable<(string Label, Parcel[] Parcels)> groups)
        {
            if (levels != null && !levels.B(key, true)) return;
            var table = new DataTable(columns);
            foreach (var (label, members) in groups)
            {
                var dapSums = new double[bins.Length];
                var volumeSums = new double[volumeBins.Length];
                long dapParcels = 0, volumeParcels = 0;
                foreach (var member in members)
                {
                    if (member.DapCount > 0)
                    {
                        dapParcels += member.DapCount;
                        for (int i = 0; i < bins.Length; i++) dapSums[i] += member.Counts.GetValueOrDefault(bins[i]);
                    }
                    if (member.VolumeCount > 0)
                    {
                        volumeParcels += member.VolumeCount;
                        for (int i = 0; i < volumeBins.Length; i++) volumeSums[i] += member.Volumes.GetValueOrDefault(volumeBins[i]);
                    }
                }
                var first = members[0];
                object?[] row = [label, key is "parcela" or "talhao" ? first.Stand : "", key is "parcela" or "talhao" or "lote" ? first.Lot : "",
                    key == "parcela" ? first.Plot : "", key == "parcela" ? first.Id : "", first.Year == 0 ? null : (double)first.Year, first.Context, first.Identity?.Type ?? "", .. keyColumns.Select(c => (object?)(first.Identity?.KeyFields.GetValueOrDefault(c) ?? "")),
                    .. dapSums.Select(v => dapParcels > 0 ? v / dapParcels * 100 : double.NaN),
                    .. volumeSums.Select(v => volumeParcels > 0 ? v / volumeParcels * 100 : double.NaN)];
                table.Add(row);
            }
            result.Add((name, table));
        }
    }
}
