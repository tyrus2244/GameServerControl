using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GameServerControl.Agent.Auth;

public sealed class TokenAuthOptions : AuthenticationSchemeOptions
{
    // Legacy field — kept for back-compat. The actual token list now lives in TokenRegistry,
    // which falls back to <c>Agent:ApiToken</c> automatically.
    public string Token { get; set; } = "";
}

public sealed class TokenAuthHandler : AuthenticationHandler<TokenAuthOptions>
{
    public const string SchemeName = "Token";
    public const string RoleClaim = "gsc:role";
    public const string TokenIdClaim = "gsc:tokenId";

    private readonly TokenRegistry _registry;

    public TokenAuthHandler(IOptionsMonitor<TokenAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokenRegistry registry)
        : base(options, logger, encoder)
    {
        _registry = registry;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? presented = null;

        if (Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var s = auth.ToString();
            if (s.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                presented = s.Substring("Bearer ".Length).Trim();
        }
        if (presented is null && Request.Query.TryGetValue("access_token", out var q))
            presented = q.ToString();

        if (string.IsNullOrEmpty(presented))
            return Task.FromResult(AuthenticateResult.NoResult());

        var match = _registry.Lookup(presented);
        if (match is null)
            return Task.FromResult(AuthenticateResult.Fail("invalid token"));

        var (id, name, role) = match.Value;
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(TokenIdClaim, id),
            new Claim(RoleClaim, role.ToString())
        }, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
