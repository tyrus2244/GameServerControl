using GameServerControl.Shared;

namespace GameServerControl.Agent.Hyperv;

public sealed class HypervService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger<HypervService> _logger;

    public HypervService(PowerShellRunner ps, ILogger<HypervService> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    public async Task<VmState> GetStateAsync(string vmName, CancellationToken ct = default)
    {
        var r = await _ps.RunScriptAsync(
            "param($n) try { (Get-VM -Name $n -ErrorAction Stop).State.ToString() } catch { 'Missing' }",
            new Dictionary<string, object?> { ["n"] = vmName }, ct);

        if (r.Output.Length == 0)
            return VmState.Unknown;

        var s = r.Output[0]?.BaseObject?.ToString() ?? "";
        return s switch
        {
            "Off" => VmState.Off,
            "Starting" => VmState.Starting,
            "Running" => VmState.Running,
            "Stopping" => VmState.Stopping,
            "Saved" => VmState.Saved,
            "Paused" => VmState.Paused,
            _ => VmState.Unknown
        };
    }

    public async Task<bool> StartVmAsync(string vmName, CancellationToken ct = default)
    {
        var r = await _ps.RunCommandAsync("Start-VM", new Dictionary<string, object?> { ["Name"] = vmName }, ct);
        if (!r.Ok) _logger.LogError("Start-VM {Vm} failed: {Err}", vmName, r.ErrorText);
        return r.Ok;
    }

    public async Task<bool> StopVmAsync(string vmName, bool force, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["Name"] = vmName,
            ["Force"] = true,
        };
        if (force) args["TurnOff"] = true;
        var r = await _ps.RunCommandAsync("Stop-VM", args, ct);
        if (!r.Ok) _logger.LogError("Stop-VM {Vm} failed: {Err}", vmName, r.ErrorText);
        return r.Ok;
    }

    public async Task<bool> CreateCheckpointAsync(string vmName, string checkpointName, CancellationToken ct = default)
    {
        var r = await _ps.RunCommandAsync("Checkpoint-VM", new Dictionary<string, object?>
        {
            ["Name"] = vmName,
            ["SnapshotName"] = checkpointName
        }, ct);
        if (!r.Ok) _logger.LogError("Checkpoint-VM {Vm} failed: {Err}", vmName, r.ErrorText);
        return r.Ok;
    }

    public async Task<string?> GetVmIpAsync(string vmName, CancellationToken ct = default)
    {
        var r = await _ps.RunScriptAsync(@"
param($n)
$ips = Get-VMNetworkAdapter -VMName $n -ErrorAction SilentlyContinue |
       Where-Object { $_.IPAddresses } |
       Select-Object -ExpandProperty IPAddresses
$ipv4 = $ips | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notmatch '^169\.254\.' } | Select-Object -First 1
if ($null -eq $ipv4) { '' } else { $ipv4 }",
            new Dictionary<string, object?> { ["n"] = vmName }, ct);

        var s = r.Output.FirstOrDefault()?.BaseObject?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public async Task<IReadOnlyList<BackupInfo>> ListCheckpointsAsync(string serverId, string vmName, CancellationToken ct = default)
    {
        var r = await _ps.RunScriptAsync(
            "param($n) Get-VMSnapshot -VMName $n | Select-Object Id, Name, CreationTime, SizeOnDisk",
            new Dictionary<string, object?> { ["n"] = vmName }, ct);

        var list = new List<BackupInfo>();
        foreach (var o in r.Output)
        {
            var p = o.Properties;
            var id = p["Id"]?.Value?.ToString() ?? Guid.NewGuid().ToString();
            var name = p["Name"]?.Value?.ToString() ?? "";
            var time = p["CreationTime"]?.Value as DateTime? ?? DateTime.UtcNow;
            var sz = p["SizeOnDisk"]?.Value as long?;
            list.Add(new BackupInfo(id, serverId, new DateTimeOffset(time), name, sz));
        }
        return list;
    }
}
