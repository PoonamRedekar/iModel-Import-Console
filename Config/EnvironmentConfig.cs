namespace iModelImportConsole.Config;

public class EnvironmentConfig
{
    public string Authority { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
}

public class DatabaseConfig
{
    public string CoreDb { get; set; } = string.Empty;
    public string StagingDb { get; set; } = string.Empty;
}
