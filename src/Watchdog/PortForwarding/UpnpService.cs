using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Model;

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
        => ApplyAsync(instance, open: true, portsOverride: null, ct);

    /// <summary>
    /// Open UPnP mappings for an explicit subset of the instance's ports rather than all of them — what
    /// the reconcile sweep re-asserts when the router is found to have dropped some but not all of a
    /// running instance's forwards. Still gated on <c>enable_port_forwarding</c>: config is the authority
    /// on whether an instance wants forwarding at all, so a gated-off instance returns
    /// <see cref="UpnpOutcome.Skipped"/> here too. A null/empty subset falls back to the instance's own
    /// ports.
    /// </summary>
    public Task<UpnpOutcome> OpenAsync(
        Instance instance, IReadOnlyList<PortMapping>? portsOverride, CancellationToken ct = default)
        => ApplyAsync(instance, open: true, portsOverride, ct);

    /// <summary>
    /// Close an explicit subset of the instance's UPnP mappings. Used to undo a re-assert that landed
    /// just after the instance stopped — the sweep re-checks liveness after the open, and a forward for
    /// something no longer running is exactly the state the open/close lifetime exists to prevent.
    /// </summary>
    public Task<UpnpOutcome> CloseAsync(
        Instance instance, IReadOnlyList<PortMapping>? portsOverride, CancellationToken ct = default)
        => ApplyAsync(instance, open: false, portsOverride, ct);

    /// <summary>
    /// Close the instance's UPnP mappings (no-op unless <c>enable_port_forwarding</c>). Returns the
    /// <see cref="UpnpOutcome"/> so the caller emits an audit event only on a confirmed removal.
    /// </summary>
    public Task<UpnpOutcome> CloseAsync(Instance instance, CancellationToken ct = default)
        => ApplyAsync(instance, open: false, portsOverride: null, ct);

    private async Task<UpnpOutcome> ApplyAsync(
        Instance instance, bool open, IReadOnlyList<PortMapping>? portsOverride, CancellationToken ct)
    {
        string action = open ? "open" : "close";

        // Per-instance gate — parity with the bash _enable_upnp guard. No global toggle (§5·3).
        // Disabled → nothing happens → Skipped (no event). This is the default (inert) path, and it
        // holds uniformly for an external explicit-port open too: config is the authority on whether an
        // instance wants port-forwarding, so an external caller never force-forwards a gated-off server.
        if (!instance.EnablePortForwarding)
            return UpnpOutcome.Skipped;

        // Expand the canonical structured ports (kgsm-lib already parsed + validated them off the
        // `instances info --json` surface) into the individual external ports upnpc opens one at a
        // time. An explicit override (external control) takes precedence over the instance's own ports;
        // ranges are unrolled here; a no-ports instance/override is a clean no-op.
        IReadOnlyList<PortMapping> source = portsOverride is { Count: > 0 } ? portsOverride : instance.Ports;
        List<(int Port, string Protocol)> ports = [.. source.Expand()];
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
    /// List the UPnP mappings the local IGD currently holds for <paramref name="instanceName"/> — the
    /// rows whose description equals the instance name (the ownership tag). Queries the router directly
    /// (<c>upnpc -l</c>): the IGD lease is the source of truth (it outlives a process death), so the
    /// watchdog keeps NO in-memory UPnP registry that could drift.
    /// <para>
    /// Honest by construction: a missing <c>upnpc</c>, no IGD on the network, a timeout, or any output we
    /// cannot confirm came from a real listing yields <see cref="UpnpListResult.State"/> = <c>"unavailable"</c>
    /// — never an empty <c>"queried"</c> list (that would fabricate the absence of forwards). Only output
    /// carrying the redirection table is reported as <c>"queried"</c>, and then an empty filtered set is a
    /// genuine "the router owns none for this instance".
    /// </para>
    /// </summary>
    public async Task<UpnpListResult> ListAsync(string instanceName, CancellationToken ct = default)
    {
        UpnpcRun run = await RunUpnpcRawAsync(["-l"], ct).ConfigureAwait(false);
        if (!run.Launched)
            logger.LogWarning("UPnP list for {Instance}: could not launch upnpc (is miniupnpc installed?)", instanceName);
        else if (run.TimedOut)
            logger.LogWarning("UPnP list for {Instance}: upnpc timed out after {Timeout}s", instanceName, UpnpTimeout.TotalSeconds);

        return ParseListOutput(instanceName, run.Launched, run.TimedOut, run.Stdout);
    }

    /// <summary>
    /// Read the IGD's <b>whole</b> redirection table in one shot, tagged by owner. The reconcile sweep
    /// asks about every running instance at once, and <c>upnpc -l</c> already returns the full table, so
    /// one invocation answers for all of them — spawning it per instance would multiply a ~3s round trip
    /// by the instance count for no extra information.
    /// <para>
    /// Honest the same way <see cref="ListAsync"/> is: an unreachable router yields
    /// <see cref="UpnpTable.Reached"/> = false, which a caller must treat as "I do not know what is
    /// mapped" — never as an empty table. Acting on the difference against a table we could not read
    /// would re-open forwards that are already there, or worse, read a router outage as mass expiry.
    /// </para>
    /// </summary>
    public async Task<UpnpTable> ListAllAsync(CancellationToken ct = default)
    {
        UpnpcRun run = await RunUpnpcRawAsync(["-l"], ct).ConfigureAwait(false);
        if (!run.Launched)
            logger.LogWarning("UPnP sweep: could not launch upnpc (is miniupnpc installed?)");
        else if (run.TimedOut)
            logger.LogWarning("UPnP sweep: upnpc timed out after {Timeout}s", UpnpTimeout.TotalSeconds);

        return ParseTable(run.Launched, run.TimedOut, run.Stdout);
    }

    /// <summary>
    /// Pure parse of <c>upnpc -l</c> output into the owned-by-instance mappings. Kept static + pure so
    /// the tolerant, human-format parsing is unit-tested apart from the shell-out. Filters
    /// <see cref="ParseTable"/> down to the rows this instance tagged as its own.
    /// </summary>
    internal static UpnpListResult ParseListOutput(string instanceName, bool launched, bool timedOut, string stdout)
    {
        UpnpTable table = ParseTable(launched, timedOut, stdout);
        if (!table.Reached)
            return new UpnpListResult(instanceName, "unavailable", []);

        return new UpnpListResult(instanceName, "queried", [.. table.Mappings.Where(
            m => string.Equals(m.Description, instanceName, StringComparison.Ordinal))]);
    }

    /// <summary>
    /// Pure parse of <c>upnpc -l</c> output into every redirection row the IGD reports, with no owner
    /// filtering. Confirms the output is a real listing (carries upnpc's "Found valid IGD" marker AND no
    /// "No IGD" banner) before reporting <see cref="UpnpTable.Reached"/>; anything else is an honest
    /// not-reached, never an empty table that would fabricate the absence of forwards.
    /// </summary>
    internal static UpnpTable ParseTable(bool launched, bool timedOut, string stdout)
    {
        if (!launched || timedOut)
            return new UpnpTable(false, []);

        // upnpc prints "No IGD UPnP Device found on the network !" AND exits 0 when no router answers —
        // so the exit code is not a reliable signal; the text is. "Found valid IGD" is upnpc's definitive
        // "the router answered" marker (it precedes the redirection table on every -l against a real IGD,
        // regardless of miniupnpc version). Require it, and the absence of the no-IGD banner.
        bool noIgd = stdout.Contains("No IGD", StringComparison.OrdinalIgnoreCase)
                     || stdout.Contains("No valid UPNP", StringComparison.OrdinalIgnoreCase);
        bool reached = stdout.Contains("Found valid IGD", StringComparison.OrdinalIgnoreCase);
        if (noIgd || !reached)
            return new UpnpTable(false, []);

        var mappings = new List<UpnpMapping>();
        foreach (string line in stdout.Split('\n'))
        {
            if (TryParseMappingRow(line, out UpnpMapping? mapping))
                mappings.Add(mapping);
        }

        return new UpnpTable(true, mappings);
    }

    /// <summary>
    /// Tolerantly parse one <c>upnpc -l</c> redirection row into a <see cref="UpnpMapping"/>. The row
    /// shape (miniupnpc) is:
    /// <c> &lt;i&gt; &lt;PROTO&gt; &lt;exPort&gt;-&gt;&lt;inClient&gt;:&lt;inPort&gt; '&lt;desc&gt;' '&lt;rHost&gt;' &lt;lease&gt;</c>,
    /// e.g. <c> 0 UDP 34197-&gt;192.168.1.100:34197 'factorio-test' '' 0</c>. A line that is not a
    /// numbered redirection row (headers, banners, blanks) returns <c>false</c>. The protocol is
    /// lower-cased to the ecosystem's <c>tcp</c>/<c>udp</c> convention.
    /// </summary>
    internal static bool TryParseMappingRow(string line, out UpnpMapping mapping)
    {
        mapping = null!;
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        // Split the leading whitespace-delimited fields: index, protocol, and the exPort->client:port
        // token. The quoted description/remote-host that follow may not split cleanly, so we take the
        // description from the first single-quoted region rather than from token position.
        string[] head = trimmed.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        if (head.Length < 3)
            return false;

        if (!int.TryParse(head[0], out _))                 // first field must be the row index
            return false;

        string proto = head[1];
        if (!proto.Equals("TCP", StringComparison.OrdinalIgnoreCase)
            && !proto.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            return false;

        // head[2] = "<exPort>-><inClient>:<inPort>"
        string map = head[2];
        int arrow = map.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
            return false;
        if (!int.TryParse(map[..arrow], out int externalPort))
            return false;

        string clientPort = map[(arrow + 2)..];
        int colon = clientPort.LastIndexOf(':');
        if (colon < 0)
            return false;
        string internalClient = clientPort[..colon];
        if (!int.TryParse(clientPort[(colon + 1)..], out int internalPort))
            return false;

        // Description = the first single-quoted region on the line (the -e tag we set on open).
        string description = "";
        int q1 = trimmed.IndexOf('\'');
        if (q1 >= 0)
        {
            int q2 = trimmed.IndexOf('\'', q1 + 1);
            if (q2 > q1)
                description = trimmed[(q1 + 1)..q2];
        }

        mapping = new UpnpMapping(
            externalPort, proto.ToLowerInvariant(), internalPort, internalClient, description);
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

    private async Task<UpnpOutcome> RunUpnpcAsync(
        IReadOnlyList<string> args, bool open, string instanceName, CancellationToken ct)
    {
        string action = open ? "open" : "close";

        UpnpcRun run = await RunUpnpcRawAsync(args, ct).ConfigureAwait(false);

        if (!run.Launched)
        {
            // upnpc missing (miniupnpc not installed) or otherwise unspawnable. Best-effort, exactly
            // like the bash path logging and continuing — UPnP is an opt-in convenience, never a hard
            // supervision dependency. Nothing was mapped → Failed (no event).
            logger.LogWarning(
                "UPnP {Action} for {Instance}: could not launch upnpc (is miniupnpc installed?)",
                action, instanceName);
            return UpnpOutcome.Failed;
        }

        if (run.TimedOut)
        {
            logger.LogWarning(
                "UPnP {Action} for {Instance}: upnpc timed out after {Timeout}s; killed (slow/absent router?)",
                action, instanceName, UpnpTimeout.TotalSeconds);
            return UpnpOutcome.Failed;
        }

        if (run.ExitCode == 0)
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
            string detail = !string.IsNullOrEmpty(run.Stderr) ? run.Stderr : run.Stdout;
            logger.LogWarning(
                "UPnP open for {Instance}: upnpc exited {Code}, continuing without port forwarding{Detail}",
                instanceName, run.ExitCode,
                string.IsNullOrEmpty(detail) ? "" : $" — {detail}");
            return UpnpOutcome.Failed;
        }

        // Close fires unconditionally (no bash-style state-file guard under supervision), so
        // a non-zero on close is usually just "nothing to delete" (no mapping was opened, or
        // the lease expired — upnpc returns IGD error 714). Nothing changed → Skipped (no
        // event — never fabricate a close that removed nothing). Information, so stops stay quiet.
        logger.LogInformation(
            "UPnP close for {Instance}: upnpc exited {Code} (likely no active mapping — harmless)",
            instanceName, run.ExitCode);
        return UpnpOutcome.Skipped;
    }

    /// <summary>The mechanical result of one time-boxed <c>upnpc</c> invocation, before any semantic
    /// interpretation — so the open/close outcome mapping and the list parse share the exact same
    /// spawn/timeout/pipe-drain plumbing.</summary>
    private readonly record struct UpnpcRun(bool Launched, bool TimedOut, int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Spawn <c>upnpc</c> with the given argv, time-boxed to <see cref="UpnpTimeout"/>, draining both
    /// pipes concurrently with the wait so a chatty child can't deadlock. Returns the raw exit/output —
    /// never throws for a normal upnpc failure (a missing binary → <c>Launched=false</c>; a timeout →
    /// <c>TimedOut=true</c> with the child killed). Shells out as the daemon's unprivileged uid.
    /// </summary>
    private static async Task<UpnpcRun> RunUpnpcRawAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
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
        catch
        {
            return new UpnpcRun(Launched: false, TimedOut: false, ExitCode: -1, Stdout: "", Stderr: "");
        }

        using (proc)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(UpnpTimeout);

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                string stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
                string stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                return new UpnpcRun(Launched: true, TimedOut: false, proc.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }

                // Observe the pipe reads so a post-kill faulted read can't surface as an
                // UnobservedTaskException (cancellation usually lands them Canceled, but a broken-pipe
                // teardown can fault them — swallow either way; we've already handled the timeout).
                await ObserveQuietly(stdoutTask).ConfigureAwait(false);
                await ObserveQuietly(stderrTask).ConfigureAwait(false);
                return new UpnpcRun(Launched: true, TimedOut: true, ExitCode: -1, Stdout: "", Stderr: "");
            }
        }
    }

    private static async Task ObserveQuietly(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* timeout / teardown — already accounted for */ }
    }
}
