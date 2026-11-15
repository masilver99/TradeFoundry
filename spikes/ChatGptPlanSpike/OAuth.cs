using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatGptPlanSpike;

/// <summary>
/// C# port of the DevKit's oauth.ts (pinned 0a36fefe): OIDC discovery, PKCE + loopback authorization with
/// dynamic client registration, ID-token verification, refresh with rotation, and revocation.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class OAuth
{
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private const string CallbackPath = "/auth/callback";
    private const string AppName = "TradeFoundry";

    public static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(4),
    };

    private sealed record Discovery(string AuthorizationEndpoint, string TokenEndpoint, string? RevocationEndpoint, string JwksUri);

    private static Discovery? _discovery;

    private static async Task<Discovery> DiscoverAsync(CancellationToken ct)
    {
        if (_discovery is not null) return _discovery;
        using var response = await Http.GetAsync($"{Issuer}/.well-known/openid-configuration", ct);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        if (!response.IsSuccessStatusCode || root.Str("issuer") != Issuer) throw DiscoveryFailed();

        string Endpoint(string name, bool optional = false)
        {
            var value = root.Str(name);
            if (value is null && optional) return null!;
            if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != Issuer)
                throw DiscoveryFailed();
            return value;
        }

        return _discovery = new Discovery(
            Endpoint("authorization_endpoint"), Endpoint("token_endpoint"), Endpoint("revocation_endpoint", optional: true), Endpoint("jwks_uri"));

        static ChatGptException DiscoveryFailed() => new("discovery_failed", "ChatGPT sign-in configuration could not be verified.", true);
    }

    public static async Task<Connection> SignInAsync(SpikeStore store, int port, bool reconsent, CancellationToken ct)
    {
        var previous = store.LoadConnection();
        var savedClientId = store.LoadClientId();
        var hostId = store.GetHostId();
        var state = RandomValue();
        var nonce = RandomValue();
        var verifier = RandomValue();
        var provider = await DiscoverAsync(ct);

        using var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (SocketException) { throw new ChatGptException("callback_port_unavailable", $"Loopback port {port} is unavailable. Pass --port 0 or another port."); }
        var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{actualPort}{CallbackPath}";

        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", savedClientId ?? "dynamic_agent_client"),
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", Scopes),
            new("resource", Resource),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge_method", "S256"),
            new("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))),
            new("ext_agent_host_id", hostId),
        };
        if (savedClientId is null) query.Add(new("agent_name_hint", AppName));
        if (previous?.Email is { } email) query.Add(new("login_hint", email));
        if (reconsent) query.Add(new("prompt", "consent"));

        var separator = provider.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        var url = provider.AuthorizationEndpoint + separator + string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        Console.WriteLine($"Listening on {redirectUri}");
        Console.WriteLine("Opening the system browser for ChatGPT sign-in. If it doesn't open, paste this URL:");
        Console.WriteLine(url);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { Console.WriteLine("(Could not launch a browser automatically.)"); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var (code, clientId) = await WaitForCallbackAsync(listener, actualPort, state, savedClientId, timeout.Token);
        listener.Stop();

        store.SaveClientId(clientId);
        var data = await TokenRequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = Resource,
        }, ct);

        var idToken = data.Str("id_token") ?? throw new ChatGptException("invalid_id_token", "ChatGPT did not return a verifiable identity.");
        var identity = await VerifyIdentityAsync(provider, idToken, clientId, nonce, ct);
        if (previous?.Subject is { } subject && subject != identity.Subject && previous.ClientId == clientId)
            throw new ChatGptException("account_mismatch", "This sign-in returned a different ChatGPT account.");

        var connection = new Connection
        {
            ClientId = clientId,
            Subject = identity.Subject,
            Email = identity.Email,
            Name = identity.Name,
        };
        ApplyTokenFields(connection, data, requireRefresh: false, previousScopes: null);
        store.SaveConnection(connection);
        return connection;
    }

    /// <summary>Returns a valid access token, refreshing (with rotation persisted immediately) when near expiry.</summary>
    public static async Task<string> GetAccessTokenAsync(SpikeStore store, CancellationToken ct, bool forceRefresh = false)
    {
        var connection = store.LoadConnection() ?? throw new ChatGptException("not_connected", "Not signed in. Run: signin");
        if (!connection.Sharing) throw new ChatGptException("sharing_not_granted", "ChatGPT plan usage was not granted. Run: signin --reconsent");
        if (!forceRefresh && connection.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return connection.AccessToken!;
        await RefreshAsync(store, connection, ct);
        return connection.AccessToken!;
    }

    public static async Task RefreshAsync(SpikeStore store, Connection connection, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connection.RefreshToken))
            throw new ChatGptException("invalid_grant", "Your ChatGPT connection has expired. Sign in again.");
        var provider = await DiscoverAsync(ct);
        var data = await TokenRequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = connection.ClientId,
            ["refresh_token"] = connection.RefreshToken,
            ["resource"] = Resource,
        }, ct);
        ApplyTokenFields(connection, data, requireRefresh: true, previousScopes: connection.Scopes);
        // Persist the rotated refresh token before anything else can fail: the old one is now spent.
        store.SaveConnection(connection);

        if (data.Str("id_token") is { } idToken)
        {
            var identity = await VerifyIdentityAsync(provider, idToken, connection.ClientId, nonce: null, ct);
            if (identity.Subject != connection.Subject)
                throw new ChatGptException("account_mismatch", "The refreshed ChatGPT identity does not match this profile.");
        }
    }

    public static async Task RevokeAsync(SpikeStore store, CancellationToken ct)
    {
        var connection = store.LoadConnection();
        store.DeleteConnection();
        if (connection?.RefreshToken is not { } token) return;
        var provider = await DiscoverAsync(ct);
        if (provider.RevocationEndpoint is null)
            throw new ChatGptException("revocation_failed", "Local credentials were removed, but remote disconnection could not be confirmed. Disconnect the app in ChatGPT Settings.");
        using var response = await Http.PostAsync(provider.RevocationEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = connection.ClientId,
        }), ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ChatGptException("revocation_failed", "Local credentials were removed, but remote disconnection could not be confirmed. Disconnect the app in ChatGPT Settings.", status: (int)response.StatusCode);
    }

    private static async Task<(string Code, string ClientId)> WaitForCallbackAsync(TcpListener listener, int port, string state, string? savedClientId, CancellationToken ct)
    {
        const string script = "history.replaceState(null, \"\", \"/auth/complete\");";
        var scriptHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        var expectedHost = $"127.0.0.1:{port}";

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            stream.ReadTimeout = 15_000;
            var head = await ReadRequestHeadAsync(stream, ct);
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var host = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1].Trim()).FirstOrDefault();

            if (requestLine.Length < 2 || requestLine[0] != "GET" || host != expectedHost || !requestLine[1].StartsWith(CallbackPath, StringComparison.Ordinal) ||
                (requestLine[1].Length > CallbackPath.Length && requestLine[1][CallbackPath.Length] != '?'))
            {
                await RespondAsync(stream, "404 Not Found", "text/plain", "Not found", null, ct);
                continue;
            }

            var queryText = requestLine[1].Length > CallbackPath.Length ? requestLine[1][(CallbackPath.Length + 1)..] : string.Empty;
            var query = System.Web.HttpUtility.ParseQueryString(queryText);
            var returnedState = query.GetValues("state");
            if (returnedState is not { Length: 1 } ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState[0]), Encoding.UTF8.GetBytes(state)))
            {
                // Unrelated loopback requests must not consume the pending sign-in.
                await RespondAsync(stream, "400 Bad Request", "text/plain", "Invalid sign-in state. Return to the browser tab that started sign-in.", null, ct);
                continue;
            }

            var csp = $"default-src 'none'; script-src 'sha256-{scriptHash}'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
            await RespondAsync(stream, "200 OK", "text/html; charset=utf-8",
                $"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Return to TradeFoundry</title><style>body{{font:17px system-ui;max-width:32rem;margin:18vh auto;padding:24px}}h1{{font-size:26px}}</style><h1>Return to TradeFoundry</h1><p>TradeFoundry will finish checking your ChatGPT connection. You can close this tab.</p><script>{script}</script></html>",
                csp, ct);

            if (query["error"] is { } error)
                throw ApiErrors.From(JsonSerializer.Serialize(new { error, error_description = query["error_description"] }), null, null);

            var codes = query.GetValues("code");
            var returnedClientIds = query.GetValues("client_id");
            var clientId = returnedClientIds?.FirstOrDefault() ?? savedClientId;
            if (codes is not { Length: 1 } || returnedClientIds is { Length: > 1 } || clientId is null ||
                clientId == "dynamic_agent_client" || clientId.Length > 200 || !clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
                (savedClientId is not null && returnedClientIds is { Length: 1 } && returnedClientIds[0] != savedClientId))
            {
                throw new ChatGptException("registration_incomplete", "ChatGPT did not complete app registration. Please try signing in again.");
            }
            return (codes[0], clientId);
        }
    }

    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string contentType, string body, string? csp, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {status}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {bytes.Length}\r\n")
            .Append("Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
        if (csp is not null) headers.Append($"Content-Security-Policy: {csp}\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), ct);
        await stream.WriteAsync(bytes, ct);
    }

    private static async Task<JsonElement> TokenRequestAsync(Discovery provider, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode) throw ApiErrors.From(body, (int)response.StatusCode, requestId);
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new ChatGptException("invalid_token_response", "ChatGPT returned an invalid token response."); }
    }

    private static void ApplyTokenFields(Connection connection, JsonElement data, bool requireRefresh, string[]? previousScopes)
    {
        // OAuth may omit scope on refresh when granted scopes are unchanged.
        var scopeText = data.Str("scope") ?? (previousScopes is not null ? string.Join(' ', previousScopes) : null)
            ?? throw new ChatGptException("invalid_token_response", "ChatGPT did not confirm the granted permissions.");
        var scopes = scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var accessToken = data.Str("access_token");
        if (!string.IsNullOrEmpty(accessToken))
        {
            var refresh = data.Str("refresh_token");
            if (!string.Equals(data.Str("token_type"), "bearer", StringComparison.OrdinalIgnoreCase) ||
                !data.TryGetProperty("expires_in", out var expiresIn) || expiresIn.ValueKind != JsonValueKind.Number || expiresIn.GetDouble() <= 0 ||
                ((requireRefresh || scopes.Contains("offline_access")) && string.IsNullOrEmpty(refresh)))
            {
                throw new ChatGptException("invalid_token_response", "ChatGPT returned incomplete credentials.");
            }
            connection.AccessToken = accessToken;
            connection.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn.GetDouble());
            if (!string.IsNullOrEmpty(refresh)) connection.RefreshToken = refresh;
        }
        else if (requireRefresh || scopes.Contains("offline_access") || scopes.Contains("chatgpt.tokens.use.direct"))
        {
            throw new ChatGptException("invalid_token_response", "ChatGPT did not return the requested credentials.");
        }
        connection.Scopes = scopes;
    }

    private sealed record Identity(string Subject, string? Email, string? Name);

    private static async Task<Identity> VerifyIdentityAsync(Discovery provider, string idToken, string clientId, string? nonce, CancellationToken ct)
    {
        using var jwksResponse = await Http.GetAsync(provider.JwksUri, ct);
        if (jwksResponse.StatusCode != HttpStatusCode.OK)
            throw new ChatGptException("identity_verification_unavailable", "ChatGPT identity verification is temporarily unavailable.", true);
        var jwks = new JsonWebKeySet(await jwksResponse.Content.ReadAsStringAsync(ct));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(5),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
        });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
            throw new ChatGptException("invalid_id_token", $"The ChatGPT identity could not be verified ({result.Exception?.GetType().Name}).");

        string? Claim(string type) => token.TryGetPayloadValue<string>(type, out var value) ? value : null;
        var subject = Claim("sub");
        var azp = Claim("azp");
        if (string.IsNullOrEmpty(subject) || (nonce is not null && Claim("nonce") != nonce) || (azp is not null && azp != clientId) ||
            (token.Audiences.Count() > 1 && azp != clientId))
        {
            throw new ChatGptException("invalid_id_token", "The ChatGPT identity could not be verified.");
        }
        return new Identity(subject, Claim("email"), Claim("name"));
    }

    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
