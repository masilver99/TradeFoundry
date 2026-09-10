using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly TradeFoundryDb _database;

    public LoginModel(TradeFoundryDb database) => _database = database;

    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; private set; }
    public string? FlashMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (!_database.HasOwner()) return RedirectToPage("/Setup");
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(ReturnUrl ?? "/");
        FlashMessage = TempData["FlashMessage"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_database.HasOwner()) return RedirectToPage("/Setup");
        Password ??= string.Empty;
        var hash = _database.GetOwnerPasswordHash();
        if (hash is null || !PasswordService.Verify(Password, hash))
        {
            ErrorMessage = "That password did not unlock this instance.";
            return Page();
        }
        var owner = _database.GetOwner();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, owner?.DisplayName ?? "Owner") };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return LocalRedirect(IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }

    private static bool IsLocalUrl(string? url) => !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);
}
