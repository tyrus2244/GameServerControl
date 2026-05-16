using System.Collections.Concurrent;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed class StatusTracker
{
    private readonly ConcurrentDictionary<string, ServerStatus> _byId = new();

    public ServerStatus Get(string id) =>
        _byId.GetOrAdd(id, _ => new ServerStatus(id, VmState.Unknown, ProcessState.Unknown, null, null, null, null));

    public ServerStatus Update(string id, Func<ServerStatus, ServerStatus> updater)
    {
        return _byId.AddOrUpdate(id,
            _ => updater(new ServerStatus(id, VmState.Unknown, ProcessState.Unknown, null, null, null, null)),
            (_, cur) => updater(cur));
    }

    public IEnumerable<ServerStatus> Snapshot() => _byId.Values.ToArray();
}
