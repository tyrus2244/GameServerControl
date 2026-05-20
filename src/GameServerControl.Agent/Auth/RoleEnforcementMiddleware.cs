using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Pragmatic role enforcement: any state-mutating HTTP verb (POST/PUT/DELETE/PATCH) requires
/// the Admin role. GETs are allowed for any authenticated token.
///
/// This is preferable to retrofitting <c>.RequireAuthorization("admin")</c> on every endpoint —
/// it's one rule, easy to audit, and impossible to forget on a new endpoint.
/// </summary>
public sealed class RoleEnforcementMiddleware
{
    private readonly RequestDelegate _next;

    public RoleEnforcementMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Health + UI + SignalR negotiation work for any role.
        var p = ctx.Request.Path.Value ?? "";
        var isWrite = ctx.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH";
        if (!isWrite || !p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        // Anon endpoints (/, /health) never reach here because they're not under /api or are public.
        var roleClaim = ctx.User?.FindFirst(TokenAuthHandler.RoleClaim)?.Value;
        if (!string.Equals(roleClaim, nameof(TokenRole.Admin), StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("This token is read-only.");
            return;
        }
        await _next(ctx);
    }
}
