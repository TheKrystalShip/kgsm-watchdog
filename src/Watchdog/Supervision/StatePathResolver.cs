namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Where the daemon's persistent state lives, resolved once for every store that keeps a file there —
/// the boot-autostart set (<see cref="DesiredStateStore"/>), the restart counters
/// (<see cref="SupervisionStateStore"/>), the run ledger (<see cref="RunHistoryStore"/>) and the
/// player name index (<see cref="PlayerNameStore"/>).
/// <para>
/// <b>Three sources, in order.</b> An explicit <c>Watchdog__StateFile</c> wins — it names the
/// desired-state file itself, and its directory is where the companions go. Otherwise
/// <c>$STATE_DIRECTORY</c>, which systemd creates before <c>ExecStart</c> and chowns to <c>User=</c>
/// from the unit's <c>StateDirectory=kgsm-watchdog</c>, giving <c>/var/lib/kgsm-watchdog</c> at no
/// privilege. Failing both — a daemon run by hand, outside systemd —
/// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog</c>.
/// </para>
/// <para>
/// <b>Resolved lazily, never at options construction.</b> <c>HOME</c> only becomes the KGSM user's
/// after the bootstrap privilege drop, and every call site runs after that.
/// </para>
/// <para>
/// <b>State from a home-directory layout is carried over on first use of the systemd directory.</b>
/// The autostart set is the only record of which instances come back after a reboot: resolving to a
/// new directory while leaving that file behind would not fail, it would simply start nothing at the
/// next boot and say nothing about it. The carry-over copies before it deletes, and a failure to
/// delete leaves a harmless stale copy rather than risking the live one. An explicit
/// <c>Watchdog__StateFile</c> is exempt — that path is an operator's own choice, not a layout to
/// migrate off.
/// </para>
/// </summary>
internal sealed class StatePathResolver(WatchdogOptions options, ILogger<StatePathResolver> logger)
{
    /// <summary>The boot-autostart set — which instances come back after a restart or reboot.</summary>
    public const string DesiredStateFile = "desired-state.json";

    /// <summary>The restart counters and give-up latch, so they survive any daemon death.</summary>
    public const string SupervisionStateFile = "supervision-state.json";

    /// <summary>How each run of each instance ended.</summary>
    public const string RunHistoryFile = "run-history.json";

    /// <summary>The display name each account id was last seen under, per instance.</summary>
    public const string PlayerNamesFile = "player-names.json";

    /// <summary>
    /// The files carried over from a home-directory layout. The run ledger and the player name index
    /// are absent by design: both are only ever written to the resolved directory, so there is no older
    /// copy of either anywhere.
    /// </summary>
    private static readonly string[] CarriedOver = [DesiredStateFile, SupervisionStateFile];

    /// <summary>The directory systemd created from the unit's <c>StateDirectory=</c>.</summary>
    private const string SystemdSource = "$STATE_DIRECTORY";

    private readonly Lazy<string> _directory = new(
        () => ResolveDirectory(options, logger),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The directory every state file lives in. Created if absent; carry-over runs once, here.</summary>
    public string StateDirectory => _directory.Value;

    /// <summary><paramref name="fileName"/> inside <see cref="StateDirectory"/>.</summary>
    public string PathFor(string fileName) => Path.Combine(StateDirectory, fileName);

    /// <summary>
    /// The desired-state path. Distinct from <c>PathFor(DesiredStateFile)</c> in exactly one case: an
    /// operator who set <c>Watchdog__StateFile</c> named that file, and gets the name they chose.
    /// </summary>
    public string DesiredStatePath =>
        !string.IsNullOrEmpty(options.StateFile) ? options.StateFile : PathFor(DesiredStateFile);

    private static string ResolveDirectory(WatchdogOptions options, ILogger logger)
    {
        string resolved = Choose(options, out string source);

        try
        {
            Directory.CreateDirectory(resolved);
        }
        catch (Exception ex)
        {
            // Nothing to do about it here — each store's own write already degrades to a logged
            // warning, and refusing to boot over a state directory would take the supervised games
            // down with it.
            logger.LogError(ex, "could not create the state directory {Dir}; state will not persist", resolved);
        }

        // Only the systemd-managed directory carries state over. An explicit Watchdog__StateFile is an
        // operator naming a location deliberately, and copying files they did not put there would be a
        // surprise; the home-directory fallback is where the files already are.
        if (source == SystemdSource)
            CarryOverLegacyState(resolved, logger);

        logger.LogInformation("state directory: {Dir} (from {Source})", resolved, source);
        return resolved;
    }

    private static string Choose(WatchdogOptions options, out string source)
    {
        if (!string.IsNullOrEmpty(options.StateFile))
        {
            source = "Watchdog__StateFile";
            return Path.GetDirectoryName(options.StateFile) is { Length: > 0 } dir ? dir : ".";
        }

        // systemd sets STATE_DIRECTORY from StateDirectory=. It is colon-separated when a unit
        // declares several; this one declares a single directory, and the first is ours either way.
        if (Environment.GetEnvironmentVariable("STATE_DIRECTORY") is { Length: > 0 } state)
        {
            string first = state.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? state;
            if (first.Length > 0)
            {
                source = SystemdSource;
                return first;
            }
        }

        source = "XDG data home";
        return LegacyDirectory();
    }

    /// <summary>
    /// The home-directory location a daemon outside systemd resolves to, and where an installation
    /// that predates the unit's <c>StateDirectory=</c> left its files.
    /// </summary>
    internal static string LegacyDirectory()
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/var/lib", ".local", "share");

        return Path.Combine(dataHome, "kgsm-watchdog");
    }

    /// <summary>
    /// Bring <see cref="CarriedOver"/> across from <see cref="LegacyDirectory"/> when the resolved
    /// directory is somewhere else and does not already hold them. A file already at the destination
    /// is never overwritten — it is the newer of the two by definition, since it is the one the
    /// running daemon has been writing.
    /// </summary>
    private static void CarryOverLegacyState(string resolved, ILogger logger)
    {
        string legacy = LegacyDirectory();
        if (string.Equals(Path.TrimEndingDirectorySeparator(legacy),
                          Path.TrimEndingDirectorySeparator(resolved), StringComparison.Ordinal))
            return;

        foreach (string file in CarriedOver)
        {
            string from = Path.Combine(legacy, file);
            string to = Path.Combine(resolved, file);
            try
            {
                if (!File.Exists(from) || File.Exists(to))
                    continue;

                // Copy first, delete second: the destination is a different filesystem often enough
                // (/var vs a home on its own mount) that this cannot be a rename, and a half-done move
                // of the autostart set is the one outcome worth designing against.
                File.Copy(from, to);
                logger.LogInformation("carried state file {File} over from {From}", file, legacy);

                try
                {
                    File.Delete(from);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "copied {File} to {To} but could not remove {From}; the stale copy is unused", file, to, from);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "could not carry {File} over from {From}; if it is the autostart set, instances enabled "
                    + "for boot will not be restored until they are enabled again", file, legacy);
            }
        }
    }
}
