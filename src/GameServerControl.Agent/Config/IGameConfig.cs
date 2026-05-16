using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

public interface IGameConfig
{
    Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct);
    Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct);
}
