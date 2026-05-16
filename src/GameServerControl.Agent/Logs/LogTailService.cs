using System.Collections.Concurrent;
using System.Text;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Logs;

/// <summary>
/// Per-server background log tail. While a server's tail is running, new lines
/// appended to its LogPathInGuest are pushed to SignalR clients as LogLine events
/// (source = "log"). Multi-client safe; each server gets at most one tail loop.
/// </summary>
public sealed class LogTailService : IDisposable
{
    private readonly IHubContext<StatusHub> _hub;
    private readonly ServerRegistry _registry;
    private readonly ILogger<LogTailService> _logger;
    private readonly ConcurrentDictionary<string, TailHandle> _active = new();

    public LogTailService(IHubContext<StatusHub> hub, ServerRegistry registry, ILogger<LogTailService> logger)
    {
        _hub = hub;
        _registry = registry;
        _logger = logger;
    }

    public bool IsTailing(string serverId) => _active.ContainsKey(serverId);

    public bool Start(string serverId)
    {
        var def = _registry.Get(serverId);
        if (def is null || string.IsNullOrWhiteSpace(def.LogPathInGuest)) return false;
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Log tail not yet supported for HostingMode={Mode}", def.HostingMode);
            return false;
        }

        return _active.GetOrAdd(serverId, _ =>
        {
            var cts = new CancellationTokenSource();
            var handle = new TailHandle(cts);
            handle.Task = Task.Run(() => TailLoop(serverId, def.LogPathInGuest!, cts.Token));
            return handle;
        }).Cts.IsCancellationRequested == false;
    }

    public bool Stop(string serverId)
    {
        if (!_active.TryRemove(serverId, out var h)) return false;
        try { h.Cts.Cancel(); } catch { /* ignore */ }
        return true;
    }

    private async Task TailLoop(string serverId, string path, CancellationToken ct)
    {
        long position = 0;
        var buf = new byte[64 * 1024];
        var carry = new StringBuilder();

        // Start from the end of the file if it already exists, so we don't dump history.
        try { if (File.Exists(path)) position = new FileInfo(path).Length; }
        catch (Exception ex) { _logger.LogDebug(ex, "stat failed for {Path}", path); }

        await EmitAsync(serverId, $"[log tail started — {path}]");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(path))
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (position > fs.Length) position = 0; // log rotated
                fs.Seek(position, SeekOrigin.Begin);

                int n;
                while ((n = await fs.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    position += n;
                    carry.Append(Encoding.UTF8.GetString(buf, 0, n));
                    var s = carry.ToString();
                    var lines = s.Replace("\r\n", "\n").Split('\n');
                    // Keep last partial line in carry
                    carry.Clear();
                    carry.Append(lines[^1]);
                    for (var i = 0; i < lines.Length - 1; i++)
                    {
                        if (!string.IsNullOrEmpty(lines[i]))
                            await EmitAsync(serverId, lines[i]);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "tail iteration failed for {Path}", path);
                await Task.Delay(2000, ct);
                continue;
            }

            try { await Task.Delay(700, ct); } catch (OperationCanceledException) { break; }
        }

        await EmitAsync(serverId, "[log tail stopped]");
    }

    private async Task EmitAsync(string serverId, string text)
    {
        var line = new LogLine(serverId, DateTimeOffset.UtcNow, "log", text);
        try { await _hub.Clients.All.SendAsync("logLine", line); }
        catch { /* hub may not be ready at startup */ }
    }

    public void Dispose()
    {
        foreach (var kv in _active.ToArray()) Stop(kv.Key);
    }

    private sealed class TailHandle
    {
        public TailHandle(CancellationTokenSource cts) { Cts = cts; }
        public CancellationTokenSource Cts { get; }
        public Task? Task { get; set; }
    }
}
