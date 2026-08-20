using TheKrystalShip.KGSM.Watchdog.Control;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The run ledger: how each run ended, and how a row finds the console file it describes.
/// <para>
/// The case behind it is romestead. The server aborted, the supervisor restarted it a second later,
/// and the crash's output was rotated into the instance's logs directory while a clean boot took its
/// place at the live path. The supervisor is the only thing that saw the exit and could tell it from
/// a deliberate stop; the ledger is how that knowledge reaches a reader afterwards.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // the store resolves its path from the environment
public sealed class RunHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kgsm-wd-ledger-" + Guid.NewGuid().ToString("N"));

    public RunHistoryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private RunHistoryStore NewStore() =>
        TestState.RunHistory(new WatchdogOptions { StateFile = Path.Combine(_dir, "desired-state.json") });

    private static RunRecord Run(DateTime endedAt, string outcome = RunRecord.Crashed, int? exit = 139) =>
        new(endedAt, StartedAt: endedAt.AddMinutes(-30), outcome, exit, RestartCount: 1, Detail: "test");

    private static DateTime At(int minute, int second = 0) =>
        new(2026, 8, 11, 18, minute, second, DateTimeKind.Utc);

    [Fact]
    public void RoundTripsThroughAFreshStore()
    {
        NewStore().Record("romestead", Run(At(16, 38)));

        var runs = NewStore().RunsFor("romestead");

        Assert.Single(runs);
        Assert.Equal(RunRecord.Crashed, runs[0].Outcome);
        Assert.Equal(139, runs[0].ExitCode);
        Assert.Equal(At(16, 38), runs[0].EndedAt);
    }

    [Fact]
    public void AnInstanceWithNoRunsIsEmpty_NotAnError()
    {
        Assert.Empty(NewStore().RunsFor("never-run"));
    }

    [Fact]
    public void NewestFirst()
    {
        var store = NewStore();
        store.Record("romestead", Run(At(10)));
        store.Record("romestead", Run(At(20)));

        var runs = store.RunsFor("romestead");

        Assert.Equal(At(20), runs[0].EndedAt);
        Assert.Equal(At(10), runs[1].EndedAt);
    }

    [Fact]
    public void ARunIsRecordedOnce_AndTheFirstClassificationStands()
    {
        // A crash leaves a restart pending; an operator then cancels it. Two transitions, ONE run —
        // and the second must not re-label the crash as a stop, or the console that holds a stack
        // trace reports as a clean shutdown.
        var store = NewStore();
        store.Record("romestead", Run(At(16, 38), RunRecord.Crashed));
        store.Record("romestead", Run(At(16, 38), RunRecord.Stopped, exit: null));

        var runs = store.RunsFor("romestead");

        Assert.Single(runs);
        Assert.Equal(RunRecord.Crashed, runs[0].Outcome);
    }

    [Fact]
    public void OlderRunsFallOffTheEnd()
    {
        var store = NewStore();
        for (int i = 0; i < RunHistoryStore.MaxRunsPerInstance + 5; i++)
            store.Record("crashloop", Run(At(0, i)));

        var runs = store.RunsFor("crashloop");

        Assert.Equal(RunHistoryStore.MaxRunsPerInstance, runs.Count);
        // The cap keeps the NEWEST, which is the end a crash is correlated against.
        Assert.Equal(At(0, RunHistoryStore.MaxRunsPerInstance + 4), runs[0].EndedAt);
    }

    [Fact]
    public void InstancesAreKeptApart()
    {
        var store = NewStore();
        store.Record("romestead", Run(At(10)));
        store.Record("factorio", Run(At(20), RunRecord.Stopped));

        Assert.Equal(RunRecord.Crashed, store.RunsFor("romestead")[0].Outcome);
        Assert.Equal(RunRecord.Stopped, store.RunsFor("factorio")[0].Outcome);
    }

    [Fact]
    public void ForgettingAnInstanceDropsItsRows()
    {
        var store = NewStore();
        store.Record("romestead", Run(At(10)));
        store.Forget("romestead");

        Assert.Empty(store.RunsFor("romestead"));
    }

    [Fact]
    public void ACorruptLedgerReadsAsEmpty_RatherThanThrowing()
    {
        // A bad file must never break a supervision transition — the next write rewrites it cleanly.
        var store = NewStore();
        File.WriteAllText(Path.Combine(_dir, "run-history.json"), "{ this is not json");

        Assert.Empty(store.RunsFor("romestead"));

        store.Record("romestead", Run(At(10)));
        Assert.Single(store.RunsFor("romestead"));
    }
}

/// <summary>
/// Joining a ledger row to the console file it describes. Both timestamps come from the same
/// <c>stat</c> of the same file — the supervisor reads mtime when it concludes the run, and rotation
/// moves the file with <c>rename(2)</c>, which leaves mtime alone — so the join is an equality with a
/// tolerance for coarse filesystem timestamps, not a search.
/// </summary>
public sealed class ConsoleRunLedgerJoinTests
{
    private static DateTime At(int minute, int second) =>
        new(2026, 8, 11, 18, minute, second, DateTimeKind.Utc);

    private static RunRecord Run(DateTime endedAt, string outcome, int? exit = null) =>
        new(endedAt, StartedAt: null, outcome, exit, RestartCount: 0, Detail: "");

    [Fact]
    public void FindsTheRowForThisRun()
    {
        var ledger = new[]
        {
            Run(At(16, 38), RunRecord.Crashed, 139),
            Run(At(46, 50), RunRecord.Stopped),
        };

        var match = ConsoleEndpoints.MatchLedger(ledger, At(16, 38));

        Assert.Equal(RunRecord.Crashed, match!.Outcome);
        Assert.Equal(139, match.ExitCode);
    }

    [Fact]
    public void UnderACrashLoop_EachConsoleGetsItsOwnRow()
    {
        // Runs seconds apart. The tolerance is well below the gap, so a row can never describe the
        // neighbouring run's console — which would attach one crash's exit code to another's output.
        var ledger = new[]
        {
            Run(At(16, 44), RunRecord.Crashed, 139),
            Run(At(16, 20), RunRecord.Crashed, 1),
            Run(At(15, 55), RunRecord.Crashed, 139),
        };

        Assert.Equal(139, ConsoleEndpoints.MatchLedger(ledger, At(16, 44))!.ExitCode);
        Assert.Equal(1, ConsoleEndpoints.MatchLedger(ledger, At(16, 20))!.ExitCode);
    }

    [Fact]
    public void ARunWithNoRowIsUnmatched_NotTheNearestOne()
    {
        // The whole honesty of the field: a console the ledger says nothing about reports unknown.
        // Reaching for the closest row instead would label an unrecorded run with another's outcome.
        var ledger = new[] { Run(At(16, 38), RunRecord.Crashed, 139) };

        Assert.Null(ConsoleEndpoints.MatchLedger(ledger, At(10, 0)));
        Assert.Null(ConsoleEndpoints.MatchLedger([], At(16, 38)));
    }

    [Fact]
    public void ACoarserFilesystemTimestampStillMatches()
    {
        // Some filesystems store mtime at a second's resolution, so the value read back can be
        // truncated relative to the one recorded. Within a second is still the same run.
        var ledger = new[] { Run(new DateTime(2026, 8, 11, 18, 16, 38, 750, DateTimeKind.Utc), RunRecord.Crashed) };

        Assert.NotNull(ConsoleEndpoints.MatchLedger(ledger, At(16, 38)));
    }
}

/// <summary>
/// The two lookups the control surface reports a run duration from: when the current run was spawned,
/// and when the last one ended. Both come out of the ledger the supervisor already keeps, which is
/// what makes them survive a daemon restart — a consumer asking "running since when" must not be told
/// the moment the daemon last came back.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class RunHistoryLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kgsm-wd-lookup-" + Guid.NewGuid().ToString("N"));

    public RunHistoryLookupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private RunHistoryStore NewStore() =>
        TestState.RunHistory(new WatchdogOptions { StateFile = Path.Combine(_dir, "desired-state.json") });

    private static RunRecord Run(DateTime endedAt) =>
        new(endedAt, StartedAt: endedAt.AddMinutes(-30), RunRecord.Stopped, 0, RestartCount: 0, Detail: "test");

    private static DateTime At(int day, int hour) => new(2026, 8, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LastEndedForReportsTheNewestRun()
    {
        var store = NewStore();
        store.Record("romestead", Run(At(11, 9)));
        store.Record("romestead", Run(At(12, 18)));   // newer, recorded second

        Assert.Equal(At(12, 18), NewStore().LastEndedFor("romestead"));
    }

    [Fact]
    public void LastEndedForIsNullForAnInstanceWithNoRuns()
    {
        NewStore().Record("romestead", Run(At(11, 9)));

        // An instance the ledger has never seen is an honest unknown, never a fabricated date.
        Assert.Null(NewStore().LastEndedFor("necesse"));
    }

    [Fact]
    public void LastEndedByInstanceAnswersEveryInstanceFromOneRead()
    {
        var store = NewStore();
        store.Record("romestead", Run(At(11, 9)));
        store.Record("romestead", Run(At(12, 18)));
        store.Record("necesse", Run(At(10, 7)));

        var all = NewStore().LastEndedByInstance();

        Assert.Equal(2, all.Count);
        Assert.Equal(At(12, 18), all["romestead"]);
        Assert.Equal(At(10, 7), all["necesse"]);
    }

    [Fact]
    public void LastEndedByInstanceIsEmptyWithNoLedger()
    {
        Assert.Empty(NewStore().LastEndedByInstance());
    }
}
