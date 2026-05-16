namespace GameServerControl.Shared;

public sealed record RconPlayer(
    string SteamId,
    string Name,
    string? PlayerUid,
    string? Address);

public sealed record RconResponse(
    bool Success,
    string Output,
    string? Error);

public enum RconStandardCommand
{
    ListPlayers,
    Save,
    BroadcastMessage,   // payload: text
    KickPlayer,         // payload: steamId
    BanPlayer,          // payload: steamId
    Shutdown,           // payload: optional "N message"
    Raw                 // payload: literal command text
}
