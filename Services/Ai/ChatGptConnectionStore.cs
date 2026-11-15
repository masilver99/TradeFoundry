using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Ai;

public sealed class ChatGptCredentials
{
    public string AccessToken { get; init; } = string.Empty;
    public string? RefreshToken { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>The saved ChatGPT connection with credentials unprotected. Never hand this to a page.</summary>
public sealed class ChatGptConnection
{
    public const string DirectScope = "chatgpt.tokens.use.direct";

    public string HostId { get; init; } = string.Empty;
    public string? ClientId { get; init; }
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public ChatGptCredentials? Credentials { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    /// <summary>Credentials existed but could not be decrypted (for example, Data Protection keys were lost).</summary>
    public bool CredentialsUnreadable { get; init; }

    public bool Connected => Credentials is not null && !string.IsNullOrEmpty(Subject);
    public bool Sharing => Connected && Scopes.Contains(DirectScope, StringComparer.Ordinal);
}

/// <summary>
/// Persists the ChatGPT connection in <c>ai_connections</c>. Tokens are encrypted with ASP.NET Data Protection
/// (DPAPI-backed keys on Windows; <c>DataProtection:KeysDirectory</c> in Docker). The host ID and issued client ID
/// survive disconnects so reconnecting does not register another app.
/// </summary>
public sealed class ChatGptConnectionStore(TradeFoundryDb database, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("TradeFoundry.ChatGptCredentials.v1");
    private readonly object _gate = new();

    public ChatGptConnection Load()
    {
        lock (_gate)
        {
            var record = EnsureRecord();
            ChatGptCredentials? credentials = null;
            var unreadable = false;
            if (!string.IsNullOrEmpty(record.ProtectedCredentials))
            {
                try { credentials = JsonSerializer.Deserialize<ChatGptCredentials>(_protector.Unprotect(record.ProtectedCredentials)); }
                catch (CryptographicException) { unreadable = true; }
                catch (JsonException) { unreadable = true; }
            }
            return new ChatGptConnection
            {
                HostId = record.HostId,
                ClientId = record.ClientId,
                Subject = record.Subject,
                Email = record.Email,
                DisplayName = record.DisplayName,
                Scopes = record.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Credentials = credentials,
                Model = record.Model,
                ReasoningEffort = record.ReasoningEffort,
                CredentialsUnreadable = unreadable,
            };
        }
    }

    /// <summary>Saved before the code exchange: a one-time code can fail after registration succeeds.</summary>
    public void SaveClientId(string clientId) => Update(record => Copy(record, clientId: clientId));

    public void SaveSignIn(string clientId, string subject, string? email, string? displayName, IReadOnlyList<string> scopes, ChatGptCredentials? credentials) =>
        Update(record => Copy(record, clientId: clientId, subject: subject, email: email, displayName: displayName,
            scopes: string.Join(' ', scopes), protectedCredentials: Protect(credentials), replaceIdentity: true));

    public void SaveCredentials(IReadOnlyList<string> scopes, ChatGptCredentials credentials) =>
        Update(record => Copy(record, scopes: string.Join(' ', scopes), protectedCredentials: Protect(credentials)));

    public void SavePreferences(string? model, string? reasoningEffort) =>
        Update(record => Copy(record, model: Normalize(model), reasoningEffort: Normalize(reasoningEffort), replacePreferences: true));

    /// <summary>Removes credentials and identity; keeps the host ID, client registration, and preferences.</summary>
    public void ClearConnection() =>
        Update(record => Copy(record, scopes: string.Empty, protectedCredentials: string.Empty, replaceIdentity: true));

    private void Update(Func<AiConnectionRecord, AiConnectionRecord> change)
    {
        lock (_gate) database.SaveAiConnection(change(EnsureRecord()));
    }

    private AiConnectionRecord EnsureRecord()
    {
        var record = database.GetAiConnection(AiOptions.ChatGptProvider);
        if (record is not null) return record;
        record = new AiConnectionRecord { Provider = AiOptions.ChatGptProvider, HostId = $"urn:uuid:{Guid.NewGuid():D}" };
        database.SaveAiConnection(record);
        return record;
    }

    private string Protect(ChatGptCredentials? credentials) =>
        credentials is null ? string.Empty : _protector.Protect(JsonSerializer.Serialize(credentials));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AiConnectionRecord Copy(
        AiConnectionRecord record,
        string? clientId = null,
        string? subject = null,
        string? email = null,
        string? displayName = null,
        string? scopes = null,
        string? protectedCredentials = null,
        string? model = null,
        string? reasoningEffort = null,
        bool replaceIdentity = false,
        bool replacePreferences = false) => new()
    {
        Provider = record.Provider,
        HostId = record.HostId,
        ClientId = clientId ?? record.ClientId,
        Subject = replaceIdentity ? subject : record.Subject,
        Email = replaceIdentity ? email : record.Email,
        DisplayName = replaceIdentity ? displayName : record.DisplayName,
        Scopes = scopes ?? record.Scopes,
        ProtectedCredentials = protectedCredentials ?? record.ProtectedCredentials,
        Model = replacePreferences ? model : record.Model,
        ReasoningEffort = replacePreferences ? reasoningEffort : record.ReasoningEffort,
    };
}
