using System.Net.Http;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameServerControl.Client.Services;

public sealed class StatusHubClient : IAsyncDisposable
{
    private readonly HubConnection _conn;
    public event Action<ServerStatus>? StatusChanged;
    public event Action<LogLine>? LogLine;
    public event Action<bool>? ConnectionChanged;
    public event Action<InstallProgress>? InstallProgress;

    public StatusHubClient(string agentUrl, string token)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl(agentUrl.TrimEnd('/') + "/hubs/status", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.HttpMessageHandlerFactory = inner =>
                {
                    if (inner is HttpClientHandler h)
                        h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                    return inner;
                };
                o.WebSocketConfiguration = ws =>
                {
                    ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                };
            })
            .WithAutomaticReconnect()
            .Build();

        _conn.On<ServerStatus>("statusChanged", s => StatusChanged?.Invoke(s));
        _conn.On<LogLine>("logLine", l => LogLine?.Invoke(l));
        _conn.On<InstallProgress>("installProgress", p => InstallProgress?.Invoke(p));
        _conn.Closed += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
        _conn.Reconnected += _ => { ConnectionChanged?.Invoke(true); return Task.CompletedTask; };
        _conn.Reconnecting += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _conn.StartAsync(ct);
        ConnectionChanged?.Invoke(true);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _conn.StopAsync(); } catch { /* ignore */ }
        await _conn.DisposeAsync();
    }
}
