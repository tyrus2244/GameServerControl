using System.Text.Json;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Append-only JSON-lines audit log of authenticated API mutations.
///
/// One line per mutating request:
///   {"ts":"2026-05-16T13:45:21Z","ip":"100.90.15.50","method":"POST",
///    "path":"/api/servers/satisfactory-1/config","status":200}
///
/// Reads (<c>GET</c>) are skipped to keep the log small and useful — what matters
/// for audit is who CHANGED what, not who looked.
///
/// File is rotated daily by appending the date suffix; old files are kept indefinitely
/// (operator can prune). Path is configurable via <c>Agent:AuditLogPath</c>; default
/// next to the agent's own log directory.
/// </summary>
public sealed class AuditLogger
{
    private readonly string _logDir;
    private readonly object _writeLock = new();
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(IConfiguration cfg, IHostEnvironment env, ILogger<AuditLogger> logger)
    {
        _logger = logger;
        var configured = cfg["Agent:AuditLogPath"];
        _logDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "Logs", "audit")
            : configured;
        Directory.CreateDirectory(_logDir);
    }

    public void Record(HttpContext ctx, int statusCode)
    {
        // Skip GETs — pure reads don't change state. We log POST/PUT/DELETE/PATCH.
        var method = ctx.Request.Method;
        if (method == HttpMethods.Get || method == HttpMethods.Head || method == HttpMethods.Options)
            return;

        try
        {
            var entry = new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                method,
                path = ctx.Request.Path.Value,
                status = statusCode,
                user = ctx.User?.Identity?.Name ?? "bearer"
            };
            var line = JsonSerializer.Serialize(entry);
            var path = Path.Combine(_logDir, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (_writeLock) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Audit logging must never break the request path.
            _logger.LogDebug(ex, "Audit log write failed");
        }
    }
}

/// <summary>
/// ASP.NET Core middleware that pipes every API response into the audit log.
/// Registered after Authorization so we only log authenticated requests.
/// </summary>
public sealed class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuditLogger _log;
    public AuditMiddleware(RequestDelegate next, AuditLogger log) { _next = next; _log = log; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        await _next(ctx);
        // Only audit /api/* paths and only when the user passed auth (else status 401 already).
        if (ctx.Request.Path.StartsWithSegments("/api"))
            _log.Record(ctx, ctx.Response.StatusCode);
    }
}
