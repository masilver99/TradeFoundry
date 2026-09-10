using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public SetupModel(TradeFoundryDb database) => _database = database;

    [BindProperty] public string DisplayName { get; set; } = "Owner";
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet() => _database.HasOwner() ? RedirectToPage("/Login") : Page();

    public IActionResult OnPost()
    {
        if (_database.HasOwner()) return RedirectToPage("/Login");
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Owner" : DisplayName.Trim();
        Password ??= string.Empty;
        ConfirmPassword ??= string.Empty;
        if (Password.Length > 0 && Password.Length < 8) ErrorMessage = "Use at least 8 characters, or leave the password empty.";
        else if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal)) ErrorMessage = "The passwords do not match.";
        if (ErrorMessage is not null) return Page();
        _database.CreateOwner(DisplayName, PasswordService.Hash(Password));
        TempData["FlashMessage"] = "Setup complete. Sign in to open your journal.";
        return RedirectToPage("/Login");
    }
}
