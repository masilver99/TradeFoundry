using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TradeFoundry.Services.Ai;

public sealed record ChatGptAuthorizationRequest(string HostId, string? SavedClientId, string? LoginHint, int RedirectPort, bool Reconsent);

public sealed record ChatGptSignInResult(string ClientId, string Subject, string? Email, string? DisplayName, IReadOnlyList<string> Scopes, ChatGptCredentials? Credentials);

public sealed record ChatGptTokenResult(IReadOnlyList<string> Scopes, ChatGptCredentials Credentials, string? IdToken);

/// <summary>
/// Sign in with ChatGPT OAuth: OIDC discovery, PKCE + loopback authorization with dynamic client registration,
/// ID-token verification, refresh, and revocation. Port of the DevKit's oauth.ts (pinned 0a36fefe).
/// </summary>
public sealed class ChatGptOAuth(Func<HttpClient> httpClients, TimeProvider? timeProvider = null)
{
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public const string AppName = "TradeFoundry";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private Discovery? _discovery;
    private (JsonWebKeySet Keys, DateTimeOffset FetchedAt)? _jwks;

    private sealed record Discovery(string AuthorizationEndpoint, string TokenEndpoint, string? RevocationEndpoint, string JwksUri);

    /// <summary>Starts the loopback listener and builds the authorization URL. Dispose the result to stop listening.</summary>
    public async Task<ChatGptPendingAuthorization> BeginAsync(ChatGptAuthorizationRequest request, CancellationToken cancellationToken)
    {
        var provider = await DiscoverAsync(cancellationToken);
        var state = RandomValue();
        var nonce = RandomValue();
        var verifier = RandomValue();
        var listener = LoopbackCallbackListener.Start(request.RedirectPort, state, request.SavedClientId);
        try
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("client_id", request.SavedClientId ?? "dynamic_agent_client"),
                new("response_type", "code"),
                new("redirect_uri", listener.RedirectUri),
                new("scope", Scopes),
                new("resource", Resource),
                new("state", state),
                new("nonce", nonce),
                new("code_challenge_method", "S256"),
                new("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))),
                new("ext_agent_host_id", request.HostId),
            };
            if (request.SavedClientId is null) query.Add(new("agent_name_hint", AppName));
            if (!string.IsNullOrWhiteSpace(request.LoginHint)) query.Add(new("login_hint", request.LoginHint));
            if (request.Reconsent) query.Add(new("prompt", "consent"));
            var separator = provider.AuthorizationEndpoint.Contains('?') ? '&' : '?';
            var url = provider.AuthorizationEndpoint + separator +
                string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            return new ChatGptPendingAuthorization(url, listener, verifier, nonce);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>Exchanges the callback code and verifies the returned identity.</summary>
    public async Task<ChatGptSignInResult> CompleteAsync(ChatGptPendingAuthorization pending, LoopbackCallback callback, CancellationToken cancellationToken)
    {
        var provider = await DiscoverAsync(cancellationToken);
        var data = await TokenRequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = callback.ClientId,
            ["code"] = callback.Code,
            ["code_verifier"] = pending.Verifier,
            ["redirect_uri"] = pending.RedirectUri,
            ["resource"] = Resource,
        }, cancellationToken);
        var idToken = data.String("id_token")
            ?? throw new AiProviderException("invalid_id_token", "ChatGPT did not return a verifiable identity. Try connecting again.");
        var identity = await VerifyIdentityAsync(provider, idToken, callback.ClientId, pending.Nonce, null, cancellationToken);
        var (scopes, credentials) = ParseTokenFields(data, requireRefresh: false, previousScopes: null, _time.GetUtcNow());
        return new ChatGptSignInResult(callback.ClientId, identity.Subject, identity.Email, identity.Name, scopes, credentials);
    }

    /// <summary>
    /// Refreshes with rotation. <paramref name="persistRotation"/> runs before identity verification because the old
    /// refresh token is spent as soon as the token endpoint answers.
    /// </summary>
    public async Task<ChatGptTokenResult> RefreshAsync(ChatGptConnection connection, Action<ChatGptTokenResult> persistRotation, CancellationToken cancellationToken)
    {
        if (connection.ClientId is null || connection.Credentials?.RefreshToken is not { Length: > 0 } refreshToken)
            throw new AiProviderException("invalid_grant", "Your ChatGPT connection has expired. Connect again.");
        var provider = await DiscoverAsync(cancellationToken);
        var data = await TokenRequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = connection.ClientId,
            ["refresh_token"] = refreshToken,
            ["resource"] = Resource,
        }, cancellationToken);
        var receivedAt = _time.GetUtcNow();
        var (scopes, credentials) = ParseTokenFields(data, requireRefresh: true, previousScopes: connection.Scopes, receivedAt);
        if (data.TryGetProperty("id_token", out var idElement) && (idElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(idElement.GetString())))
            throw new AiProviderException("invalid_id_token", "ChatGPT returned an invalid refreshed identity. Connect again.");
        var result = new ChatGptTokenResult(scopes, credentials!, data.String("id_token"));
        persistRotation(result);

        if (result.IdToken is { } idToken)
        {
            var identity = await VerifyIdentityAsync(provider, idToken, connection.ClientId, null, receivedAt, cancellationToken);
            if (identity.Subject != connection.Subject)
                throw new AiProviderException("account_mismatch", "The refreshed ChatGPT identity does not match this connection. Connect again.");
        }
        return result;
    }

    public async Task RevokeAsync(ChatGptConnection connection, CancellationToken cancellationToken)
    {
        if (connection.ClientId is null || connection.Credentials?.RefreshToken is not { Length: > 0 } token) return;
        var failed = new AiProviderException("revocation_failed",
            "TradeFoundry removed its saved credentials, but ChatGPT did not confirm the disconnect. Remove TradeFoundry in ChatGPT settings.", true);
        Discovery provider;
        try { provider = await DiscoverAsync(cancellationToken); }
        catch (AiProviderException) { throw failed; }
        if (provider.RevocationEndpoint is null) throw failed;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var retryable = false;
            try
            {
                using var response = await httpClients().PostAsync(provider.RevocationEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = token,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = connection.ClientId,
                }), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK) return;
                retryable = (int)response.StatusCode >= 500;
            }
            catch (HttpRequestException)
            {
                retryable = true;
            }
            if (!retryable || attempt == 1) break;
            await Task.Delay(300, cancellationToken);
        }
        throw failed;
    }

    internal static (IReadOnlyList<string> Scopes, ChatGptCredentials? Credentials) ParseTokenFields(
        JsonElement data, bool requireRefresh, IReadOnlyList<string>? previousScopes, DateTimeOffset now)
    {
        // OAuth may omit scope on refresh when the granted scopes are unchanged.
        var scopeText = data.String("scope") ?? (previousScopes is not null ? string.Join(' ', previousScopes) : null)
            ?? throw new AiProviderException("invalid_token_response", "ChatGPT did not confirm the granted permissions. Connect again.");
        var scopes = scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var accessToken = data.String("access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            if (requireRefresh || scopes.Contains("offline_access") || scopes.Contains(ChatGptConnection.DirectScope))
                throw new AiProviderException("invalid_token_response", "ChatGPT did not return the requested credentials. Connect again.");
            return (scopes, null);
        }

        var refreshToken = data.String("refresh_token");
        if (!string.Equals(data.String("token_type"), "bearer", StringComparison.OrdinalIgnoreCase) ||
            !data.TryGetProperty("expires_in", out var expiresIn) || expiresIn.ValueKind != JsonValueKind.Number ||
            !expiresIn.TryGetDouble(out var seconds) || seconds <= 0 || seconds > TimeSpan.FromDays(366).TotalSeconds ||
            ((requireRefresh || scopes.Contains("offline_access")) && string.IsNullOrEmpty(refreshToken)))
        {
            throw new AiProviderException("invalid_token_response", "ChatGPT returned incomplete credentials. Connect again.");
        }
        return (scopes, new ChatGptCredentials { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = now.AddSeconds(seconds) });
    }

    private async Task<Discovery> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_discovery is not null) return _discovery;
        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (_discovery is not null) return _discovery;
            var failed = new AiProviderException("discovery_failed", "ChatGPT sign-in configuration could not be verified. Try again shortly.", true);
            JsonElement root;
            try
            {
                using var response = await httpClients().GetAsync($"{Issuer}/.well-known/openid-configuration", cancellationToken);
                if (!response.IsSuccessStatusCode) throw failed;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                root = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw failed;
            }
            if (root.String("issuer") != Issuer) throw failed;

            string? Endpoint(string name, bool optional)
            {
                var value = root.String(name);
                if (value is null && optional) return null;
                if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != Issuer) throw failed;
                return value;
            }

            return _discovery = new Discovery(
                Endpoint("authorization_endpoint", false)!, Endpoint("token_endpoint", false)!,
                Endpoint("revocation_endpoint", true), Endpoint("jwks_uri", false)!);
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private async Task<JsonElement> TokenRequestAsync(Discovery provider, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await httpClients().SendAsync(request, cancellationToken); }
        catch (HttpRequestException) { throw new AiProviderException("network_error", "Could not reach ChatGPT. Check your connection and try again.", true); }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) throw ChatGptErrors.FromBody(body, (int)response.StatusCode, requestId);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new AiProviderException("invalid_token_response", "ChatGPT returned an invalid token response. Connect again.");
            }
        }
    }

    private sealed record Identity(string Subject, string? Email, string? Name);

    private async Task<Identity> VerifyIdentityAsync(Discovery provider, string idToken, string clientId, string? nonce, DateTimeOffset? receivedAt, CancellationToken cancellationToken)
    {
        var unavailable = new AiProviderException("identity_verification_unavailable",
            "ChatGPT identity verification is temporarily unavailable. Your connection has been kept; try again shortly.", true);
        var result = await ValidateAsync(await GetKeysAsync(provider, forceRefresh: false, unavailable, cancellationToken));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            result = await ValidateAsync(await GetKeysAsync(provider, forceRefresh: true, unavailable, cancellationToken));
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
            throw new AiProviderException("invalid_id_token", "The ChatGPT identity could not be verified. Connect again.");

        string? Claim(string type) => token.TryGetPayloadValue<string>(type, out var value) ? value : null;
        var subject = Claim("sub");
        var azp = Claim("azp");
        if (string.IsNullOrEmpty(subject) || (nonce is not null && Claim("nonce") != nonce) ||
            (azp is not null && azp != clientId) || (token.Audiences.Count() > 1 && azp != clientId))
        {
            throw new AiProviderException("invalid_id_token", "The ChatGPT identity could not be verified. Connect again.");
        }
        return new Identity(subject, Claim("email"), Claim("name"));

        Task<TokenValidationResult> ValidateAsync(JsonWebKeySet keys)
        {
            // A refresh checkpoint can outlive its ID token; validate lifetime as of when it was received.
            var now = receivedAt ?? _time.GetUtcNow();
            return new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = clientId,
                IssuerSigningKeys = keys.GetSigningKeys(),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(5),
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                LifetimeValidator = (notBefore, expires, _, _) =>
                    expires is { } exp && now.UtcDateTime <= exp.AddSeconds(5) && (notBefore is null || now.UtcDateTime >= notBefore.Value.AddSeconds(-5)),
            });
        }
    }

    private async Task<JsonWebKeySet> GetKeysAsync(Discovery provider, bool forceRefresh, AiProviderException unavailable, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _jwks is { } cached && _time.GetUtcNow() - cached.FetchedAt < TimeSpan.FromHours(1)) return cached.Keys;
        try
        {
            using var response = await httpClients().GetAsync(provider.JwksUri, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) throw unavailable;
            var keys = new JsonWebKeySet(await response.Content.ReadAsStringAsync(cancellationToken));
            if (keys.Keys.Count == 0) throw unavailable;
            _jwks = (keys, _time.GetUtcNow());
            return keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or JsonException)
        {
            throw unavailable;
        }
    }

    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ChatGptPendingAuthorization : IDisposable
{
    private readonly LoopbackCallbackListener _listener;

    internal ChatGptPendingAuthorization(string authorizationUrl, LoopbackCallbackListener listener, string verifier, string nonce)
    {
        AuthorizationUrl = authorizationUrl;
        _listener = listener;
        Verifier = verifier;
        Nonce = nonce;
    }

    public string AuthorizationUrl { get; }
    public string RedirectUri => _listener.RedirectUri;
    internal string Verifier { get; }
    internal string Nonce { get; }

    public Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken cancellationToken) => _listener.WaitAsync(cancellationToken);

    public void Dispose() => _listener.Dispose();
}
