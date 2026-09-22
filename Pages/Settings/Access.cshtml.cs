using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class AccessModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly McpTokenService _mcpTokens;
    private readonly McpOptions _mcpOptions;

    public AccessModel(TradeFoundryDb database, McpTokenService mcpTokens, IOptions<McpOptions> mcpOptions)
    {
        _database = database;
        _mcpTokens = mcpTokens;
        _mcpOptions = mcpOptions.Value;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string ConfirmNewPassword { get; set; } = string.Empty;
    [BindProperty] public string McpTokenName { get; set; } = "Local AI client";
    [BindProperty] public List<Guid> McpJournalIds { get; set; } = new();
    [BindProperty] public bool McpAllowFeeProfileWrites { get; set; }

    public Journal? Journal { get; private set; }
    public IReadOnlyList<Journal> Journals { get; private set; } = Array.Empty<Journal>();
    public IReadOnlyList<McpAccessToken> McpTokens { get; private set; } = Array.Empty<McpAccessToken>();
    public bool McpEnabled => _mcpOptions.Enabled;
    public string McpEndpoint => _mcpOptions.Url.TrimEnd('/') + "/mcp";
    public string? NewMcpToken { get; private set; }
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }

    public IActionResult OnGet()
    {
        Load();
        if (Journal is null) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostChangePassword()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var currentHash = _database.GetOwnerPasswordHash();
        if (currentHash is null || !PasswordService.Verify(CurrentPassword, currentHash))
        {
            TempData["FlashMessage"] = "The current password is incorrect.";
            TempData["FlashKind"] = "error";
        }
        else if (!string.Equals(NewPassword, ConfirmNewPassword, StringComparison.Ordinal))
        {
            TempData["FlashMessage"] = "The new passwords do not match.";
            TempData["FlashKind"] = "error";
        }
        else if (!_database.UpdateOwnerPasswordHash(PasswordService.Hash(NewPassword)))
        {
            TempData["FlashMessage"] = "The password could not be changed.";
            TempData["FlashKind"] = "error";
        }
        else
        {
            TempData["FlashMessage"] = "Password changed.";
            TempData["FlashKind"] = "success";
        }

        return Redirect($"/journal/{JournalId:D}/settings/access");
    }

    public IActionResult OnPostCreateMcpToken()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        try
        {
            var created = _mcpTokens.Create(McpTokenName, McpJournalIds, McpAllowFeeProfileWrites);
            NewMcpToken = created.Secret;
            FlashMessage = "MCP access token created. Copy it now; TradeFoundry will not show it again.";
            FlashKind = "success";
        }
        catch (InvalidOperationException ex)
        {
            FlashMessage = ex.Message;
            FlashKind = "error";
        }
        Response.Headers.CacheControl = "no-store";
        Load();
        return Page();
    }

    public IActionResult OnPostRevokeMcpToken(Guid tokenId)
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        var revoked = _mcpTokens.Revoke(tokenId);
        TempData["FlashMessage"] = revoked ? "MCP access token revoked." : "The MCP access token was already revoked or could not be found.";
        TempData["FlashKind"] = revoked ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/access");
    }

    private void Load()
    {
        Journals = _database.GetJournals();
        McpTokens = _mcpTokens.List();
        Journal = _database.GetJournal(JournalId);
    }
}
