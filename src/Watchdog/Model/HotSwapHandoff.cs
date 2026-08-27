namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The state the daemon hands to its <b>successor image across a self-re-exec hot-swap</b> (Inc 7 /
/// Option 3). When the daemon <c>execv</c>s its freshly-deployed binary in place — same PID — the
/// kernel inherits every fd that shed <c>O_CLOEXEC</c> (each live game's stdin-FIFO write-fd), so the
/// game never sees stdin EOF. But the managed CLR/AOT image is discarded and a fresh one starts at
/// <c>Main</c>, so the successor cannot read the predecessor's in-memory tables. This blob carries the
/// minimum it needs to ADOPT those inherited fds directly (NOT re-open them — a re-opened FIFO is a
/// different inode the game can't see) and restore each instance's supervision bookkeeping.
/// <para>
/// It travels through the <see cref="EnvVarName"/> environment variable as base64(UTF8(json)) — small,
/// no temp file, and inherited by <c>execv</c> via the libc <c>environ</c> (which
/// <c>Environment.SetEnvironmentVariable</c> updates). What is NOT here is re-derived in the successor
/// from authoritative sources: the spec (kgsm-lib), cgroup liveness (<c>cgroup.events</c>), and the
/// display PID (<c>cgroup.procs</c>) — never fabricated.
/// </para>
/// </summary>
internal sealed class HotSwapHandoff
{
    /// <summary>
    /// The environment variable that carries the base64(UTF8(json)) handoff across the <c>execv</c>.
    /// The successor reads it once at boot, adopts from it, then <b>unsets it</b> so a later plain restart
    /// (no swap) can never re-adopt stale fds. It is an internal IPC channel, not an operator config knob,
    /// so it is deliberately excluded from <c>WatchdogOptions.UnknownConfigVars()</c>'s typo check rather
    /// than listed as a configuration variable.
    /// </summary>
    public const string EnvVarName = "KGSM_WATCHDOG_HOTSWAP_HANDOFF";

    /// <summary>Forward-compat version stamp (same convention as the persisted-state DTOs).</summary>
    public int Version { get; set; } = 1;

    /// <summary>One entry per live instance whose FIFO fd is being carried across the swap.</summary>
    public List<HotSwapEntry> Instances { get; set; } = new();

    /// <summary>
    /// Every instance's live player sessions (<c>PlayerSessionStore</c>), keyed by instance name — the
    /// correlation map that turns independently-matched join and leave log lines into paired presence
    /// events.
    /// <para>
    /// It is here for the same reason the FIFO fds are: it cannot be re-derived. The successor's log tail
    /// primes at EOF on its first attach, so the join lines that established these sessions are behind it
    /// and will never be read again. Most games' leave lines carry only a bare correlation token (an
    /// address, a ZDOID, a userid) and no display name, so a leave arriving against an empty map has
    /// nothing to resolve and, per the presence contract, is skipped rather than guessed — leaving that
    /// player reported as connected until the instance next stops.
    /// </para>
    /// <para>
    /// Deliberately a top-level map rather than a field on <see cref="HotSwapEntry"/>: entries exist only
    /// for instances carrying a live FIFO fd, while sessions are tracked for every instance the ingester
    /// discovers. Hanging them off the entries would silently drop the map for an adopted, cgroup-only
    /// instance — one that has already lost its console and can least afford a second loss.
    /// </para>
    /// </summary>
    public Dictionary<string, PlayerSession[]> PlayerSessions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One instance's hot-swap handoff record: the inherited FIFO fd plus the supervision bookkeeping that
/// has no authoritative source to re-derive from (counters, phase, timing, intent). The fd number is
/// inherited verbatim by <c>execv</c>; the successor confirms it is still open
/// (<c>ReExec.IsValidFd</c>) before adopting it, and falls back to re-opening the on-disk FIFO node
/// (Option 1) for any single entry whose fd unexpectedly did not survive.
/// </summary>
internal sealed class HotSwapEntry
{
    public string Name { get; set; } = "";

    /// <summary>The inherited stdin-FIFO write-fd (O_CLOEXEC cleared just before the exec so it survives).</summary>
    public int FifoFd { get; set; }

    /// <summary>The FIFO node path — used only for the Option-1 re-open fallback if the inherited fd is gone.</summary>
    public string FifoPath { get; set; } = "";

    public int ConsecutiveFailures { get; set; }
    public bool GaveUp { get; set; }

    /// <summary>The <c>SupervisionPhase</c> enum name (string for human-readable + forward-compat parsing).</summary>
    public string Phase { get; set; } = "";

    public DateTime? SpawnedAt { get; set; }

    /// <summary>The measured start of the run being handed over — carried so the successor reports the
    /// game's own age rather than re-dating it to the swap.</summary>
    public DateTime? RunStartedAt { get; set; }

    public DateTime? NextRestartAt { get; set; }
    public string LastReason { get; set; } = "";
    public bool DesiredRunning { get; set; }
}
