using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace GameServerControl.Agent.Hyperv;

public sealed class PowerShellRunner : IDisposable
{
    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<PowerShellRunner> _logger;

    public PowerShellRunner(ILogger<PowerShellRunner> logger)
    {
        _logger = logger;
        // CreateDefault2 = only Microsoft.PowerShell.Core (skips Diagnostics snap-in,
        // which isn't shipped in the hosted SDK runtime payload).
        var iss = InitialSessionState.CreateDefault2();
        iss.ImportPSModule(new[] { "Hyper-V" });
        _runspace = RunspaceFactory.CreateRunspace(iss);
        _runspace.Open();
    }

    public Task<PsResult> RunScriptAsync(string script, IDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        => RunAsync(ps =>
        {
            ps.AddScript(script);
            if (parameters is not null)
            {
                foreach (var (k, v) in parameters)
                    ps.AddParameter(k, v);
            }
        }, ct);

    public Task<PsResult> RunCommandAsync(string command, IDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        => RunAsync(ps =>
        {
            ps.AddCommand(command);
            if (parameters is not null)
            {
                foreach (var (k, v) in parameters)
                    ps.AddParameter(k, v);
            }
        }, ct);

    private async Task<PsResult> RunAsync(Action<PowerShell> build, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            build(ps);

            var input = new PSDataCollection<PSObject>();
            input.Complete();
            var output = new PSDataCollection<PSObject>();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var iar = ps.BeginInvoke<PSObject, PSObject>(input, output, null,
                ar => {
                    try { ps.EndInvoke(ar); tcs.TrySetResult(true); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }, null);

            using (ct.Register(() => { try { ps.Stop(); } catch { /* ignore */ } }))
            {
                try { await tcs.Task.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PowerShell invocation threw");
                }
            }

            return new PsResult(
                output.ToArray(),
                ps.Streams.Error.Select(e => e.ToString()).ToArray(),
                ps.HadErrors);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try { _runspace.Close(); } catch { /* ignore */ }
        _runspace.Dispose();
        _gate.Dispose();
    }
}

public sealed record PsResult(PSObject[] Output, string[] Errors, bool HadErrors)
{
    public bool Ok => !HadErrors;
    public string ErrorText => string.Join("\n", Errors);
}
