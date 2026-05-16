using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GameServerControl.Agent.Auth;

public sealed class TokenAuthOptions : AuthenticationSchemeOptions
{
    public string Token { get; set; } = "";
}

public sealed class TokenAuthHandler : AuthenticationHandler<TokenAuthOptions>
{
    public const string SchemeName = "Token";

    public TokenAuthHandler(IOptionsMonitor<TokenAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

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

        var expected = Options.Token ?? "";
        if (!CryptoEquals(presented, expected))
            return Task.FromResult(AuthenticateResult.Fail("invalid token"));

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "operator") }, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool CryptoEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
