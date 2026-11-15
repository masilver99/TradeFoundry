using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatGptPlanSpike;

public sealed class Connection
{
    public string ClientId { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>Mirrors the DevKit's <c>sharing</c> flag: plan usage is only usable when the direct scope was granted.</summary>
    public bool Sharing => !string.IsNullOrEmpty(AccessToken) && Scopes.Contains("chatgpt.tokens.use.direct");
}

/// <summary>
/// Spike credential store under %LOCALAPPDATA%\TradeFoundry\chatgpt-spike (outside the repo and app data directory).
/// Credentials are DPAPI-encrypted for the current Windows user. The host ID and issued client ID persist across sign-outs.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SpikeStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TradeFoundry.ChatGptPlanSpike.v1");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradeFoundry", "chatgpt-spike");

    private string HostFile => Path.Combine(Directory, "chatgpt-host.json");
    private string RegistrationFile => Path.Combine(Directory, "chatgpt-registration.json");
    private string ConnectionFile => Path.Combine(Directory, "connection.dpapi");

    public SpikeStore() => System.IO.Directory.CreateDirectory(Directory);

    public string GetHostId()
    {
        if (File.Exists(HostFile))
        {
            var id = JsonDocument.Parse(File.ReadAllText(HostFile)).RootElement.Str("hostId");
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        var hostId = $"urn:uuid:{Guid.NewGuid():D}";
        File.WriteAllText(HostFile, JsonSerializer.Serialize(new { hostId }, Json));
        return hostId;
    }

    public string? LoadClientId() =>
        File.Exists(RegistrationFile) ? JsonDocument.Parse(File.ReadAllText(RegistrationFile)).RootElement.Str("clientId") : null;

    /// <summary>Saved before the code exchange so a failed exchange doesn't register another app (DevKit behaviour).</summary>
    public void SaveClientId(string clientId) =>
        File.WriteAllText(RegistrationFile, JsonSerializer.Serialize(new { clientId }, Json));

    public Connection? LoadConnection()
    {
        if (!File.Exists(ConnectionFile)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(ConnectionFile), Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Connection>(plain);
    }

    public void SaveConnection(Connection connection)
    {
        connection.SavedAt = DateTimeOffset.UtcNow;
        var plain = JsonSerializer.SerializeToUtf8Bytes(connection);
        var temp = ConnectionFile + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, ConnectionFile, overwrite: true);
    }

    public void DeleteConnection()
    {
        if (File.Exists(ConnectionFile)) File.Delete(ConnectionFile);
    }
}
