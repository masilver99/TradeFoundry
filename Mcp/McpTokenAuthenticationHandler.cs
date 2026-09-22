using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TradeFoundry.Services;

namespace TradeFoundry.Mcp;

public static class McpAuthentication
{
    public const string Scheme = "TradeFoundryMcpBearer";
    public const string Policy = "McpRead";
    public const string WritePolicy = "McpWrite";
    public const string JournalClaim = "tradefoundry:journal";
    public const string ScopeClaim = "scope";

    public static bool HasScope(ClaimsPrincipal user, string scope) => user
        .FindAll(ScopeClaim)
        .SelectMany(claim => claim.Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(value => value.Equals(scope, StringComparison.OrdinalIgnoreCase));
}

public sealed class McpTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    McpTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var secret = authorization["Bearer ".Length..].Trim();
        var token = tokens.Validate(secret);
        if (token is null)
            return Task.FromResult(AuthenticateResult.Fail("The MCP access token is invalid or revoked."));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.Id.ToString("D")),
            new(ClaimTypes.Name, token.Name),
            new(McpAuthentication.ScopeClaim, token.Scopes)
        };
        claims.AddRange(token.JournalIds.Select(id => new Claim(McpAuthentication.JournalClaim, id.ToString("D"))));
        tokens.RecordUsed(token.Id);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, McpAuthentication.Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, McpAuthentication.Scheme)));
    }
}
