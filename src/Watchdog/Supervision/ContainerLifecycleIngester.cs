using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The watchdog's second <b>container role</b> (a peer of <see cref="PlayerPresenceIngester"/>,
/// tailing a different channel in the SAME bind-mounted <c>/run/kgsm</c> dir): an always-on host-side
/// ingester that turns a container instance's self-reported lifecycle line
/// (<c>instance_started</c>/<c>instance_stopping</c>, kgsm-containers Phase 1) into a
/// <see cref="UpnpService"/> open/close call. It is purely a file reader — it <b>never shells
/// <c>docker</c></b> and never supervises containers (Docker owns their lifecycle); the watchdog's
/// native-only supervision charter is untouched, and native UPnP is already driven by
/// <c>InstanceSupervisor</c> — this ingester only acts on <b>container</b>-runtime instances so the two
/// paths never double-drive the same instance.
/// <para>
/// <b>UPnP-only, no wire events.</b> Unlike <see cref="PlayerPresenceIngester"/>, this ingester does
/// <b>not</b> emit a kgsm wire event for what it reads — the container's own manage.sh already emits
/// the kgsm lifecycle events (Phase 1 lives in kgsm-containers, not here); re-emitting here would be a
/// duplicate. <see cref="UpnpService"/> itself already gates on <c>EnablePortForwarding</c> and expands
/// <c>Ports</c>, and only a <em>confirmed</em> upnpc mapping change is worth anything upstream — this
/// ingester does not need to audit-emit that either (parity with how the container path today has no
/// UPnP audit trail at all; the native <c>instance-upnp-opened/-closed</c> emits stay
/// <c>InstanceSupervisor</c>-only for now).
/// </para>
/// <para>
/// Same discovery/tail shape as <see cref="PlayerPresenceIngester"/>: a
/// <c>&lt;instances&gt;/&lt;blueprint&gt;/&lt;instance&gt;/events/lifecycle.ndjson</c> two-level walk
/// (through the instance→working_dir symlink), one <see cref="EventChannelTail"/> per discovered
/// channel keyed by path (fresh inode ⇒ new container session, matching how the in-image script
/// truncates the file at the top of every <c>_start</c>). Poll-driven, mirroring every other ingester
/// here.
/// </para>
/// <para>
/// A dropped/malformed line, an unresolvable instance name, or a native-runtime instance are all
/// silently skipped (the latter two logged at Debug only — routine, not anomalies). Every UPnP call is
/// wrapped so a failure never crashes the daemon, matching the other ingesters' best-effort posture.
/// </para>
/// </summary>
internal sealed class ContainerLifecycleIngester(
    WatchdogOptions options,
    IInstanceService instances,
    UpnpService upnp,
    IForwardedPortClaims claims,
    ILogger<ContainerLifecycleIngester> logger) : BackgroundService
{
    // One live tail per discovered channel path, keyed by absolute path — mirrors
    // PlayerPresenceIngester._tails (a channel that disappears keeps a cheap idle tail).
    private readonly Dictionary<string, EventChannelTail> _tails = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.ContainerLifecyclePollMs));
        string root = ResolveInstancesDir();
        logger.LogInformation(
            "container lifecycle ingester started; watching {Root} for */*/events/lifecycle.ndjson every {Ms}ms",
            root, interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await IngestOnceAsync(root, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Never let the ingest loop die — it is an additive forwarder; a bad tick must not
                    // take the whole daemon down (the supervision loop is the daemon's reason to live).
                    logger.LogError(ex, "container lifecycle ingest pass threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        logger.LogInformation("container lifecycle ingester stopped");
    }

    /// <summary>
    /// One scan + tail pass: discover channels, read new lines from each, parse and act. <c>internal</c>
    /// (async, unlike the sibling ingesters — UPnP is genuinely I/O-bound) so a test can drive a single
    /// deterministic pass without racing the <see cref="PeriodicTimer"/> loop.
    /// </summary>
    internal async Task IngestOnceAsync(string root, CancellationToken ct = default)
    {
        foreach (string channelPath in DiscoverChannels(root))
        {
            if (!_tails.TryGetValue(channelPath, out var tail))
            {
                tail = new EventChannelTail(channelPath);
                _tails[channelPath] = tail;
            }

            string instanceName = DeriveInstanceName(channelPath);
            foreach (string line in tail.ReadNewLines())
                await HandleLineAsync(instanceName, line, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enumerate <c>&lt;root&gt;/&lt;blueprint&gt;/&lt;instance&gt;/events/lifecycle.ndjson</c> — same
    /// manual two-level walk as <see cref="PlayerPresenceIngester.DiscoverChannels"/>, just a different
    /// filename in the same channel dir. Tolerant of a missing root / races.
    /// </summary>
    internal static IEnumerable<string> DiscoverChannels(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (string blueprintDir in SafeEnumerateDirectories(root))
        {
            foreach (string instanceDir in SafeEnumerateDirectories(blueprintDir))
            {
                string channel = Path.Combine(instanceDir, "events", "lifecycle.ndjson");
                if (File.Exists(channel))
                    yield return channel;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir); // follows the instance symlinks to real dirs
        }
        catch (Exception)
        {
            return []; // vanished / unreadable between ticks — skip this level
        }
    }

    /// <summary>
    /// Derive <c>&lt;instance_name&gt;</c> from a channel path
    /// <c>.../&lt;blueprint&gt;/&lt;instance&gt;/events/lifecycle.ndjson</c> — the dir two levels above
    /// the file. Mirrors <see cref="PlayerPresenceIngester.DeriveInstanceName"/>.
    /// </summary>
    internal static string DeriveInstanceName(string channelPath)
    {
        string? eventsDir = Path.GetDirectoryName(channelPath);   // .../<instance>/events
        string? instanceDir = Path.GetDirectoryName(eventsDir);   // .../<instance>
        return Path.GetFileName(instanceDir) is { Length: > 0 } name ? name : "unknown";
    }

    private async Task HandleLineAsync(string instanceName, string line, CancellationToken ct)
    {
        ContainerLifecycleParser.ParseResult result = ContainerLifecycleParser.Parse(line);
        if (!result.Emit)
        {
            // A blank line is the normal trailing-newline case — don't spam the log for it.
            if (result.DropReason != "blank line")
                logger.LogWarning(
                    "dropping lifecycle line for {Instance} ({Reason}): {Line}",
                    instanceName, result.DropReason, Truncate(line));
            return;
        }

        Instance? instance;
        try
        {
            instance = instances.GetInstanceInfo(instanceName);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "container lifecycle: could not read instance info for {Instance} (skipped)", instanceName);
            return;
        }

        if (instance is null)
        {
            logger.LogDebug("container lifecycle: {Instance} not resolvable yet (skipped)", instanceName);
            return;
        }

        if (instance.Runtime != InstanceRuntime.Container)
        {
            // Native UPnP is already driven by InstanceSupervisor — never double-drive the same
            // instance from here (this channel shouldn't exist for a native instance anyway, but the
            // check is defensive, not load-bearing).
            logger.LogDebug("container lifecycle: {Instance} is not a container instance (skipped)", instanceName);
            return;
        }

        try
        {
            if (result.Type == ContainerLifecycleParser.TypeStarted)
            {
                UpnpOutcome outcome = await upnp.OpenAsync(instance, ct).ConfigureAwait(false);
                logger.LogInformation(
                    "container lifecycle {Type} for {Instance}: UPnP open -> {Outcome}",
                    result.Type, instanceName, outcome);
            }
            else // TypeStopping
            {
                // A container's stop deletes router rows by port like any other, so it asks the same
                // question a native stop does before releasing anything — a port a supervised instance
                // is still running on is not this container's to take down.
                UpnpOutcome outcome = await upnp
                    .CloseAsync(instance, claims.ForwardedPortsHeldByOthers(instanceName), ct)
                    .ConfigureAwait(false);
                logger.LogInformation(
                    "container lifecycle {Type} for {Instance}: UPnP close -> {Outcome}",
                    result.Type, instanceName, outcome);
            }
        }
        catch (Exception ex)
        {
            // Best-effort, like every other UPnP call site — a failed open/close is logged and
            // swallowed, never crashes the ingester or the daemon.
            logger.LogWarning(ex, "UPnP action for {Instance} on lifecycle {Type} threw", instanceName, result.Type);
        }
    }

    /// <summary>
    /// The instances dir to watch — same resolution as <see cref="PlayerPresenceIngester.ResolveInstancesDir"/>
    /// (the explicit <c>KGSM_WATCHDOG_INSTANCES_DIR</c> if set, else the dropped KGSM user's XDG data dir).
    /// </summary>
    internal string ResolveInstancesDir()
    {
        if (!string.IsNullOrEmpty(options.InstancesDir))
            return options.InstancesDir;

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/var/lib", ".local", "share");

        return Path.Combine(dataHome, "kgsm", "instances");
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "…";
}
