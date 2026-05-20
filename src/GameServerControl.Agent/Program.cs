using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Config;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Logs;
using GameServerControl.Agent.Rcon;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Host.UseWindowsService();

var bind = builder.Configuration["Agent:Bind"] ?? "http://127.0.0.1:5099";
var bindHttps = builder.Configuration["Agent:BindHttps"];
var bindUrls = new List<string> { bind };
if (!string.IsNullOrWhiteSpace(bindHttps)) bindUrls.Add(bindHttps);
builder.WebHost.UseUrls(bindUrls.ToArray());

if (!string.IsNullOrWhiteSpace(bindHttps))
{
    builder.WebHost.ConfigureKestrel(opts =>
    {
        var pfxPath = Path.Combine(AppContext.BaseDirectory, "agent.pfx");
        var pfxPassword = builder.Configuration["Agent:CertPassword"] ?? "gscagent";
        var sanHosts = new[] { Environment.MachineName.ToLowerInvariant(), "gamingserver" };
        var sanIps = new List<System.Net.IPAddress>();
        try { sanIps.Add(System.Net.IPAddress.Parse("100.90.15.50")); } catch { /* not on tailnet */ }
        var cert = CertHelper.LoadOrCreate(pfxPath, pfxPassword, sanHosts, sanIps.ToArray());
        opts.ConfigureHttpsDefaults(o => o.ServerCertificate = cert);
    });
}

builder.Services.AddSingleton<PowerShellRunner>();
builder.Services.AddSingleton<HypervService>();
builder.Services.AddSingleton<GuestProcessService>();
builder.Services.AddSingleton<LocalProcessService>();
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddSingleton<StatusTracker>();
builder.Services.AddSingleton<ServerOrchestrator>();
builder.Services.AddSingleton<MaintenanceScheduler>();
builder.Services.AddSingleton<ValheimConfig>();
builder.Services.AddSingleton<PalworldConfig>();
builder.Services.AddSingleton<WindroseConfig>();
builder.Services.AddSingleton<SatisfactoryConfig>();
builder.Services.AddSingleton<GameServerControl.Agent.Admin.SatisfactoryAdminClient>();
// Dynamic auto-discovery extensions (one per game). Inject as IEnumerable in consumers.
builder.Services.AddSingleton<IDynamicSchemaExtension, PalworldDynamicSchema>();
builder.Services.AddSingleton<IDynamicSchemaExtension, SatisfactoryDynamicSchema>();
// Discovery is cross-platform: probes Windows registry on Windows, Linux Steam paths on Linux.
builder.Services.AddSingleton<GameServerControl.Agent.Discovery.SteamLibraryReader>();
builder.Services.AddSingleton<GameServerControl.Agent.Discovery.ServerDiscoveryService>();
builder.Services.AddSingleton<GameConfigFactory>();
// Server-side mod management (one IModManager per game family). Add more here as they ship.
builder.Services.AddSingleton<GameServerControl.Agent.Mods.ThunderstoreClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.ValheimBepInExModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.SatisfactoryThunderstoreModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.ModManagerRegistry>();
builder.Services.AddSingleton<SourceRconClient>();
builder.Services.AddSingleton<IGameRcon, PalworldRcon>();
builder.Services.AddSingleton<RconService>();
builder.Services.AddSingleton<LogTailService>();

// Security: generate a strong random API token on first boot if the operator left
// the placeholder in appsettings.json. Keeps cloned-from-GitHub deployments from
// shipping with a default credential.
{
    using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var startupLogger = startupLoggerFactory.CreateLogger("FirstRunTokenGenerator");
    if (FirstRunTokenGenerator.EnsureToken(builder.Configuration, AppContext.BaseDirectory, startupLogger, out var resolvedToken))
        builder.Configuration["Agent:ApiToken"] = resolvedToken;
}

builder.Services.AddSingleton<AuditLogger>();

builder.Services.AddAuthentication(TokenAuthHandler.SchemeName)
    .AddScheme<TokenAuthOptions, TokenAuthHandler>(TokenAuthHandler.SchemeName, opts =>
    {
        opts.Token = builder.Configuration["Agent:ApiToken"] ?? "";
    });
builder.Services.AddAuthorization(o =>
{
    o.DefaultPolicy = new AuthorizationPolicyBuilder(TokenAuthHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Web UI — anonymous HTML page. All API calls from it still go through the auth-gated /api routes below.
app.MapGet("/", () =>
{
    var htmlPath = Path.Combine(AppContext.BaseDirectory, "WebUi", "index.html");
    return File.Exists(htmlPath)
        ? Results.File(htmlPath, "text/html; charset=utf-8")
        : Results.NotFound("WebUi/index.html missing in publish output");
});

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuditMiddleware>();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/health", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));

api.MapGet("/discover", (GameServerControl.Agent.Discovery.ServerDiscoveryService disco) =>
    Results.Ok(disco.Discover()));

api.MapGet("/download/client", (HttpContext http) =>
{
    if (!OperatingSystem.IsWindows())
    {
        // The WPF client is Windows-only — no native Linux client exists. Linux operators use
        // the web UI at "/" or the prebuilt Windows client artifact from GitHub Releases.
        return Results.NotFound(
            "No native client bundle available on this Linux agent. " +
            "Use the web UI at /, or download a Windows client from " +
            "https://github.com/tyrus2244/GameServerControl/releases / Actions.");
    }
    var zipPath = @"C:\GameServerControl\Client.zip";
    var clientDir = @"C:\GameServerControl\Client";
    // Auto-regenerate if missing or older than the published exe
    var clientExe = Path.Combine(clientDir, "GameServerControl.exe");
    var needs = !File.Exists(zipPath) || (File.Exists(clientExe) && File.GetLastWriteTimeUtc(clientExe) > File.GetLastWriteTimeUtc(zipPath));
    if (needs && Directory.Exists(clientDir))
    {
        // Copy to temp first (handles file locks if the client is currently running)
        var tmp = Path.Combine(Path.GetTempPath(), "GSCBundle-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(tmp);
        foreach (var f in Directory.EnumerateFiles(clientDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(clientDir, f);
            var dst = Path.Combine(tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
        if (File.Exists(zipPath)) File.Delete(zipPath);
        System.IO.Compression.ZipFile.CreateFromDirectory(tmp, zipPath, System.IO.Compression.CompressionLevel.Optimal, false);
        Directory.Delete(tmp, true);
    }
    if (!File.Exists(zipPath)) return Results.NotFound("Client.zip not available.");
    return Results.File(zipPath, "application/zip", "GameServerController.zip");
}).RequireAuthorization();

api.MapGet("/servers", (ServerRegistry reg) =>
    Results.Ok(reg.All));

api.MapPost("/servers/reload", (ServerRegistry reg) =>
{
    reg.Reload();
    return Results.Ok(new { reloaded = true, count = reg.All.Count });
});

api.MapPost("/servers", (ServerDef def, ServerStore store) =>
{
    try { return Results.Ok(store.Add(def)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

api.MapPut("/servers/{id}", (string id, ServerDef def, ServerStore store) =>
{
    try { return Results.Ok(store.Update(id, def)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapDelete("/servers/{id}", (string id, ServerStore store) =>
{
    return store.Delete(id) ? Results.NoContent() : Results.NotFound();
});

api.MapGet("/servers/{id}/config", async (
    string id,
    ServerRegistry reg,
    GameConfigFactory factory,
    IEnumerable<IDynamicSchemaExtension> dynamicExtensions,
    CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var schema = ConfigSchemas.For(def);
    var handler = factory.For(def);

    var values = handler is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                 : await handler.ReadAsync(def, ct);

    // Compose curated + dynamic auto-discovery. Dynamic extensions skip keys the
    // curated schema already covers, so each setting appears exactly once.
    var sections = new List<ConfigSection>();
    if (schema is not null) sections.AddRange(schema.Sections);
    var curatedKeys = new HashSet<string>(
        schema?.AllFields.Select(f => f.Key) ?? Array.Empty<string>(),
        StringComparer.OrdinalIgnoreCase);

    foreach (var ext in dynamicExtensions)
    {
        if (!ext.Supports(def)) continue;
        var dyn = await ext.BuildAsync(def, curatedKeys, ct);
        if (dyn is null) continue;
        sections.Add(dyn.Section);
        foreach (var (k, v) in dyn.Values) values[k] = v;
        // Prevent another extension from re-adding the same keys
        foreach (var f in dyn.Section.Fields) curatedKeys.Add(f.Key);
    }

    var composedSchema = schema is null
        ? (sections.Count == 0 ? null : new ConfigSchema("auto", def.Name, sections.ToArray()))
        : schema with { Sections = sections.ToArray() };

    return Results.Ok(new { schema = composedSchema, values });
});

api.MapPost("/servers/{id}/logs/tail", (string id, LogTailRequest req, ServerRegistry reg, LogTailService tail) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (req.Action.Equals("start", StringComparison.OrdinalIgnoreCase))
    {
        var ok = tail.Start(id);
        return ok ? Results.Ok(new { tailing = true }) : Results.BadRequest(new { error = "Could not start tail (no LogPathInGuest or not BareMetal?)" });
    }
    if (req.Action.Equals("stop", StringComparison.OrdinalIgnoreCase))
    {
        tail.Stop(id);
        return Results.Ok(new { tailing = false });
    }
    return Results.BadRequest(new { error = "action must be 'start' or 'stop'" });
});

api.MapGet("/servers/{id}/logs/tail", (string id, LogTailService tail) =>
    Results.Ok(new { tailing = tail.IsTailing(id) }));

api.MapGet("/servers/{id}/autostart", async (string id, ServerRegistry reg, LocalProcessService local, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        return Results.Ok(new { supported = false, enabled = (bool?)null });
    var enabled = await local.GetAutostartAsync(def.ScheduledTaskName, ct);
    return Results.Ok(new { supported = true, enabled });
});

api.MapPost("/servers/{id}/autostart", async (string id, AutostartRequest req, ServerRegistry reg, LocalProcessService local, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        return Results.BadRequest(new { error = "Server has no ScheduledTaskName set." });
    var ok = await local.SetAutostartAsync(def.ScheduledTaskName, req.Enabled, ct);
    return ok ? Results.Ok(new { enabled = req.Enabled }) : Results.StatusCode(502);
});

// Honest per-game guidance when no marketplace integration exists yet.
// Surfaced via /mods and /mods/search responses so the UI can render a useful message
// instead of a flat "unsupported".
static string ModUnsupportedReason(ServerDef def) => def.SteamAppId switch
{
    "2394010" => "Palworld doesn't have a centralized mod marketplace yet. " +
                 "Server-side admin tools like Palguard exist — install manually into " +
                 "PalServer\\Pal\\Binaries\\Win64\\. Follow Palguard's README on GitHub.",
    "4129620" => "Windrose's modding ecosystem is still developing — no community marketplace yet. " +
                 "Watch the official Windrose Discord for emerging tools.",
    "2430930" => "ARK: Survival Ascended distributes mods through Steam Workshop, not Thunderstore. " +
                 "Workshop-based mod management isn't wired into the dashboard yet.",
    "376030"  => "ARK: Survival Evolved distributes mods through Steam Workshop, not Thunderstore. " +
                 "Workshop-based mod management isn't wired into the dashboard yet.",
    _         => "No mod manager registered for this game. Manage mods on disk for now."
};

api.MapGet("/servers/{id}/mods", async (string id, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null)
        return Results.Ok(new ModListResponse(Array.Empty<ModInfo>(), false, ModUnsupportedReason(def), null));
    var list = await mgr.ListAsync(def, ct);
    return Results.Ok(new ModListResponse(list, true, null, mgr.ModsFolder(def)));
});

api.MapGet("/servers/{id}/mods/search", async (string id, string? q, int? limit, bool? serverSideOnly, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null)
        return Results.Ok(new ModSearchResponse(Array.Empty<ModSearchResult>(), false, null, ModUnsupportedReason(def)));
    // Default to server-side-only so the UI is safe-by-default: every result shown
    // can be installed without asking clients to do anything on their end.
    var results = await mgr.SearchAsync(def, q ?? "", limit ?? 30, serverSideOnly ?? true, ct);
    return Results.Ok(new ModSearchResponse(results, true, mgr.MarketplaceSource(def), null));
});

api.MapPost("/servers/{id}/mods/install", async (string id, ModInstallRequest req, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.BadRequest(new ModInstallResult(false, null, "Mod management not supported for this game."));
    var result = await mgr.InstallFromUrlAsync(def, req.Url, req.DisplayName, ct);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

api.MapDelete("/servers/{id}/mods/{modId}", async (string id, string modId, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.BadRequest(new { error = "Mod management not supported for this game." });
    var ok = await mgr.UninstallAsync(def, modId, ct);
    return ok ? Results.Ok(new { uninstalled = modId }) : Results.NotFound(new { error = "Mod not found." });
});

api.MapGet("/servers/{id}/rcon/players", async (string id, ServerRegistry reg, RconService rcon, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    return Results.Ok(await rcon.ListPlayersAsync(def, ct));
});

api.MapPost("/servers/{id}/rcon/command", async (string id, RconCommandRequest req, ServerRegistry reg, RconService rcon, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var result = await rcon.RunAsync(def, req.Command, req.Payload, ct);
    return Results.Ok(result);
});

api.MapPut("/servers/{id}/config", async (string id, Dictionary<string, string> values, ServerRegistry reg, GameConfigFactory factory, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var handler = factory.For(def);
    if (handler is null) return Results.BadRequest(new { error = "No config handler for this game." });
    try
    {
        var ok = await handler.WriteAsync(def, values ?? new Dictionary<string, string>(), ct);
        return ok ? Results.Ok(new { written = true }) : Results.StatusCode(502);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/servers/{id}/status", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
{
    try { return Results.Ok(await orch.RefreshStatusAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapGet("/status", (StatusTracker tracker) => Results.Ok(tracker.Snapshot()));

api.MapPost("/servers/{id}/start", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.StartAsync(id, ct)));

api.MapPost("/servers/{id}/stop", async (string id, bool? stopVm, bool? force, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.StopAsync(id, stopVm ?? true, force ?? false, ct)));

api.MapPost("/servers/{id}/restart", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.RestartAsync(id, ct)));

api.MapPost("/servers/{id}/backup", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.BackupAsync(id, ct)));

api.MapPost("/servers/{id}/update", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.UpdateAsync(id, ct)));

api.MapPost("/servers/{id}/apply", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.ApplyConfigAsync(id, ct)));

api.MapGet("/servers/{id}/backups", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
{
    try { return Results.Ok(await orch.ListBackupsAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapPost("/servers/{id}/backups/{name}/restore", async (string id, string name, ServerOrchestrator orch, CancellationToken ct) =>
    Results.Ok(await orch.RestoreBackupAsync(id, name, ct)));

api.MapDelete("/servers/{id}/backups/{name}", async (string id, string name, ServerOrchestrator orch, CancellationToken ct) =>
    (await orch.DeleteBackupAsync(id, name, ct)) ? Results.NoContent() : Results.NotFound());

api.MapGet("/servers/{id}/schedule", async (string id, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    return Results.Ok(await sched.ReadAsync(id, ct));
});

api.MapPost("/servers/{id}/schedule", async (string id, MaintenanceSchedule schedule, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    await sched.ApplyAsync(id, schedule, ct);
    return Results.Ok(new { applied = true });
});

api.MapDelete("/servers/{id}/schedule", async (string id, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    await sched.ApplyAsync(id, null, ct);
    return Results.NoContent();
});

app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();

// Background poller: every 30s, refresh status for all servers and broadcast.
_ = Task.Run(async () =>
{
    var orch = app.Services.GetRequiredService<ServerOrchestrator>();
    var reg = app.Services.GetRequiredService<ServerRegistry>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        foreach (var s in reg.All)
        {
            try { await orch.RefreshStatusAsync(s.Id, app.Lifetime.ApplicationStopping); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogDebug(ex, "Status refresh failed for {Id}", s.Id); }
        }
        try { await Task.Delay(TimeSpan.FromSeconds(30), app.Lifetime.ApplicationStopping); }
        catch (OperationCanceledException) { break; }
    }
});

app.Run();

public record RconCommandRequest(RconStandardCommand Command, string? Payload);
public record AutostartRequest(bool Enabled);
public record LogTailRequest(string Action);
