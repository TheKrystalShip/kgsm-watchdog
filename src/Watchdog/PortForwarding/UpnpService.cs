using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

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

    /// <summary>Open the instance's UPnP mappings (no-op unless <c>enable_port_forwarding</c>).</summary>
    public Task OpenAsync(Instance instance, CancellationToken ct = default)
        => ApplyAsync(instance, open: true, ct);

    /// <summary>Close the instance's UPnP mappings (no-op unless <c>enable_port_forwarding</c>).</summary>
    public Task CloseAsync(Instance instance, CancellationToken ct = default)
        => ApplyAsync(instance, open: false, ct);

    private async Task ApplyAsync(Instance instance, bool open, CancellationToken ct)
    {
        string action = open ? "open" : "close";

        // Per-instance gate — parity with the bash _enable_upnp guard. No global toggle (§5·3).
        if (!instance.EnablePortForwarding)
            return;

        if (!TryParsePorts(instance.UpnpPorts, out var ports, out string? error))
        {
            logger.LogWarning(
                "UPnP {Action} skipped for {Instance}: malformed upnp_ports {Raw} ({Error})",
                action, instance.Name, instance.UpnpPorts, error);
            return;
        }

        if (ports.Count == 0)
        {
            logger.LogInformation(
                "UPnP {Action} skipped for {Instance}: no ports configured", action, instance.Name);
            return;
        }

        await RunUpnpcAsync(BuildUpnpcArgs(open, instance.Name, ports), open, instance.Name, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parse the stored <c>upnp_ports</c> value into validated <c>(port, proto)</c> pairs. KGSM emits
    /// it as a bash array literal, verbatim — e.g. <c>"(34197 tcp 34197 udp)"</c> (parens included,
    /// alternating port/proto, whitespace-separated) — so we strip a single surrounding <c>( )</c> and
    /// whitespace-split. Defensive even on our own data (§5·1): a malformed shape (odd token count,
    /// out-of-range port, a proto that isn't tcp/udp) is rejected as a <b>batch</b> — <paramref name="error"/>
    /// is set and <paramref name="ports"/> left empty — rather than opening a guessed partial set. An
    /// empty or parens-only value is a clean no-op (returns true, no error). Returns false only on a
    /// genuinely malformed value.
    /// </summary>
    internal static bool TryParsePorts(
        string? raw, out List<(int Port, string Proto)> ports, out string? error)
    {
        ports = [];
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
            return true; // nothing configured — clean no-op

        // Strip a single surrounding ( ) if present (the bash array literal form).
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
            s = s[1..^1];

        string[] tokens = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return true; // "()" or whitespace — clean no-op

        if (tokens.Length % 2 != 0)
        {
            error = $"odd token count ({tokens.Length}); expected alternating 'port proto' pairs";
            return false;
        }

        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (!int.TryParse(tokens[i], out int port) || port < 1 || port > 65535)
            {
                ports.Clear();
                error = $"port out of range or non-numeric: '{tokens[i]}'";
                return false;
            }

            string proto = tokens[i + 1].ToLowerInvariant();
            if (proto is not ("tcp" or "udp"))
            {
                ports.Clear();
                error = $"protocol not tcp/udp: '{tokens[i + 1]}'";
                return false;
            }

            ports.Add((port, proto));
        }

        return true;
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

    private async Task RunUpnpcAsync(
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
            // supervision dependency.
            logger.LogWarning(ex,
                "UPnP {Action} for {Instance}: could not launch upnpc (is miniupnpc installed?)",
                action, instanceName);
            return;
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
                    logger.LogInformation("UPnP {Action} for {Instance}: ok", action, instanceName);
                }
                else if (open)
                {
                    // Open failing is real signal: the operator asked for forwarding and didn't get it.
                    string detail = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                    logger.LogWarning(
                        "UPnP open for {Instance}: upnpc exited {Code}, continuing without port forwarding{Detail}",
                        instanceName, proc.ExitCode,
                        string.IsNullOrEmpty(detail) ? "" : $" — {detail}");
                }
                else
                {
                    // Close fires unconditionally (no bash-style state-file guard under supervision), so
                    // a non-zero on close is usually just "nothing to delete" (no mapping was opened, or
                    // the lease expired — upnpc returns IGD error 714). End-state is correct either way;
                    // this is routine, not a problem — Information, not Warning, so stops stay quiet.
                    logger.LogInformation(
                        "UPnP close for {Instance}: upnpc exited {Code} (likely no active mapping — harmless)",
                        instanceName, proc.ExitCode);
                }
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
            }
        }
    }

    private static async Task ObserveQuietly(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* timeout / teardown — already accounted for */ }
    }
}
