using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Rcon;

public sealed class RconService
{
    private readonly IEnumerable<IGameRcon> _handlers;
    private readonly HypervService _hv;
    private readonly ILogger<RconService> _logger;

    public RconService(IEnumerable<IGameRcon> handlers, HypervService hv, ILogger<RconService> logger)
    {
        _handlers = handlers;
        _hv = hv;
        _logger = logger;
    }

    public IGameRcon? GetHandler(ServerDef def) => _handlers.FirstOrDefault(h => h.Supports(def));

    public async Task<(string host, int port, string password)> ResolveAsync(ServerDef def, CancellationToken ct)
    {
        if (def.RconPort is null || def.RconPort.Value <= 0)
            throw new InvalidOperationException("RCON port is not set for this server.");
        if (string.IsNullOrEmpty(def.RconPassword))
            throw new InvalidOperationException("RCON password is not set for this server.");

        var host = def.RconHost ?? "auto";
        if (string.Equals(host, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(host))
        {
            if (def.HostingMode == HostingMode.BareMetal)
            {
                host = "127.0.0.1";
            }
            else
            {
                var ip = await _hv.GetVmIpAsync(def.VmName, ct);
                if (string.IsNullOrEmpty(ip))
                    throw new InvalidOperationException("Could not auto-discover VM IP (no IPv4 reported by Hyper-V Integration Services). Set RconHost explicitly.");
                host = ip;
            }
        }
        return (host, def.RconPort.Value, def.RconPassword);
    }

    public async Task<RconResponse> RunAsync(ServerDef def, RconStandardCommand cmd, string? payload, CancellationToken ct)
    {
        var handler = GetHandler(def);
        if (handler is null)
            return new RconResponse(false, "", $"No RCON handler for game (SteamAppId={def.SteamAppId}). Palworld is the only supported game in v1.");
        try
        {
            var (host, port, pw) = await ResolveAsync(def, ct);
            return await handler.ExecuteAsync(host, port, pw, cmd, payload, ct);
        }
        catch (Exception ex)
        {
            return new RconResponse(false, "", ex.Message);
        }
    }

    public async Task<RconPlayer[]> ListPlayersAsync(ServerDef def, CancellationToken ct)
    {
        var handler = GetHandler(def);
        if (handler is null) return Array.Empty<RconPlayer>();
        try
        {
            var (host, port, pw) = await ResolveAsync(def, ct);
            return await handler.ListPlayersAsync(host, port, pw, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCON ListPlayers failed for {Id}", def.Id);
            return Array.Empty<RconPlayer>();
        }
    }
}
