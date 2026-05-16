using System.Management.Automation;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Hyperv;

public sealed class GuestCredential
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class GuestProcessService
{
    private readonly PowerShellRunner _ps;
    private readonly ILogger<GuestProcessService> _logger;

    public GuestProcessService(PowerShellRunner ps, ILogger<GuestProcessService> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    private static string ToPSEscaped(string s) => "'" + s.Replace("'", "''") + "'";

    private static string BuildCommandLine(string exePath, string[] args)
    {
        var parts = new List<string> { "\"" + exePath + "\"" };
        foreach (var a in args)
        {
            if (a.Contains(' ') || a.Contains('"'))
                parts.Add("\"" + a.Replace("\"", "\\\"") + "\"");
            else
                parts.Add(a);
        }
        return string.Join(" ", parts);
    }

    public async Task<(bool Ok, int? Pid, string Error)> StartProcessAsync(
        string vmName, GuestCredential cred, string exePath, string[] args, string workingDir, CancellationToken ct = default)
    {
        var cmdLine = BuildCommandLine(exePath, args);
        var script = @"
param($VMName, $User, $Pass, $CmdLine, $WorkDir)
$sec = ConvertTo-SecureString $Pass -AsPlainText -Force
$c = New-Object System.Management.Automation.PSCredential($User, $sec)
$res = Invoke-Command -VMName $VMName -Credential $c -ScriptBlock {
    param($cmd, $wd)
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = $wd }
    [pscustomobject]@{ ReturnValue = $r.ReturnValue; ProcessId = $r.ProcessId }
} -ArgumentList $CmdLine, $WorkDir
$res
";
        var r = await _ps.RunScriptAsync(script, new Dictionary<string, object?>
        {
            ["VMName"] = vmName,
            ["User"] = cred.Username,
            ["Pass"] = cred.Password,
            ["CmdLine"] = cmdLine,
            ["WorkDir"] = workingDir
        }, ct);

        if (!r.Ok || r.Output.Length == 0)
        {
            return (false, null, r.ErrorText);
        }

        var o = r.Output[0].Properties;
        var rv = (int?)(o["ReturnValue"]?.Value as int?) ?? Convert.ToInt32(o["ReturnValue"]?.Value ?? 1);
        var pid = (int?)(o["ProcessId"]?.Value as int?) ?? Convert.ToInt32(o["ProcessId"]?.Value ?? 0);
        if (rv != 0)
            return (false, null, $"Win32_Process.Create returned {rv}");
        return (true, pid == 0 ? null : pid, "");
    }

    public async Task<(bool Ok, int? Pid)> FindProcessAsync(
        string vmName, GuestCredential cred, string exePath, CancellationToken ct = default)
    {
        var leaf = System.IO.Path.GetFileNameWithoutExtension(exePath);
        var script = @"
param($VMName, $User, $Pass, $Name)
$sec = ConvertTo-SecureString $Pass -AsPlainText -Force
$c = New-Object System.Management.Automation.PSCredential($User, $sec)
$res = Invoke-Command -VMName $VMName -Credential $c -ScriptBlock {
    param($n)
    $p = Get-Process -Name $n -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $p) { $null } else { $p.Id }
} -ArgumentList $Name
$res
";
        var r = await _ps.RunScriptAsync(script, new Dictionary<string, object?>
        {
            ["VMName"] = vmName,
            ["User"] = cred.Username,
            ["Pass"] = cred.Password,
            ["Name"] = leaf
        }, ct);

        if (!r.Ok || r.Output.Length == 0 || r.Output[0]?.BaseObject is null)
            return (true, null);

        if (int.TryParse(r.Output[0].BaseObject.ToString(), out var pid))
            return (true, pid);
        return (true, null);
    }

    public async Task<bool> StopProcessAsync(
        string vmName, GuestCredential cred, string exePath, int? pid, bool force, CancellationToken ct = default)
    {
        var leaf = System.IO.Path.GetFileNameWithoutExtension(exePath);
        var script = @"
param($VMName, $User, $Pass, $Name, $Pid, $Force)
$sec = ConvertTo-SecureString $Pass -AsPlainText -Force
$c = New-Object System.Management.Automation.PSCredential($User, $sec)
Invoke-Command -VMName $VMName -Credential $c -ScriptBlock {
    param($n, $pidArg, $force)
    if ($pidArg -gt 0) {
        $p = Get-Process -Id $pidArg -ErrorAction SilentlyContinue
    } else {
        $p = Get-Process -Name $n -ErrorAction SilentlyContinue
    }
    if ($null -eq $p) { return 'no-process' }
    if (-not $force) {
        foreach ($proc in @($p)) {
            try { $null = $proc.CloseMainWindow() } catch { }
        }
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            $still = Get-Process -Id ($p | Select-Object -ExpandProperty Id) -ErrorAction SilentlyContinue
            if ($null -eq $still) { return 'graceful' }
            Start-Sleep -Milliseconds 500
        }
    }
    $p | Stop-Process -Force -ErrorAction SilentlyContinue
    return 'killed'
} -ArgumentList $Name, $Pid, $Force
";
        var r = await _ps.RunScriptAsync(script, new Dictionary<string, object?>
        {
            ["VMName"] = vmName,
            ["User"] = cred.Username,
            ["Pass"] = cred.Password,
            ["Name"] = leaf,
            ["Pid"] = pid ?? 0,
            ["Force"] = force
        }, ct);

        if (!r.Ok) _logger.LogError("StopProcess in {Vm} failed: {Err}", vmName, r.ErrorText);
        return r.Ok;
    }

    public async Task<(bool Ok, string Output)> RunCommandInGuestAsync(
        string vmName, GuestCredential cred, string commandLine, string workingDir, TimeSpan timeout, CancellationToken ct = default)
    {
        var script = @"
param($VMName, $User, $Pass, $Cmd, $Wd, $TimeoutSec)
$sec = ConvertTo-SecureString $Pass -AsPlainText -Force
$c = New-Object System.Management.Automation.PSCredential($User, $sec)
$job = Invoke-Command -VMName $VMName -Credential $c -AsJob -ScriptBlock {
    param($cmd, $wd)
    Set-Location -LiteralPath $wd
    $out = & cmd /c $cmd 2>&1
    ($out | Out-String)
} -ArgumentList $Cmd, $Wd
$null = Wait-Job $job -Timeout $TimeoutSec
if ($job.State -ne 'Completed') {
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    'TIMEOUT'
} else {
    Receive-Job $job
    Remove-Job $job -ErrorAction SilentlyContinue
}
";
        var r = await _ps.RunScriptAsync(script, new Dictionary<string, object?>
        {
            ["VMName"] = vmName,
            ["User"] = cred.Username,
            ["Pass"] = cred.Password,
            ["Cmd"] = commandLine,
            ["Wd"] = workingDir,
            ["TimeoutSec"] = (int)timeout.TotalSeconds
        }, ct);

        var text = string.Join("\n", r.Output.Select(o => o?.BaseObject?.ToString() ?? ""));
        return (r.Ok, text);
    }
}
