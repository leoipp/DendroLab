using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Dendro.Desktop;

internal sealed record NoaaNdviImage(byte[] Png, double XMin, double YMin, double XMax, double YMax, int Width, int Height);

internal static class NoaaNdviClient
{
    private const string ServiceRoot = "https://gis.nnvl.noaa.gov/arcgis/rest/services";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DendroLab/1.0 (NOAA NDVI viewer)");
        return client;
    }

    internal static async Task<NoaaNdviImage> DownloadAsync(
        string product, string cadence, DateTime selectedDate, double xMin, double yMin, double xMax, double yMax,
        int requestedWidth, string token, CancellationToken cancellation)
    {
        string service = product switch
        {
            "RGB" => "TRUE/TRUE_daily",
            "NDVI" => cadence switch
            {
                "Semanal" => "NDVI/NDVI_weekly",
                "Mensal" => "NDVI/NDVI_monthly",
                "Anual" => "NDVI/NDVI_yearly",
                _ => throw new InvalidOperationException("Selecione uma periodicidade válida para o NDVI."),
            },
            _ => throw new InvalidOperationException("Selecione um produto NOAA válido."),
        };
        (DateTime start, DateTime end) = product == "RGB"
            ? (selectedDate.Date, selectedDate.Date.AddDays(1).AddMilliseconds(-1))
            : Period(cadence, selectedDate);
        double aspect = (yMax - yMin) / (xMax - xMin);
        int width = Math.Clamp(requestedWidth, 256, 4096);
        int height = Math.Clamp((int)Math.Round(width * aspect), 64, 4096);
        string endpoint = $"{ServiceRoot}/{service}/ImageServer/exportImage";
        var query = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["bbox"] = string.Join(",", new[] { xMin, yMin, xMax, yMax }.Select(Number)),
            ["bboxSR"] = "4326",
            ["imageSR"] = "4326",
            ["size"] = $"{width},{height}",
            ["adjustAspectRatio"] = "false",
            ["format"] = "png32",
            ["time"] = $"{Epoch(start)},{Epoch(end)}",
        };
        if (!string.IsNullOrWhiteSpace(token)) query["token"] = token.Trim();
        string requestUri = endpoint + "?" + Encode(query);
        using var response = await Http.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellation);
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error))
        {
            string message = error.TryGetProperty("message", out JsonElement detail) ? detail.GetString() ?? "A NOAA recusou a solicitação." : "A NOAA recusou a solicitação.";
            throw new InvalidOperationException("Falha ao consultar a imagem NOAA: " + message);
        }
        if (!root.TryGetProperty("href", out JsonElement hrefElement) || string.IsNullOrWhiteSpace(hrefElement.GetString()))
            throw new InvalidDataException("O serviço NOAA não retornou uma imagem para a área e o período selecionados.");
        if (!root.TryGetProperty("extent", out JsonElement extent))
            throw new InvalidDataException("O serviço NOAA não informou a extensão geográfica da imagem.");
        string href = hrefElement.GetString()!;
        Uri? imageUri = Uri.TryCreate(href, UriKind.Absolute, out var absoluteImageUri)
            ? absoluteImageUri
            : Uri.TryCreate(new Uri("https://gis.nnvl.noaa.gov/"), href, out var relativeImageUri) ? relativeImageUri : null;
        if (imageUri == null
            || imageUri.Scheme != Uri.UriSchemeHttps
            || !imageUri.Host.Equals("gis.nnvl.noaa.gov", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O serviço NOAA retornou um endereço de imagem inesperado.");
        if (!string.IsNullOrWhiteSpace(token) && !imageUri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(imageUri) { Query = string.IsNullOrEmpty(imageUri.Query)
                ? "token=" + Uri.EscapeDataString(token.Trim())
                : imageUri.Query.TrimStart('?') + "&token=" + Uri.EscapeDataString(token.Trim()) };
            imageUri = builder.Uri;
        }
        using var imageResponse = await Http.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, cancellation);
        imageResponse.EnsureSuccessStatusCode();
        if (imageResponse.Content.Headers.ContentLength is long length && length > 100_000_000)
            throw new InvalidDataException("A imagem NOAA excede o limite de 100 MB.");
        byte[] png = await imageResponse.Content.ReadAsByteArrayAsync(cancellation);
        if (png.Length < 8 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
            throw new InvalidDataException("A resposta do serviço NOAA não está em formato PNG válido para conversão GeoTIFF.");
        int resultWidth = root.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : width;
        int resultHeight = root.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : height;
        return new NoaaNdviImage(
            png,
            extent.GetProperty("xmin").GetDouble(),
            extent.GetProperty("ymin").GetDouble(),
            extent.GetProperty("xmax").GetDouble(),
            extent.GetProperty("ymax").GetDouble(),
            resultWidth,
            resultHeight);
    }

    private static (DateTime Start, DateTime End) Period(string cadence, DateTime date)
    {
        DateTime start = cadence switch
        {
            "Semanal" => date.Date,
            "Mensal" => new DateTime(date.Year, date.Month, 1),
            "Anual" => new DateTime(date.Year, 1, 1),
            _ => throw new InvalidOperationException("Periodicidade inválida."),
        };
        DateTime next = cadence switch
        {
            "Semanal" => start.AddDays(7),
            "Mensal" => start.AddMonths(1),
            "Anual" => start.AddYears(1),
            _ => throw new InvalidOperationException("Periodicidade inválida."),
        };
        return (start, next.AddMilliseconds(-1));
    }

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static long Epoch(DateTime date) => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static string Encode(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
}
