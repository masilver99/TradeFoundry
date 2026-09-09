using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradeFoundry.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class TestErrorModel(IWebHostEnvironment environment) : PageModel
{
    public IActionResult OnGet()
    {
        if (environment.IsProduction())
        {
            return NotFound();
        }

        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return new EmptyResult();
    }
}
