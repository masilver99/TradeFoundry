using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Ai;

public enum ChatGptSignInState
{
    Idle,
    Pending,
    Succeeded,
    Failed
}

public sealed record ChatGptSignInStatus(ChatGptSignInState State, string? AuthorizationUrl = null, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>
/// Owns the single in-flight browser sign-in. The page starts it, opens the authorization URL, and polls
/// <see cref="Current"/> while the loopback listener waits for the redirect in the background.
/// </summary>
public sealed class ChatGptSignInCoordinator(
    ChatGptOAuth oauth,
    ChatGptConnectionStore store,
    IOptions<AiOptions> options,
    ILogger<ChatGptSignInCoordinator> logger) : IDisposable
{
    private readonly object _gate = new();
    private ChatGptSignInStatus _status = new(ChatGptSignInState.Idle);
    private CancellationTokenSource? _pending;

    public ChatGptSignInStatus Current
    {
        get { lock (_gate) return _status; }
    }

    public async Task<ChatGptSignInStatus> StartAsync(bool reconsent, CancellationToken cancellationToken)
    {
        Cancel();
        var connection = store.Load();
        var request = new ChatGptAuthorizationRequest(connection.HostId, connection.ClientId, connection.Email, options.Value.RedirectPort, reconsent);
        ChatGptPendingAuthorization pending;
        try
        {
            pending = await oauth.BeginAsync(request, cancellationToken);
        }
        catch (AiProviderException ex)
        {
            return Set(new ChatGptSignInStatus(ChatGptSignInState.Failed, ErrorCode: ex.Code, ErrorMessage: ex.Message));
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, options.Value.SignInTimeoutMinutes)));
        var status = new ChatGptSignInStatus(ChatGptSignInState.Pending, pending.AuthorizationUrl);
        lock (_gate)
        {
            _pending = cancellation;
            _status = status;
        }
        _ = Task.Run(() => CompleteAsync(pending, cancellation), CancellationToken.None);
        return status;
    }

    public void Cancel()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            if (_status.State == ChatGptSignInState.Pending) _status = new ChatGptSignInStatus(ChatGptSignInState.Idle);
        }
        pending?.Cancel();
    }

    /// <summary>Clears a finished result once the page has shown it.</summary>
    public void Acknowledge()
    {
        lock (_gate)
        {
            if (_status.State is ChatGptSignInState.Succeeded or ChatGptSignInState.Failed) _status = new ChatGptSignInStatus(ChatGptSignInState.Idle);
        }
    }

    private async Task CompleteAsync(ChatGptPendingAuthorization pending, CancellationTokenSource cancellation)
    {
        ChatGptSignInStatus result;
        try
        {
            using (pending)
            {
                var callback = await pending.WaitForCallbackAsync(cancellation.Token);
                store.SaveClientId(callback.ClientId);
                var signIn = await oauth.CompleteAsync(pending, callback, cancellation.Token);
                store.SaveSignIn(signIn.ClientId, signIn.Subject, signIn.Email, signIn.DisplayName, signIn.Scopes, signIn.Credentials);
                logger.LogInformation("ChatGPT connected; plan usage {Granted}.", signIn.Scopes.Contains(ChatGptConnection.DirectScope) ? "granted" : "not granted");
                result = new ChatGptSignInStatus(ChatGptSignInState.Succeeded);
            }
        }
        catch (AiProviderException ex)
        {
            result = new ChatGptSignInStatus(ChatGptSignInState.Failed, ErrorCode: ex.Code, ErrorMessage: ex.Message);
        }
        catch (OperationCanceledException)
        {
            result = new ChatGptSignInStatus(ChatGptSignInState.Failed, ErrorCode: "cancelled", ErrorMessage: "Sign-in was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatGPT sign-in failed unexpectedly.");
            result = new ChatGptSignInStatus(ChatGptSignInState.Failed, ErrorCode: "unexpected", ErrorMessage: "Sign-in failed unexpectedly. Try again.");
        }

        lock (_gate)
        {
            // A newer sign-in replaced this one; its status wins.
            if (!ReferenceEquals(_pending, cancellation))
            {
                cancellation.Dispose();
                return;
            }
            _pending = null;
            _status = result;
        }
        cancellation.Dispose();
    }

    private ChatGptSignInStatus Set(ChatGptSignInStatus status)
    {
        lock (_gate) _status = status;
        return status;
    }

    public void Dispose() => Cancel();
}
