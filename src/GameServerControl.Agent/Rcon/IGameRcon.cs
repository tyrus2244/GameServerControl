using GameServerControl.Shared;

namespace GameServerControl.Agent.Rcon;

public interface IGameRcon
{
    bool Supports(ServerDef def);
    Task<RconPlayer[]> ListPlayersAsync(string host, int port, string password, CancellationToken ct);
    Task<RconResponse> ExecuteAsync(string host, int port, string password, RconStandardCommand cmd, string? payload, CancellationToken ct);
}
