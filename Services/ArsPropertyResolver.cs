using System.Net.Http.Headers;
using System.Text.Json;

namespace iModelImportConsole.Services;

/// <summary>
/// Resolves target property names from Asset Register Service.
/// </summary>
public class ArsPropertyResolver
{
    private readonly HttpClient _httpClient;
    private readonly string _arsBaseUrl;

    public ArsPropertyResolver(string arsBaseUrl, string? bearerToken = null)
    {
        _arsBaseUrl = arsBaseUrl.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrEmpty(bearerToken))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    /// <summary>
    /// Resolves property IDs to display names by calling ARS individual property endpoint.
    /// Calls ARS: GET /asset-register/v1/itwins/{iTwinId}/properties/{propertyId} for each unique ID.
    /// </summary>
    public async Task<Dictionary<Guid, string>> ResolvePropertyNamesAsync(Guid iTwinId, IEnumerable<Guid> propertyIds, CancellationToken ct = default)
    {
        var lookup = new Dictionary<Guid, string>();
        var uniqueIds = propertyIds.Distinct().ToList();

        Console.WriteLine($"[INFO] Resolving {uniqueIds.Count} unique target property names from ARS...");

        foreach (var propertyId in uniqueIds)
        {
            try
            {
                var url = $"{_arsBaseUrl}/api/v1/itwins/{iTwinId}/properties/{propertyId}";
                var response = await _httpClient.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[WARN] ARS property lookup failed for {propertyId} ({response.StatusCode})");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Try flat: { "displayLabel": "...", "name": "..." }
                // Try nested: { "property": { "displayLabel": "...", "name": "..." } }
                var target = root;
                if (root.TryGetProperty("property", out var propEl) && propEl.ValueKind == JsonValueKind.Object)
                    target = propEl;

                var name = target.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() :
                           target.TryGetProperty("Name", out var n2) && n2.ValueKind == JsonValueKind.String ? n2.GetString() :
                           target.TryGetProperty("displayLabel", out var dlEl) && dlEl.ValueKind == JsonValueKind.String ? dlEl.GetString() :
                           target.TryGetProperty("DisplayLabel", out var dl2) && dl2.ValueKind == JsonValueKind.String ? dl2.GetString() : null;

                if (!string.IsNullOrEmpty(name))
                    lookup[propertyId] = name;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not resolve property {propertyId}: {ex.Message}");
            }
        }

        Console.WriteLine($"[INFO] Resolved {lookup.Count}/{uniqueIds.Count} target property names from ARS");
        return lookup;
    }

    /// <summary>
    /// Resolves a feature type display name from ARS.
    /// Calls ARS: GET /api/v1/itwins/{iTwinId}/feature-types/{featureTypeId}
    /// </summary>
    public async Task<string?> ResolveFeatureTypeNameAsync(Guid iTwinId, Guid featureTypeId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_arsBaseUrl}/api/v1/itwins/{iTwinId}/feature-types/{featureTypeId}";
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    Console.WriteLine($"[WARN] ARS feature type lookup failed for {featureTypeId} ({response.StatusCode})");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try nested: { "featureType": { "displayLabel": "...", "name": "..." } } or flat
            var target = root;
            if (root.TryGetProperty("featureType", out var ftEl) && ftEl.ValueKind == JsonValueKind.Object)
                target = ftEl;

            if (target.TryGetProperty("displayLabel", out var dlEl) && dlEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(dlEl.GetString()))
                return dlEl.GetString();
            if (target.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                return nEl.GetString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not resolve feature type {featureTypeId}: {ex.Message}");
        }
        return null;
    }
}
