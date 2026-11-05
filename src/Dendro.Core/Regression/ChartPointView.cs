using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Dendro.Core.Regression;

/// <summary>Read-only view over legacy point objects or a compact fixed-width chart payload.</summary>
public sealed class ChartPointView
{
    public const string PackedFormat = "packed-v1";
    public const int PackedRecordBytes = 24;
    private sealed record PackedPayload(byte[] Bytes, string[] Groups, int Count);
    private static readonly ConditionalWeakTable<JsonObject, PackedPayload> PackedCache = new();

    private readonly JsonArray? legacy;
    private readonly byte[]? packed;
    private readonly string[] groups;
    private readonly HashSet<int> excluded;
    public int Count { get; }
    public static bool IsPacked(JsonObject data) => data.S("point_format") == PackedFormat;

    private ChartPointView(JsonArray points, HashSet<int> excluded)
    {
        legacy = points;
        this.excluded = excluded;
        groups = [];
        Count = points.Count;
    }

    private ChartPointView(byte[] packed, string[] groups, int count, HashSet<int> excluded)
    {
        if (count < 0 || packed.Length != checked(count * PackedRecordBytes))
            throw new InvalidDataException("Dados compactos do gráfico inválidos.");
        this.packed = packed;
        this.groups = groups;
        this.excluded = excluded;
        Count = count;
    }

    public static ChartPointView Open(JsonObject data, JsonObject? config = null)
    {
        JsonObject? settings = config ?? data["chart"] as JsonObject;
        var excluded = (settings?["excluded"] as JsonArray ?? [])
            .Select(value => (int)JsonExtensions.Number(value)).ToHashSet();
        if (data.S("point_format") != PackedFormat)
            return new(data["points"] as JsonArray ?? [], excluded);

        var payload = PackedCache.GetValue(data, DecodePacked);
        return new(payload.Bytes, payload.Groups, payload.Count, excluded);
    }

    private static PackedPayload DecodePacked(JsonObject data)
    {
        byte[] packed;
        try { packed = Convert.FromBase64String(data.S("point_blob")); }
        catch (FormatException error) { throw new InvalidDataException("Dados compactos do gráfico inválidos.", error); }
        string[] groups = (data["point_groups"] as JsonArray ?? [])
            .Select(value => value?.GetValue<string>() ?? "").ToArray();
        int count = (int)data.N("point_count", -1);
        if (count < 0 || packed.Length != checked(count * PackedRecordBytes))
            throw new InvalidDataException("Dados compactos do gráfico inválidos.");
        return new(packed, groups, count);
    }

    public ChartPoint Get(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (legacy != null)
        {
            var point = legacy[index] ?? throw new InvalidDataException("Ponto do gráfico inválido.");
            int row = (int)point.N("row");
            return new(point.N("x"), point.N("y"), row, point.S("group"), point.B("excluded") || excluded.Contains(row));
        }

        int offset = index * PackedRecordBytes;
        int rowId = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(offset, 4));
        double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(packed.AsSpan(offset + 4, 8)));
        double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(packed.AsSpan(offset + 12, 8)));
        int groupId = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(offset + 20, 4));
        if ((uint)groupId >= (uint)groups.Length) throw new InvalidDataException("Grupo de ponto inválido no gráfico.");
        return new(x, y, rowId, groups[groupId], excluded.Contains(rowId));
    }

    public IEnumerable<ChartPoint> Points()
    {
        for (int index = 0; index < Count; index++) yield return Get(index);
    }
}
