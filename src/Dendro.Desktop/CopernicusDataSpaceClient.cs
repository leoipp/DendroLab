using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dendro.Desktop;

internal sealed record Sentinel2Scene(string Id, DateTimeOffset Acquired, double CloudCover, string Platform)
{
    public string Label => $"{Acquired:dd/MM/yyyy HH:mm} UTC · nuvens {CloudCover:0.#}% · {Platform} · {Id}";
    public override string ToString() => Label;
}

internal enum Sentinel2ImageProduct { Rgb, FalseColorVegetation, Ndvi, Evi, Ndmi, Ndwi, Savi }

/// <summary>Reusable Copernicus Data Space STAC and Sentinel Hub Process API client.</summary>
internal sealed class CopernicusDataSpaceClient : IDisposable
{
    private const string TokenEndpoint = "https://identity.dataspace.copernicus.eu/auth/realms/CDSE/protocol/openid-connect/token";
    private const string StacSearchEndpoint = "https://stac.dataspace.copernicus.eu/v1/search";
    private const string ProcessEndpoint = "https://sh.dataspace.copernicus.eu/process/v1";
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private readonly CopernicusOAuthCredentials? credentials;
    private string? accessToken;
    private DateTimeOffset tokenExpiresAt;
    private bool disposed;

    internal CopernicusDataSpaceClient(CopernicusOAuthCredentials? credentials = null) => this.credentials = credentials;

    internal static async Task TestCredentialsAsync(string clientId, string clientSecret)
    {
        using var client = new CopernicusDataSpaceClient(new CopernicusOAuthCredentials(clientId.Trim(), clientSecret.Trim()));
        _ = await client.GetAccessTokenAsync(CancellationToken.None);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DendroLab/1.0 (Copernicus Data Space client)");
        return client;
    }

    internal async Task<Sentinel2Scene[]> SearchSentinel2Async(
        double xmin, double ymin, double xmax, double ymax,
        DateTime from, DateTime to, double maxCloudCoverage, CancellationToken cancellation)
    {
        EnsureActive();
        if (from.Date > to.Date) throw new InvalidOperationException("A data inicial precisa ser anterior à data final.");
        if (maxCloudCoverage is < 0 or > 100) throw new InvalidOperationException("A cobertura máxima de nuvens deve estar entre 0 e 100%.");
        var requestBody = new
        {
            collections = new[] { "sentinel-2-l2a" },
            bbox = new[] { xmin, ymin, xmax, ymax },
            datetime = $"{from:yyyy-MM-dd}T00:00:00Z/{to:yyyy-MM-dd}T23:59:59Z",
            limit = 100,
            query = new Dictionary<string, object> { ["eo:cloud_cover"] = new { lte = maxCloudCoverage } },
        };
        using var response = await Http.PostAsJsonAsync(StacSearchEndpoint, requestBody, cancellation);
        await EnsureSuccess(response, "buscar produtos Sentinel-2");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (!document.RootElement.TryGetProperty("features", out JsonElement features) || features.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("O catálogo Copernicus não retornou a lista de cenas.");
        var scenes = new List<Sentinel2Scene>();
        foreach (JsonElement feature in features.EnumerateArray())
        {
            JsonElement properties = feature.GetProperty("properties");
            if (!properties.TryGetProperty("datetime", out JsonElement dateElement)
                || !DateTimeOffset.TryParse(dateElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var acquired))
                continue;
            double cloud = properties.TryGetProperty("eo:cloud_cover", out JsonElement cloudElement) && cloudElement.TryGetDouble(out var value)
                ? value : double.NaN;
            string platform = properties.TryGetProperty("platform", out JsonElement platformElement) ? platformElement.GetString() ?? "Sentinel-2" : "Sentinel-2";
            scenes.Add(new Sentinel2Scene(feature.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "", acquired, cloud, platform));
        }
        return scenes.OrderByDescending(scene => scene.Acquired).ToArray();
    }

    internal async Task<byte[]> DownloadImagePngAsync(
        double xmin, double ymin, double xmax, double ymax,
        DateTime from, DateTime to, double maxCloudCoverage,
        int width, int height, Sentinel2ImageProduct product, CancellationToken cancellation)
    {
        EnsureActive();
        if (width is < 1 or > 2500 || height is < 1 or > 2500)
            throw new InvalidOperationException("O Process API aceita no máximo 2.500 pixels por dimensão. Reduza a área ou aumente o tamanho do pixel.");
        string token = await GetAccessTokenAsync(cancellation);
        string isoFrom = from.Date.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);
        string isoTo = to.Date.ToString("yyyy-MM-dd'T'23:59:59'Z'", CultureInfo.InvariantCulture);
        string evalscript = BuildEvalscript(product);
        var requestBody = new
        {
            input = new
            {
                bounds = new
                {
                    bbox = new[] { xmin, ymin, xmax, ymax },
                    properties = new { crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84" },
                },
                data = new[]
                {
                    new
                    {
                        type = "sentinel-2-l2a",
                        dataFilter = new
                        {
                            timeRange = new { from = isoFrom, to = isoTo },
                            maxCloudCoverage,
                            mosaickingOrder = "leastCC",
                        },
                    },
                },
            },
            output = new
            {
                width,
                height,
                responses = new[]
                {
                    new { identifier = "default", format = new { type = "image/png" } },
                },
            },
            evalscript,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ProcessEndpoint)
        {
            Content = JsonContent.Create(requestBody),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        await EnsureSuccess(response, $"gerar imagem {ProductName(product)} Sentinel-2");
        if (response.Content.Headers.ContentLength is long length && length > 100_000_000)
            throw new InvalidDataException("A imagem NDVI excede o limite de 100 MB.");
        byte[] png = await response.Content.ReadAsByteArrayAsync(cancellation);
        if (png.Length < 8 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
            throw new InvalidDataException("O Process API não retornou uma imagem PNG válida para conversão GeoTIFF.");
        return png;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellation)
    {
        if (credentials == null || string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
            throw new InvalidOperationException("Configure o Client ID e o Client Secret Copernicus em Configurações → Imagens de satélite.");
        if (!string.IsNullOrWhiteSpace(accessToken) && tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return accessToken;
        await TokenLock.WaitAsync(cancellation);
        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken) && tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return accessToken;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
            });
            using var response = await Http.PostAsync(TokenEndpoint, form, cancellation);
            await EnsureSuccess(response, "autenticar no Copernicus Data Space");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            accessToken = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidDataException("A autenticação Copernicus não retornou um access token.");
            int expiresIn = document.RootElement.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) ? seconds : 3600;
            tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            return accessToken;
        }
        finally { TokenLock.Release(); }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        string detail = await response.Content.ReadAsStringAsync();
        if (detail.Length > 1200) detail = detail[..1200];
        throw new HttpRequestException($"Não foi possível {operation} no Copernicus ({(int)response.StatusCode}): {detail}", null, response.StatusCode);
    }

    private void EnsureActive()
    {
        if (disposed) throw new ObjectDisposedException(nameof(CopernicusDataSpaceClient));
    }

    public void Dispose()
    {
        disposed = true;
        accessToken = null;
    }

    private static string ProductName(Sentinel2ImageProduct product) => product switch
    {
        Sentinel2ImageProduct.Rgb => "RGB natural",
        Sentinel2ImageProduct.FalseColorVegetation => "falso colorido de vegetação",
        Sentinel2ImageProduct.Ndvi => "NDVI",
        Sentinel2ImageProduct.Evi => "EVI",
        Sentinel2ImageProduct.Ndmi => "NDMI",
        Sentinel2ImageProduct.Ndwi => "NDWI",
        Sentinel2ImageProduct.Savi => "SAVI",
        _ => throw new ArgumentOutOfRangeException(nameof(product)),
    };

    private static string BuildEvalscript(Sentinel2ImageProduct product)
    {
        (string[] bands, string expression) = product switch
        {
            Sentinel2ImageProduct.Rgb => (new[] { "B04", "B03", "B02" }, "return [stretch(s.B04), stretch(s.B03), stretch(s.B02), 255];"),
            Sentinel2ImageProduct.FalseColorVegetation => (new[] { "B08", "B04", "B03" }, "return [stretch(s.B08), stretch(s.B04), stretch(s.B03), 255];"),
            Sentinel2ImageProduct.Ndvi => (new[] { "B08", "B04" }, "index = ratio(s.B08 - s.B04, s.B08 + s.B04);"),
            Sentinel2ImageProduct.Evi => (new[] { "B08", "B04", "B02" }, "index = 2.5 * ratio(s.B08 - s.B04, s.B08 + 6 * s.B04 - 7.5 * s.B02 + 1);"),
            Sentinel2ImageProduct.Ndmi => (new[] { "B08", "B11" }, "index = ratio(s.B08 - s.B11, s.B08 + s.B11);"),
            Sentinel2ImageProduct.Ndwi => (new[] { "B03", "B08" }, "index = ratio(s.B03 - s.B08, s.B03 + s.B08);"),
            Sentinel2ImageProduct.Savi => (new[] { "B08", "B04" }, "index = 1.5 * ratio(s.B08 - s.B04, s.B08 + s.B04 + 0.5);"),
            _ => throw new ArgumentOutOfRangeException(nameof(product)),
        };
        string allBands = string.Join(", ", bands.Select(band => $"\"{band}\"").Append("\"SCL\"").Append("\"dataMask\""));
        bool isIndex = product is not (Sentinel2ImageProduct.Rgb or Sentinel2ImageProduct.FalseColorVegetation);
        string palette = product == Sentinel2ImageProduct.Ndwi
            ? "if (index < -0.2) color = [95, 135, 75];\nelse if (index < 0) color = [180, 185, 120];\nelse if (index < 0.2) color = [220, 210, 160];\nelse if (index < 0.4) color = [125, 185, 215];\nelse if (index < 0.6) color = [65, 135, 200];\nelse color = [25, 75, 175];"
            : "if (index < -0.2) color = [35, 80, 150];\nelse if (index < 0) color = [100, 145, 170];\nelse if (index < 0.2) color = [196, 176, 112];\nelse if (index < 0.4) color = [225, 226, 145];\nelse if (index < 0.6) color = [126, 190, 91];\nelse color = [27, 115, 55];";
        string body = isIndex
            ? $$"""
                let index = 0;
                {{expression}}
                if (!Number.isFinite(index)) return [0, 0, 0, 0];
                let color;
                {{palette}}
                return [color[0], color[1], color[2], 255];
                """
            : $$"""
                {{expression}}
                """;
        return $$"""
            //VERSION=3
            function setup() {
              return { input: [{ bands: [{{allBands}}] }], output: { bands: 4, sampleType: "UINT8" } };
            }
            function stretch(value) { return Math.max(0, Math.min(255, Math.round(value * 637.5))); }
            function ratio(numerator, denominator) { return denominator === 0 ? NaN : numerator / denominator; }
            function evaluatePixel(s) {
              if (s.dataMask === 0 || [1, 3, 7, 8, 9, 10, 11].includes(s.SCL)) return [0, 0, 0, 0];
            {{body}}
            }
            """;
    }
}
