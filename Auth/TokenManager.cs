using System.Text;
using System.Text.Json;

namespace iModelImportConsole.Auth;

public class TokenManager
{
    private readonly Config.EnvironmentConfig? _envConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _accessToken;
    private DateTimeOffset _expiresAt;
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    public TokenManager(Config.EnvironmentConfig envConfig, string initialToken, int expiresInSeconds)
    {
        _envConfig = envConfig;
        _accessToken = initialToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
    }

    public TokenManager(string staticToken)
    {
        _envConfig = null;
        _accessToken = staticToken;
        _expiresAt = GetExpiryFromJwt(staticToken) ?? DateTimeOffset.UtcNow.AddHours(1);
    }

    public async Task<string> GetValidTokenAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow < _expiresAt - RefreshBuffer)
            return _accessToken;

        if (_envConfig == null)
        {
            var remaining = _expiresAt - DateTimeOffset.UtcNow;
            if (remaining.TotalSeconds <= 0)
                throw new InvalidOperationException("Pre-obtained token has expired. Please provide a fresh token via --token.");
            Console.WriteLine($"[WARN] Token expires in {remaining.TotalMinutes:F0} minutes");
            return _accessToken;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _expiresAt - RefreshBuffer)
                return _accessToken;

            Console.WriteLine("[INFO] Token expiring soon - refreshing...");
            var (token, expiresIn) = await AcquireClientCredentialsTokenAsync(_envConfig, ct);
            _accessToken = token;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return _accessToken;
        }
        finally { _lock.Release(); }
    }

    public static async Task<(string AccessToken, int ExpiresIn)> AcquireClientCredentialsTokenAsync(
        Config.EnvironmentConfig envConfig, CancellationToken ct = default)
    {
        using var httpClient = new HttpClient();
        var tokenEndpoint = $"{envConfig.Authority.TrimEnd('/')}/connect/token";
        var scopes = string.Join(" ", envConfig.Scopes);

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = envConfig.ClientId,
            ["client_secret"] = envConfig.ClientSecret,
            ["scope"] = scopes
        });

        var response = await httpClient.PostAsync(tokenEndpoint, requestBody, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Auth failed ({response.StatusCode}): {content}");

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        return (accessToken, expiresIn);
    }

    private static DateTimeOffset? GetExpiryFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1];
            var mod = payload.Length % 4;
            if (mod > 0) payload += new string('=', 4 - mod);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var expProp))
                return DateTimeOffset.FromUnixTimeSeconds(expProp.GetInt64());
        }
        catch { }
        return null;
    }
}
