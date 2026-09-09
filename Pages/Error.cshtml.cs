using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradeFoundry.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public int HttpStatusCode { get; private set; }

    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public void OnGet(int statusCode)
    {
        HttpStatusCode = statusCode;
        if (HttpStatusCode is < 400 or > 599)
        {
            HttpStatusCode = StatusCodes.Status500InternalServerError;
        }

        HttpContext.Response.StatusCode = HttpStatusCode;
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}

