using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services.Ai;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class AiSettingsModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly IAiReviewProvider _provider;
    private readonly ChatGptSignInCoordinator _signIn;
    private readonly AiOptions _options;
    private readonly ILogger<AiSettingsModel> _logger;

    public AiSettingsModel(TradeFoundryDb database, IAiReviewProvider provider, ChatGptSignInCoordinator signIn, IOptions<AiOptions> options, ILogger<AiSettingsModel> logger)
    {
        _database = database;
        _provider = provider;
        _signIn = signIn;
        _options = options.Value;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string? ModelSlug { get; set; }
    [BindProperty] public string? ReasoningEffort { get; set; }

    public Journal? Journal { get; private set; }
    public bool Enabled => _options.Enabled;
    public bool IsFakeProvider => _provider.Id == AiOptions.FakeProvider;
    public string ProviderName => _provider.DisplayName;
    public AiConnectionStatus Status { get; private set; } = new();
    public ChatGptSignInStatus SignInProgress { get; private set; } = new(ChatGptSignInState.Idle);
    public IReadOnlyList<AiModel> Models { get; private set; } = Array.Empty<AiModel>();
    public string? ModelError { get; private set; }
    public bool ModelErrorIsUsageLimit { get; private set; }
    public string? SignInUnavailableReason { get; private set; }
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }
    public bool FlashIsUsageLimit { get; private set; }
    public string ManageUsageUrl => ChatGptErrors.ManageUsageUrl;
    public string BasePath => $"/journal/{JournalId:D}/settings/ai";

    public AiModel? SelectedModel =>
        Models.FirstOrDefault(m => m.Slug == Status.Model) ?? Models.FirstOrDefault(m => m.SupportsImages) ?? Models.FirstOrDefault();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        FlashIsUsageLimit = TempData["FlashUsageLimit"] as bool? ?? false;

        if (!Enabled) return Page();

        SignInProgress = _signIn.Current;
        if (SignInProgress.State == ChatGptSignInState.Succeeded)
        {
            FlashMessage = "ChatGPT connected.";
            FlashKind = "success";
            _signIn.Acknowledge();
            SignInProgress = _signIn.Current;
        }
        else if (SignInProgress.State == ChatGptSignInState.Failed)
        {
            FlashMessage = SignInProgress.ErrorMessage;
            FlashKind = "error";
            _signIn.Acknowledge();
            SignInProgress = _signIn.Current;
        }

        SignInUnavailableReason = SignInUnavailable();
        Status = _provider.GetStatus();
        if (Status.CanRun)
        {
            try
            {
                Models = await _provider.ListModelsAsync(cancellationToken);
            }
            catch (AiProviderException ex)
            {
                ModelError = ex.Message;
                ModelErrorIsUsageLimit = ex.IsUsageLimit;
                Status = _provider.GetStatus();
            }
        }
        return Page();
    }

    public JsonResult OnGetSignInStatus()
    {
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { state = _signIn.Current.State.ToString().ToLowerInvariant() });
    }

    public async Task<IActionResult> OnPostConnectAsync(bool reconsent, CancellationToken cancellationToken)
    {
        if (!Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        if (SignInUnavailable() is { } reason) return Flash(reason, "error");

        var status = await _signIn.StartAsync(reconsent, cancellationToken);
        if (status.State == ChatGptSignInState.Failed)
        {
            _signIn.Acknowledge();
            return Flash(status.ErrorMessage ?? "Sign-in could not start.", "error");
        }
        // The request came from this machine (see SignInUnavailable), so the system browser is the user's browser.
        if (status.AuthorizationUrl is { } url && !DesktopRuntime.TryOpenBrowser(url))
            _logger.LogInformation("Could not open the system browser for ChatGPT sign-in; the page shows a link instead.");
        return Redirect(BasePath);
    }

    public IActionResult OnPostCancelSignIn()
    {
        if (!Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        _signIn.Cancel();
        return Redirect(BasePath);
    }

    public async Task<IActionResult> OnPostSavePreferencesAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        IReadOnlyList<AiModel> models;
        try { models = await _provider.ListModelsAsync(cancellationToken); }
        catch (AiProviderException ex) { return Flash(ex.Message, "error", ex.IsUsageLimit); }

        var model = models.FirstOrDefault(m => m.Slug == ModelSlug);
        if (model is null) return Flash("Choose a model from the list.", "error");
        var effort = string.IsNullOrWhiteSpace(ReasoningEffort) ? null : ReasoningEffort;
        if (effort is not null && !model.ReasoningLevels.Contains(effort))
            return Flash($"{model.DisplayName} does not support the {effort} reasoning level.", "error");
        _provider.SavePreferences(model.Slug, effort);
        return Flash($"AI review will use {model.DisplayName}{(effort is null ? string.Empty : $" with {effort} reasoning")}.", "success");
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            var models = await _provider.ListModelsAsync(cancellationToken);
            var status = _provider.GetStatus();
            var model = models.FirstOrDefault(m => m.Slug == status.Model) ?? models.FirstOrDefault()
                ?? throw new AiProviderException("no_models", "No models are available for this connection.");
            var result = await _provider.StreamAsync(new AiRequest
            {
                Model = model.Slug,
                Messages = [AiMessage.Text(AiRole.User, "Reply with exactly: TradeFoundry connection works.")],
            }, null, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.Text)) return Flash("The test response was empty. Try again.", "error");
            return Flash($"Test succeeded with {model.DisplayName} in {result.Elapsed.TotalSeconds:F1}s ({result.Usage.InputTokens ?? 0} tokens in, {result.Usage.OutputTokens ?? 0} out): “{Truncate(result.Text, 120)}”", "success");
        }
        catch (AiProviderException ex)
        {
            return Flash(ex.Message, "error", ex.IsUsageLimit);
        }
    }

    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        _signIn.Cancel();
        try
        {
            await _provider.DisconnectAsync(cancellationToken);
            return Flash("ChatGPT disconnected. TradeFoundry removed its saved credentials and revoked access.", "success");
        }
        catch (AiProviderException ex)
        {
            return Flash(ex.Message, "warning");
        }
    }

    private string? SignInUnavailable()
    {
        if (IsFakeProvider) return "The offline test provider is active (Ai:Provider=fake); there is nothing to connect.";
        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            return "ChatGPT sign-in needs TradeFoundry running directly on your computer. It can't complete inside a Docker container, because ChatGPT returns to 127.0.0.1 on the machine running your browser.";
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
            return "ChatGPT sign-in must be started from a browser on the computer running TradeFoundry, because ChatGPT returns to 127.0.0.1 on that computer.";
        return null;
    }

    private RedirectResult Flash(string message, string kind, bool usageLimit = false)
    {
        TempData["FlashMessage"] = message;
        TempData["FlashKind"] = kind;
        if (usageLimit) TempData["FlashUsageLimit"] = true;
        return Redirect(BasePath);
    }

    private static string Truncate(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
