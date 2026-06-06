using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using iModelImportConsole.Auth;
using iModelImportConsole.Config;

namespace iModelImportConsole.Services;

public class IModelApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TokenManager? _tokenManager;
    private readonly string _apiBaseUrl;
    private readonly string _iModelId;
    private readonly string _iTwinId;
    private readonly string _changesetId;
    private const int MaxRetries = 3;
    private const string BentleyAcceptHeader = "application/vnd.bentley.itwin-platform.v1+json";
    private bool _gcsNotDefined = false; // cached flag — avoid repeated failed calls if iModel has no GCS

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public IModelApiClient(EnvironmentConfig envConfig, TokenManager tokenManager, string iModelId, string iTwinId, string changesetId)
    {
        _apiBaseUrl = envConfig.ApiBaseUrl;
        _iModelId = iModelId;
        _iTwinId = iTwinId;
        _changesetId = changesetId;
        _tokenManager = tokenManager;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(BentleyAcceptHeader));
    }

    public async Task<string?> GetLatestChangesetIdAsync(CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.bentley.itwin-platform.v2+json"));
        if (_tokenManager != null)
        {
            var token = await _tokenManager.GetValidTokenAsync(ct);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        var url = $"{_apiBaseUrl}/imodels/{_iModelId}/changesets?$orderBy=index+desc&$top=1";
        var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("changesets", out var arr))
            foreach (var cs in arr.EnumerateArray())
                if (cs.TryGetProperty("id", out var idEl))
                    return idEl.GetString();
        return null;
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteECSqlAsync(string query, CancellationToken ct = default, string[]? columnNames = null)
    {
        var baseQueryPath = $"{_apiBaseUrl}/imodel-query/itwins/{_iTwinId}/imodels/{_iModelId}/changesets/{_changesetId}/queries";
        var body = JsonSerializer.Serialize(new { query, limit = 10000 });
        var response = await SendWithRetryAsync(HttpMethod.Post, baseQueryPath, body, ct);

        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("state", out var stateElement))
        {
            var state = stateElement.GetString()?.ToLowerInvariant();
            if (state is "pending" or "running")
            {
                var queryId = doc.RootElement.GetProperty("id").GetString()!;
                var completedResponse = await PollQueryResultAsync(baseQueryPath, queryId, ct);
                return ParseRows(completedResponse, columnNames);
            }
        }
        return ParseRows(response, columnNames);
    }

    private async Task<string> PollQueryResultAsync(string baseQueryPath, string queryId, CancellationToken ct)
    {
        var pollUrl = $"{baseQueryPath}/{queryId}?limit=10000";
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var response = await SendWithRetryAsync(HttpMethod.Get, pollUrl, null, ct);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("state", out var stateEl))
            {
                var state = stateEl.GetString()?.ToLowerInvariant();
                if (state == "completed") return response;
                if (state == "failed") throw new InvalidOperationException("Query failed on server");
                continue;
            }
            return response;
        }
        throw new TimeoutException("Query did not complete in time");
    }

    private static List<Dictionary<string, object?>> ParseRows(string response, string[]? expectedColumns = null)
    {
        using var doc = JsonDocument.Parse(response);
        var results = new List<Dictionary<string, object?>>();
        if (doc.RootElement.TryGetProperty("rows", out var rows))
        {
            var columns = new List<string>();
            if (doc.RootElement.TryGetProperty("columns", out var cols))
                foreach (var col in cols.EnumerateArray())
                    columns.Add(col.GetString() ?? $"col{columns.Count}");
            if (columns.Count == 0 && expectedColumns != null)
                columns.AddRange(expectedColumns);

            foreach (var row in rows.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();
                if (row.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var val in row.EnumerateArray())
                    {
                        var key = i < columns.Count ? columns[i] : $"col{i}";
                        dict[key] = ExtractValue(val);
                        i++;
                    }
                }
                else if (row.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in row.EnumerateObject())
                        dict[prop.Name] = ExtractValue(prop.Value);
                }
                results.Add(dict);
            }
        }
        return results;
    }

    private static object? ExtractValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };

    private static readonly HttpStatusCode[] RetriableStatusCodes = new[]
    {
        HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout
    };

    private async Task<string> SendWithRetryAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body != null)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (_tokenManager != null)
                {
                    var token = await _tokenManager.GetValidTokenAsync(ct);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                var response = await _httpClient.SendAsync(request, ct);
                if (RetriableStatusCodes.Contains(response.StatusCode) && attempt < MaxRetries)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    Console.WriteLine($"[WARN] {(int)response.StatusCode} - retrying in {delay.TotalSeconds:F0}s...");
                    await Task.Delay(delay, ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"[ERROR] {response.StatusCode}: {errBody}");
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && ((int?)ex.StatusCode == null || (int)ex.StatusCode >= 500))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
                Console.WriteLine($"[WARN] Retrying: {ex.Message}");
            }
        }
        throw new InvalidOperationException($"Request to {url} failed after {MaxRetries} retries.");
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Converts iModel coordinates (X,Y,Z) to WGS84 (longitude, latitude, altitude)
    /// using: POST /imodel-query/itwins/{iTwin}/imodels/{imodel}/changesets/{changeset}/coordinates/geographic
    /// Request: { "coordinates": [{ "x": ..., "y": ..., "z": ... }] }
    /// Response: { "coordinates": [{ "longitude": ..., "latitude": ..., "altitude": ... }] }
    ///        or: { "rows": [{ "longitude": ..., "latitude": ..., "altitude": ... }] }
    /// </summary>
    public async Task<(double Longitude, double Latitude, double Elevation)?> ConvertToWgs84Async(
        double x, double y, double z, CancellationToken ct = default)
    {
        // Skip immediately if we already know this iModel has no GCS
        if (_gcsNotDefined) return null;

        var url = $"{_apiBaseUrl}/imodel-query/itwins/{_iTwinId}/imodels/{_iModelId}/changesets/{_changesetId}/coordinates/geographic";
        var body = JsonSerializer.Serialize(new
        {
            target = "WGS84",
            iModelCoordinates = new[] { new { x, y, z } }
        });

        try
        {
            // Use direct HTTP call (no retry) — coordinate conversion errors are not transient
            if (_tokenManager != null)
            {
                var token = await _tokenManager.GetValidTokenAsync(ct);
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var httpResponse = await _httpClient.PostAsync(url,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errBody = await httpResponse.Content.ReadAsStringAsync(ct);
                // NoGCSDefined is expected for iModels without georeferencing — log once and return null
                if (errBody.Contains("NoGCSDefined"))
                {
                    _gcsNotDefined = true;
                    Console.WriteLine("[WARN] iModel has no GCS defined — geometry will not be inserted for any element.");
                }
                else
                    Console.WriteLine($"[WARN] Coordinate conversion failed ({httpResponse.StatusCode}): {errBody}");
                return null;
            }

            var response = await httpResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(response);

            // Response: { "geoCoordinates": [{ "point": { "longitude": ..., "latitude": ..., "elevation": ... } }] }
            if (doc.RootElement.TryGetProperty("geoCoordinates", out var geoCoords) &&
                geoCoords.GetArrayLength() > 0)
            {
                var first = geoCoords[0];
                // Skip if error present
                if (first.TryGetProperty("error", out _))
                    return null;

                if (first.TryGetProperty("point", out var point) &&
                    point.TryGetProperty("longitude", out var lon) &&
                    point.TryGetProperty("latitude", out var lat))
                {
                    var elevation = point.TryGetProperty("elevation", out var elev) ? elev.GetDouble() : z;
                    return (lon.GetDouble(), lat.GetDouble(), elevation);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Coordinate conversion failed: {ex.Message}");
        }
        return null;
    }
}
