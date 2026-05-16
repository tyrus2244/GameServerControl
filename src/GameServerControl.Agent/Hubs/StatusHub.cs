using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Hubs;

[Authorize]
public sealed class StatusHub : Hub
{
}
