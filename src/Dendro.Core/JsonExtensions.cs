using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dendro.Core;

public static class JsonExtensions
{
    public static string S(this JsonNode? j, string key, string fallback = "") =>
        j?[key]?.GetValueKind() == JsonValueKind.String ? j[key]!.GetValue<string>() : fallback;

    public static double N(this JsonNode? j, string key, double fallback = 0) =>
        Number(j?[key], fallback);

    public static double Number(JsonNode? node, double fallback = 0)
    {
        if (node is not JsonValue value)
            return fallback;
        if (value.TryGetValue<double>(out var number))
            return number;
        if (value.TryGetValue<int>(out var integer))
            return integer;
        if (value.TryGetValue<long>(out var large))
            return large;
        if (value.TryGetValue<decimal>(out var precise))
            return (double)precise;
        if (value.TryGetValue<float>(out var single))
            return single;
        if (value.TryGetValue<uint>(out var unsigned))
            return unsigned;
        if (value.TryGetValue<ulong>(out var huge))
            return huge;
        if (value.TryGetValue<short>(out var small))
            return small;
        if (value.TryGetValue<ushort>(out var usmall))
            return usmall;
        if (value.TryGetValue<byte>(out var octet))
            return octet;
        if (value.TryGetValue<sbyte>(out var signed))
            return signed;
        return fallback;
    }

    public static bool B(this JsonNode? j, string key, bool fallback = false) =>
        j?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public static JsonObject Obj(this JsonObject j, string key)
    {
        if (j[key] is null)
            j[key] = new JsonObject();
        return j[key] as JsonObject ?? throw new InvalidDataException($"Objeto inválido: {key}.");
    }

    public static JsonArray Arr(this JsonObject j, string key)
    {
        if (j[key] is null)
            j[key] = new JsonArray();
        return j[key] as JsonArray ?? throw new InvalidDataException($"Lista inválida: {key}.");
    }

    public static JsonNode? At(this JsonNode? j, string path)
    {
        foreach (var part in path.Split('.'))
            j = j?[part];
        return j;
    }

    public static void Set(this JsonObject j, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1])
            j = j.Obj(part);
        j[parts[^1]] = value;
    }

    public static string Display(this JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";

    public static JsonNode? Finite(double value) =>
        double.IsFinite(value) ? JsonValue.Create(value) : null;
}
