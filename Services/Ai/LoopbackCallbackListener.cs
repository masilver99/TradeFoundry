using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeFoundry.Services.Ai;

public sealed record LoopbackCallback(string Code, string ClientId);

/// <summary>
/// Minimal one-shot HTTP listener on 127.0.0.1 for the OAuth redirect. Requests with the wrong host, path, or state
/// are answered and ignored so unrelated loopback traffic cannot consume the pending sign-in.
/// </summary>
public sealed class LoopbackCallbackListener : IDisposable
{
    public const string CallbackPath = "/auth/callback";
    private const string Script = "history.replaceState(null, \"\", \"/auth/complete\");";

    private readonly TcpListener _listener;
    private readonly string _state;
    private readonly string? _savedClientId;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    private LoopbackCallbackListener(TcpListener listener, string state, string? savedClientId)
    {
        _listener = listener;
        _state = state;
        _savedClientId = savedClientId;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }
    public string RedirectUri => $"http://127.0.0.1:{Port}{CallbackPath}";

    public static LoopbackCallbackListener Start(int port, string state, string? savedClientId)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            throw new AiProviderException("callback_port_unavailable",
                $"The sign-in port {port} is in use. Close the other program or set Ai:RedirectPort to 0.");
        }
        return new LoopbackCallbackListener(listener, state, savedClientId);
    }

    public async Task<LoopbackCallback> WaitAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                throw new AiProviderException("cancelled", "Sign-in was cancelled or timed out.");
            }

            using (client)
            {
                LoopbackCallback? result;
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    result = await HandleAsync(client.GetStream(), requestTimeout.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException || (ex is OperationCanceledException && !token.IsCancellationRequested))
                {
                    continue;
                }
                if (result is not null) return result;
            }
        }
    }

    /// <returns>The callback, or null when the request was not the expected redirect and listening should continue.</returns>
    private async Task<LoopbackCallback?> HandleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(stream, cancellationToken);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var host = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim())
            .FirstOrDefault();
        var target = requestLine.Length >= 2 ? requestLine[1] : string.Empty;
        var path = target.Split('?', 2)[0];

        if (requestLine.Length < 3 || requestLine[0] != "GET" || host != $"127.0.0.1:{Port}" || path != CallbackPath)
        {
            await RespondAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found", csp: null, cancellationToken);
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty);
        var states = query.GetValues("state");
        if (states is not { Length: 1 } ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(states[0]), Encoding.UTF8.GetBytes(_state)))
        {
            await RespondAsync(stream, "400 Bad Request", "text/plain; charset=utf-8",
                "Invalid sign-in state. Return to the browser tab that started sign-in.", csp: null, cancellationToken);
            return null;
        }

        var scriptHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script)));
        await RespondAsync(stream, "200 OK", "text/html; charset=utf-8",
            "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Return to TradeFoundry</title>" +
            "<style>body{font:17px system-ui;max-width:32rem;margin:18vh auto;padding:24px}h1{font-size:26px}</style>" +
            "<h1>Return to TradeFoundry</h1><p>TradeFoundry is finishing your ChatGPT connection. You can close this tab.</p>" +
            $"<script>{Script}</script></html>",
            $"default-src 'none'; script-src 'sha256-{scriptHash}'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'",
            cancellationToken);

        if (query["error"] is { } error)
            throw ChatGptErrors.FromBody(JsonSerializer.Serialize(new { error }), null, null);

        var codes = query.GetValues("code");
        var returnedClientIds = query.GetValues("client_id");
        var clientId = returnedClientIds?.FirstOrDefault() ?? _savedClientId;
        if (codes is not { Length: 1 } || string.IsNullOrEmpty(codes[0]) || returnedClientIds is { Length: > 1 } ||
            clientId is null || clientId == "dynamic_agent_client" || clientId.Length > 200 ||
            !clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
            (_savedClientId is not null && returnedClientIds is { Length: 1 } && returnedClientIds[0] != _savedClientId))
        {
            throw new AiProviderException("registration_incomplete", "ChatGPT did not complete app registration. Try connecting again.");
        }
        return new LoopbackCallback(codes[0], clientId);
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string contentType, string body, string? csp, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n");
        if (csp is not null) headers.Append("Content-Security-Policy: ").Append(csp).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
