using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

public sealed record InventoryContext(string Id, string Origin, string Type);

internal readonly record struct InventoryKeyFingerprint(ulong A, ulong B, ulong C, ulong D)
{
    public static InventoryKeyFingerprint FromHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 32) throw new ArgumentException("Fingerprint SHA-256 inválido.", nameof(hash));
        return new(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
    }
    public override int GetHashCode() => HashCode.Combine(A, B, C, D);
    public string ToHexString()
    {
        Span<byte> bytes = stackalloc byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, A);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D);
        return Convert.ToHexString(bytes);
    }
}

public sealed class InventoryIdentity
{
    public static JsonObject Defaults() => new() { ["arvore"] = new JsonArray("talhao", "parcela", "cova", "fuste", "fila", "data_medicao"),
        ["parcela"] = new JsonArray("talhao", "parcela", "data_medicao"), ["colunas_origem"] = new JsonArray("Fonte"), ["coluna_tipo_inventario"] = "" };
    public static void Ensure(JsonObject cfg) => Configuration.ProjectStore.MergeDefaults(cfg.Obj("identificacao"), Defaults());
    public static void Validate(JsonObject cfg)
    {
        Ensure(cfg);
        foreach (string key in new[] { "arvore", "parcela", "colunas_origem" })
        {
            if (cfg.Obj("identificacao")[key] is not JsonArray fields || (key != "colunas_origem" && fields.Count == 0)
                || fields.Any(f => f is not JsonValue v || !v.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text))
                || fields.Select(f => f.Display()).Distinct(StringComparer.Ordinal).Count() != fields.Count)
                throw new InvalidDataException($"Configure uma lista de colunas únicas para a identificação de {key}.");
        }
        if (cfg.At("identificacao.coluna_tipo_inventario") is not JsonValue type || !type.TryGetValue<string>(out _))
            throw new InvalidDataException("Configure o nome da coluna de tipo de inventário como texto.");
        if (cfg.At("processador").S("inventory_type") is not ("" or "IFC" or "IFPC" or "MISTO"))
            throw new InvalidDataException("Tipo de inventário inválido: escolha IFC, IFPC ou MISTO.");
    }

    private readonly DataTable input;
    private readonly int[] treeColumns, plotColumns, originColumns;
    private readonly int dateColumn, typeColumn;
    private readonly string defaultType;
    private readonly Dictionary<(string Type, string Origin), InventoryContext> contexts = new();
    private readonly Dictionary<InventoryKeyFingerprint, string> plots = new();
    private readonly Dictionary<InventoryKeyFingerprint, (string Stand, string Lot, string Plot)> plotDetails = new();
    private readonly int standColumn, lotColumn, plotColumn;
    private readonly Dictionary<double, string> dates = new();
    private readonly Dictionary<DataTable, (int Source, int Target)[]> copiedColumns = new();
    private static readonly InventoryContext EmptyContext = new("", "", "");
    public bool HasContext => originColumns.Length > 0 || typeColumn >= 0 || defaultType.Length > 0;

    public InventoryIdentity(DataTable input, JsonObject cfg, bool requireTree = false)
    {
        this.input = input; Ensure(cfg);
        var map = cfg.At("arquivo_entrada.colunas");
        standColumn = input.Column(map.S("talhao", "Talhão"));
        lotColumn = input.Column(map.S("lote", "Lote"));
        plotColumn = input.Column(map.S("parcela", "Parcela"));
        string Resolve(string field) => map?[field] is JsonValue ? map.S(field) : field;
        int[] Columns(string key) => cfg.Obj("identificacao").Arr(key).Select(f => input.Require(Resolve(f.Display()))).ToArray();
        treeColumns = requireTree ? Columns("arvore") : [];
        plotColumns = Columns("parcela");
        originColumns = cfg.Obj("identificacao").Arr("colunas_origem").Select(f => Resolve(f.Display())).Select(name =>
        {
            int index = input.Column(name);
            if (index < 0 && name != "Fonte") throw new InvalidDataException($"Coluna de origem ausente: {name}. Revise Configurações > Identificação.");
            return index;
        }).Where(i => i >= 0).ToArray();
        dateColumn = input.Column(map.S("data_medicao", "Data"));
        string typeName = cfg.At("identificacao").S("coluna_tipo_inventario");
        typeColumn = typeName.Length > 0 ? input.Require(typeName) : new[] { "Tipo_Inventario", "Tipo de inventário", "Tipo_Inventario_Origem" }.Select(input.Column).FirstOrDefault(i => i >= 0, -1);
        defaultType = cfg.At("processador").S("inventory_type");
    }

    public InventoryContext Context(int row)
    {
        if (!HasContext) return EmptyContext;
        string[] origins = originColumns.Length > 1 ? originColumns.Select(c => input.Text(row, c)).ToArray() : [];
        string origin = originColumns.Length == 1 ? input.Text(row, originColumns[0]) : string.Join(" · ", origins);
        string type = typeColumn >= 0 ? input.Text(row, typeColumn).Trim().ToUpperInvariant() : "";
        if (typeColumn >= 0 && type is not ("IFC" or "IFPC"))
            throw new InvalidDataException($"Linha {row + 1}: tipo de inventário '{type}' inválido. A coluna configurada deve conter IFC ou IFPC.");
        if (typeColumn < 0)
        {
            bool ifpc = origin.Contains("IFPC", StringComparison.OrdinalIgnoreCase), ifc = origin.Contains("IFC", StringComparison.OrdinalIgnoreCase);
            if (ifpc && ifc) throw new InvalidDataException($"Linha {row + 1}: a origem contém IFC e IFPC. Configure uma coluna de tipo de inventário por linha.");
            type = ifpc ? "IFPC" : ifc ? "IFC" : defaultType;
            if (type == "MISTO") throw new InvalidDataException($"Linha {row + 1}: não foi possível identificar IFC/IFPC pela origem. Configure a coluna de tipo em Identificação.");
        }
        var key = (type, origins.Length > 1 ? JsonSerializer.Serialize(origins) : origin);
        if (!contexts.TryGetValue(key, out var context)) contexts[key] = context = new(Hash(JsonSerializer.Serialize(new[] { key.Item1, key.Item2 })), origin, type);
        return context;
    }
    private InventoryKeyFingerprint RawKey(int row, int[] columns)
    {
        IEnumerable<string> Values()
        {
            foreach (int c in columns)
            {
                if (c != dateColumn) { yield return input.Text(row, c); continue; }
                object? cell = input.Value(row, c);
                double date = cell is double number ? number : InventoryMath.Date(input.Text(row, c));
                if (!double.IsFinite(date))
                {
                    string rawDate = input.Text(row, c);
                    string displayed = rawDate.Length <= 100 ? rawDate : rawDate[..100] + "…";
                    throw new InvalidDataException($"Linha {row + 1}: data de medição inválida na coluna '{input.Text(0, c)}' (valor: '{displayed}').");
                }
                if (!dates.TryGetValue(date, out string? value)) dates[date] = value = DateTime.FromOADate(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                yield return value;
            }
        }
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(new[] { Context(row).Id }.Concat(Values()));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(serialized, hash);
        return InventoryKeyFingerprint.FromHash(hash);
    }
    internal InventoryKeyFingerprint TreeFingerprint(int row) => RawKey(row, treeColumns);
    public string TreeKey(int row) => TreeFingerprint(row).ToHexString();
    public string PlotKey(int row)
    {
        var key = RawKey(row, plotColumns);
        var details = (Stand: input.Text(row, standColumn), Lot: input.Text(row, lotColumn), Plot: input.Text(row, plotColumn));
        if (plotDetails.TryGetValue(key, out var first) && first != details)
            throw new InvalidDataException("A chave de parcela está agrupando talhões, lotes ou números de parcela diferentes. Revise Configurações > Identificação.");
        plotDetails.TryAdd(key, details);
        if (!plots.TryGetValue(key, out string? stored)) plots[key] = stored = key.ToHexString();
        return stored;
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void CopyKeyFields(DataTable destination, int targetRow, int sourceRow)
    {
        if (!copiedColumns.TryGetValue(destination, out var mapping))
        {
            mapping = treeColumns.Concat(plotColumns).Distinct().Select(c => (Source: c, Name: input.Text(0, c)))
                .Where(c => destination.Column(c.Name) < 0).Select(c => (c.Source, destination.Ensure(c.Name))).ToArray();
            copiedColumns[destination] = mapping;
        }
        foreach (var item in mapping) destination.Set(targetRow, item.Target, input.Value(sourceRow, item.Source));
    }
    public static void SetContext(DataTable table, int row, InventoryContext context)
    {
        if (context.Id.Length == 0) return;
        table.Set(row, table.Ensure("Contexto_ID"), context.Id); table.Set(row, table.Ensure("Base_Origem"), context.Origin);
        table.Set(row, table.Ensure("Tipo_Inventario"), context.Type);
    }
}
