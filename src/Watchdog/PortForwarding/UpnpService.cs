using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

/// <summary>
/// The outcome of a UPnP open/close attempt, so the supervisor can emit an audit event ONLY on a
/// confirmed transition (never a fabricated one).
/// <list type="bullet">
/// <item><see cref="Skipped"/> — gated off (forwarding disabled or no ports), or a harmless close of
/// a mapping that did not exist (upnpc IGD error 714) — nothing changed, no event.</item>
/// <item><see cref="Applied"/> — upnpc confirmed the mapping change (exited 0) — emit the event.</item>
/// <item><see cref="Failed"/> — the operator asked for it and upnpc could not deliver (missing binary,
/// non-zero on open, or timeout) — logged, no event.</item>
/// </list>
/// </summary>
internal enum UpnpOutcome { Skipped, Applied, Failed }

/// <summary>
/// Opens / closes a native instance's UPnP port mappings on the local IGD by shelling
/// <c>upnpc</c> (miniupnpc) — the same backend the KGSM management script used. The watchdog owns
/// this because UPnP is <b>process-lifetime</b> state: the mapping must open on a fresh bring-up,
/// survive a crash-restart (router leases outlive a process death), and close on an intended stop —
/// and only the supervisor observes those transitions precisely. Under watchdog supervision the
/// management script's embedded <c>_enable_upnp</c>/<c>_disable_upnp</c> never fire (the daemon forks
/// the raw exe, not the script), so this is a correctness fix for a silent regression, not new scope.
/// <para>
/// Every operation is <b>best-effort and time-boxed</b>: a slow or absent router, or a missing
/// <c>upnpc</c>, must never stall supervision or fail a start/stop. Failures are logged and swallowed,
/// matching the management script's "continuing without port forwarding". Shells out as the daemon's
/// (unprivileged) uid — UPnP needs no root. AOT-safe: <see cref="Process"/> only, no reflection.
/// </para>
/// </summary>
internal sealed class UpnpService(ILogger<UpnpService> logger)
{
    // upnpc must answer (or fail) within this budget. A slow/absent router or a hung SOAP call must
    // never block the reconcile/start path — we always fire this off the supervisor thread, but the
    // cap is the second line of defence (and bounds the work the thread-pool task holds).
    private static readonly TimeSpan UpnpTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Open the instance's UPnP mappings (no-op unless <c>enable_port_forwarding</c>). Returns the
    /// <see cref="UpnpOutcome"/> so the caller emits an audit event only on a confirmed mapping.
    /// </summary>
    public Task<UpnpOutcome> OpenAsync(Instance instance, CancellationToken ct = default)
        => ApplyAsync(instance, open: true, ct);

    /// <summary>
    /// Close the instance's UPnP mappings (no-op unless <c>enable_port_forwarding</c>). Returns the
    /// <see cref="UpnpOutcome"/> so the caller emits an audit event only on a confirmed removal.
    /// </summary>
    public Task<UpnpOutcome> CloseAsync(Instance instance, CancellationToken ct = default)
        => ApplyAsync(instance, open: false, ct);

    private async Task<UpnpOutcome> ApplyAsync(Instance instance, bool open, CancellationToken ct)
    {
        string action = open ? "open" : "close";

        // Per-instance gate — parity with the bash _enable_upnp guard. No global toggle (§5·3).
        // Disabled → nothing happens → Skipped (no event). This is the default (inert) path.
        if (!instance.EnablePortForwarding)
            return UpnpOutcome.Skipped;

        // Expand the canonical structured ports (kgsm-lib already parsed + validated them off the
        // `instances info --json` surface) into the individual external ports upnpc opens one at a
        // time. Ranges are unrolled here; a no-ports instance is a clean no-op.
        List<(int Port, string Protocol)> ports = [.. instance.Ports.Expand()];
        if (ports.Count == 0)
        {
            logger.LogInformation(
                "UPnP {Action} skipped for {Instance}: no ports configured", action, instance.Name);
            return UpnpOutcome.Skipped;
        }

        return await RunUpnpcAsync(BuildUpnpcArgs(open, instance.Name, ports), open, instance.Name, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Build the exact <c>upnpc</c> argv, mirroring the management script
    /// (<c>manage.native.d/09-network.sh</c>) flag-for-flag — the bash is the contract:
    /// <list type="bullet">
    /// <item>open: <c>upnpc -e &lt;name&gt; -r &lt;port&gt; &lt;proto&gt; …</c></item>
    /// <item>close: <c>upnpc -f &lt;port&gt; &lt;proto&gt; …</c></item>
    /// </list>
    /// Each token is a separate arg (no shell, no quoting games) — <see cref="ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    internal static List<string> BuildUpnpcArgs(
        bool open, string instanceName, IReadOnlyList<(int Port, string Proto)> ports)
    {
        var args = new List<string>(3 + ports.Count * 2);
        if (open)
        {
            args.Add("-e");
            args.Add(instanceName);
            args.Add("-r");
        }
        else
        {
            args.Add("-f");
        }

        foreach (var (port, proto) in ports)
        {
            args.Add(port.ToString());
            args.Add(proto);
        }

        return args;
    }

    private async Task<UpnpOutcome> RunUpnpcAsync(
        IReadOnlyList<string> args, bool open, string instanceName, CancellationToken ct)
    {
        string action = open ? "open" : "close";

        var psi = new ProcessStartInfo
        {
            FileName = "upnpc",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            // upnpc missing (miniupnpc not installed) or otherwise unspawnable. Best-effort, exactly
            // like the bash path logging and continuing — UPnP is an opt-in convenience, never a hard
            // supervision dependency. Nothing was mapped → Failed (no event).
            logger.LogWarning(ex,
                "UPnP {Action} for {Instance}: could not launch upnpc (is miniupnpc installed?)",
                action, instanceName);
            return UpnpOutcome.Failed;
        }

        using (proc)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(UpnpTimeout);

            // Drain both pipes concurrently with the wait so a chatty upnpc can't fill a pipe buffer
            // and deadlock WaitForExit. Declared out here so the timeout path can still observe them.
            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                string stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
                string stderr = (await stderrTask.ConfigureAwait(false)).Trim();

                if (proc.ExitCode == 0)
                {
                    // A confirmed mapping change — the ONLY path that emits an audit event upstream.
                    // Note: all ports go in one upnpc call, so exit 0 means the call succeeded, not a
                    // per-port confirmation; the event claims the full requested set (best-effort).
                    logger.LogInformation("UPnP {Action} for {Instance}: ok", action, instanceName);
                    return UpnpOutcome.Applied;
                }

                if (open)
                {
                    // Open failing is real signal: the operator asked for forwarding and didn't get it.
                    string detail = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                    logger.LogWarning(
                        "UPnP open for {Instance}: upnpc exited {Code}, continuing without port forwarding{Detail}",
                        instanceName, proc.ExitCode,
                        string.IsNullOrEmpty(detail) ? "" : $" — {detail}");
                    return UpnpOutcome.Failed;
                }

                // Close fires unconditionally (no bash-style state-file guard under supervision), so
                // a non-zero on close is usually just "nothing to delete" (no mapping was opened, or
                // the lease expired — upnpc returns IGD error 714). Nothing changed → Skipped (no
                // event — never fabricate a close that removed nothing). Information, so stops stay quiet.
                logger.LogInformation(
                    "UPnP close for {Instance}: upnpc exited {Code} (likely no active mapping — harmless)",
                    instanceName, proc.ExitCode);
                return UpnpOutcome.Skipped;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "UPnP {Action} for {Instance}: upnpc timed out after {Timeout}s; killed (slow/absent router?)",
                    action, instanceName, UpnpTimeout.TotalSeconds);
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

                // Observe the pipe reads so a post-kill faulted read can't surface as an
                // UnobservedTaskException (cancellation usually lands them Canceled, but a broken-pipe
                // teardown can fault them — swallow either way; we've already handled the timeout).
                await ObserveQuietly(stdoutTask).ConfigureAwait(false);
                await ObserveQuietly(stderrTask).ConfigureAwait(false);
                return UpnpOutcome.Failed;
            }
        }
    }

    private static async Task ObserveQuietly(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* timeout / teardown — already accounted for */ }
    }
}
