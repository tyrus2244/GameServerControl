using GameServerControl.Shared;

namespace GameServerControl.Agent.Rcon;

/// <summary>
/// Palworld's RCON returns CSV-ish text from "ShowPlayers":
///   name,playeruid,steamid
///   Tyrus,12345,76561197960287930
/// Broadcast doesn't accept spaces in vanilla Palworld; replace with underscores.
/// </summary>
public sealed class PalworldRcon : IGameRcon
{
    private readonly SourceRconClient _rcon;
    public PalworldRcon(SourceRconClient rcon) { _rcon = rcon; }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && def.SteamAppId == "2394010";

    public async Task<RconPlayer[]> ListPlayersAsync(string host, int port, string password, CancellationToken ct)
    {
        var raw = await _rcon.ExecuteAsync(host, port, password, "ShowPlayers", ct: ct);
        return ParsePlayers(raw);
    }

    public async Task<RconResponse> ExecuteAsync(string host, int port, string password, RconStandardCommand cmd, string? payload, CancellationToken ct)
    {
        try
        {
            var command = cmd switch
            {
                RconStandardCommand.ListPlayers      => "ShowPlayers",
                RconStandardCommand.Save             => "Save",
                RconStandardCommand.BroadcastMessage => "Broadcast " + (payload ?? "").Replace(" ", "_"),
                RconStandardCommand.KickPlayer       => "KickPlayer " + (payload ?? ""),
                RconStandardCommand.BanPlayer        => "BanPlayer " + (payload ?? ""),
                RconStandardCommand.Shutdown         => "Shutdown " + (string.IsNullOrWhiteSpace(payload) ? "1 ServerRestart" : payload),
                RconStandardCommand.Raw              => payload ?? "",
                _ => throw new ArgumentOutOfRangeException(nameof(cmd))
            };
            var raw = await _rcon.ExecuteAsync(host, port, password, command, ct: ct);
            return new RconResponse(true, raw, null);
        }
        catch (Exception ex)
        {
            return new RconResponse(false, "", ex.Message);
        }
    }

    private static RconPlayer[] ParsePlayers(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<RconPlayer>();
        var lines = raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<RconPlayer>();
        foreach (var line in lines)
        {
            // Skip the header row Palworld emits
            if (line.StartsWith("name,", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            list.Add(new RconPlayer(
                SteamId: parts[2].Trim(),
                Name: parts[0].Trim(),
                PlayerUid: parts[1].Trim(),
                Address: null));
        }
        return list.ToArray();
    }
}
