using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dendro.Core.Configuration;

/// <summary>Compact analysis results inside projects, with support for legacy plain snapshots.</summary>
public static class ProjectSnapshots
{
    private static readonly string[] Keys = ["plot_snapshot", "details_snapshot"];

    public static void Compact(JsonObject project)
    {
        foreach (var entry in (project["_dendro"]?["analyses"] as JsonArray ?? []).OfType<JsonObject>())
            CompactEntry(entry);
    }

    public static void CompactEntry(JsonObject entry)
    {
        if (!Keys.Any(key => entry[key] != null)) return;
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
        using (var writer = new Utf8JsonWriter(gzip))
        {
            writer.WriteStartObject();
            foreach (string key in Keys)
            {
                if (entry[key] is not { } value) continue;
                writer.WritePropertyName(key); value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        entry["result_snapshot"] = new JsonObject {
            ["format"] = "DENDRO_ANALYSIS_SNAPSHOT", ["version"] = 1,
            ["encoding"] = "gzip-base64", ["data"] = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length),
        };
        foreach (string key in Keys) entry.Remove(key);
    }

    public static bool HasPlot(JsonObject entry) => entry["plot_snapshot"] != null || entry["result_snapshot"] != null;

    public static JsonObject Read(JsonObject entry)
    {
        if (entry["result_snapshot"] is not JsonObject snapshot) return entry;
        if (snapshot.S("format") != "DENDRO_ANALYSIS_SNAPSHOT" || snapshot.N("version") != 1 || snapshot.S("encoding") != "gzip-base64")
            throw new InvalidDataException("Formato de resultado salvo incompatível.");
        try
        {
            using var buffer = new MemoryStream(Convert.FromBase64String(snapshot.S("data")), false);
            using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
            return JsonNode.Parse(gzip)?.AsObject() ?? throw new InvalidDataException("Resultado salvo vazio.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new InvalidDataException("Resultado salvo inválido no projeto.", ex);
        }
    }
}
