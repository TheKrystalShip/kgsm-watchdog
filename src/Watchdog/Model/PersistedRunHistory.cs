namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The on-disk shape of the <b>run ledger</b> (written by <c>RunHistoryStore</c> to
/// <c>run-history.json</c>, beside <c>desired-state.json</c> and <c>supervision-state.json</c>): how
/// each run of each instance ended.
/// <para>
/// It exists to bind a fact the daemon already knows — this instance crashed, with this exit code —
/// to the specific console file that holds the output of the run that crashed. The supervisor is the
/// only thing on the host that observes an exit and can classify it against operator intent, and
/// nothing else can tell a crash from a deliberate stop after the fact. Without the ledger a consumer
/// can only guess which stretch of console belongs to a crash by comparing timestamps.
/// </para>
/// <para>
/// <b>It is a join key, not a second authority.</b> Whether a crash happened is the
/// <c>instance-crashed</c> event's answer and stays so; this only says which file to read.
/// </para>
/// </summary>
internal sealed class PersistedRunHistory
{
    public int Version { get; set; } = 1;

    /// <summary>Keyed by instance name → its runs, newest first, capped by <c>RunHistoryStore</c>.</summary>
    public Dictionary<string, List<RunRecord>> Instances { get; set; } = new();
}

/// <summary>
/// One finished run of one instance: when it ended, and how.
/// </summary>
/// <param name="EndedAt">
/// <b>The join key.</b> Read from the console file's last-write time at the moment the run was
/// concluded — the last line the process printed — and never from the wall clock, which is a
/// different quantity (the supervisor notices an empty cgroup on its next tick, seconds later). Log
/// rotation moves that file with <c>rename(2)</c>, which leaves mtime untouched, so this value still
/// identifies the run's file after it has been rotated into the instance's logs directory.
/// </param>
/// <param name="StartedAt">
/// When the run was spawned, giving its duration. Null for a run adopted from a previous daemon,
/// whose spawn this process never saw.
/// </param>
/// <param name="Outcome">
/// <c>crashed</c> (exited while wanted running), <c>gave-up</c> (crashed and the restart limit was
/// reached), <c>exited</c> (a clean code-0 exit left down by the on-failure policy), or
/// <c>stopped</c> (an operator asked for it). Never guessed: the supervisor gates each of these on
/// intent, and an exit with no record at all reads as unknown rather than as any of them.
/// </param>
/// <param name="ExitCode">The leader's exit code where it could be read; null is an honest unknown.</param>
/// <param name="RestartCount">The consecutive-failure streak at the moment this run ended.</param>
/// <param name="Detail">The supervisor's own one-line reason, as it logged it.</param>
internal sealed record RunRecord(
    DateTime EndedAt,
    DateTime? StartedAt,
    string Outcome,
    int? ExitCode,
    int RestartCount,
    string Detail)
{
    /// <summary>Exited while it was wanted running.</summary>
    public const string Crashed = "crashed";

    /// <summary>Crashed, and the supervisor stopped trying to bring it back.</summary>
    public const string GaveUp = "gave-up";

    /// <summary>Exited cleanly and was deliberately left down by the on-failure restart policy.</summary>
    public const string Exited = "exited";

    /// <summary>An operator asked for it to stop.</summary>
    public const string Stopped = "stopped";
}
