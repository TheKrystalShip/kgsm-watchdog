using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the companion supervision-state store: that restart counters round-trip across a daemon
/// restart (a fresh store reads what a prior one wrote), that a missing or corrupt file degrades to an
/// empty snapshot instead of throwing (a bad file must never wedge boot), and that the atomic write
/// leaves no temp residue. Each test pins <see cref="WatchdogOptions.StateFile"/> at a private temp path,
/// so the store derives its sibling <c>supervision-state.json</c> there and nothing touches a real HOME.
/// </summary>
public sealed class SupervisionStateStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stateFile;   // the DesiredStateStore path; the supervision file is its sibling
    private readonly string _supervisionFile;

    public SupervisionStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kgsm-wd-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Nested so Save() must create the dir, exactly like the desired-state store's tests.
        _stateFile = Path.Combine(_dir, "nested", "desired-state.json");
        _supervisionFile = Path.Combine(_dir, "nested", "supervision-state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SupervisionStateStore NewStore() =>
        TestState.Supervision(new WatchdogOptions { StateFile = _stateFile });

    [Fact]
    public void Load_missing_file_is_empty()
    {
        var loaded = NewStore().Load();
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Instances);
    }

    [Fact]
    public void Save_then_load_round_trips_every_field_and_creates_the_dir()
    {
        var spawned = new DateTime(2026, 6, 25, 10, 0, 0, DateTimeKind.Utc);
        var next = new DateTime(2026, 6, 25, 10, 5, 0, DateTimeKind.Utc);
        var state = new PersistedSupervisionState
        {
            Instances =
            {
                ["7dtd"] = new InstanceRestartState
                {
                    ConsecutiveFailures = 3,
                    GaveUp = true,
                    Phase = "Failed",
                    SpawnedAt = spawned,
                    NextRestartAt = next,
                    LastReason = "restart limit reached",
                },
                ["factorio"] = new InstanceRestartState
                {
                    ConsecutiveFailures = 1,
                    GaveUp = false,
                    Phase = "RestartPending",
                },
            },
        };

        NewStore().Save(state);
        Assert.True(File.Exists(_supervisionFile), $"expected supervision state at {_supervisionFile}");

        // A FRESH store (≈ a daemon restart) sees what the prior one persisted.
        var loaded = NewStore().Load();
        Assert.Equal(2, loaded.Instances.Count);

        var sevendtd = loaded.Instances["7dtd"];
        Assert.Equal(3, sevendtd.ConsecutiveFailures);
        Assert.True(sevendtd.GaveUp);
        Assert.Equal("Failed", sevendtd.Phase);
        Assert.Equal(spawned, sevendtd.SpawnedAt);
        Assert.Equal(next, sevendtd.NextRestartAt);
        Assert.Equal("restart limit reached", sevendtd.LastReason);

        var fac = loaded.Instances["factorio"];
        Assert.Equal(1, fac.ConsecutiveFailures);
        Assert.False(fac.GaveUp);
        Assert.Equal("RestartPending", fac.Phase);
        Assert.Null(fac.SpawnedAt);
        Assert.Null(fac.NextRestartAt);
    }

    [Fact]
    public void Corrupt_file_loads_as_empty_without_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_supervisionFile)!);
        File.WriteAllText(_supervisionFile, "{ this is not valid json ]");

        var store = NewStore();
        var loaded = store.Load();          // degrades, doesn't throw
        Assert.Empty(loaded.Instances);

        // and the next write heals the file
        store.Save(new PersistedSupervisionState { Instances = { ["recovered"] = new InstanceRestartState { ConsecutiveFailures = 2 } } });
        Assert.Equal(2, NewStore().Load().Instances["recovered"].ConsecutiveFailures);
    }

    [Fact]
    public void Save_leaves_no_temp_residue()
    {
        NewStore().Save(new PersistedSupervisionState { Instances = { ["7dtd"] = new InstanceRestartState() } });

        // The atomic write uses "<path>.tmp"; after a successful Save it must be renamed away, not left behind.
        Assert.False(File.Exists(_supervisionFile + ".tmp"), "atomic write left a .tmp residue");
        var dir = Path.GetDirectoryName(_supervisionFile)!;
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Supervision_file_is_a_sibling_of_the_desired_state_file()
    {
        NewStore().Save(new PersistedSupervisionState { Instances = { ["7dtd"] = new InstanceRestartState() } });

        // It lands next to desired-state.json (same dir), distinct filename — never collides with it.
        Assert.True(File.Exists(_supervisionFile));
        Assert.Equal(Path.GetDirectoryName(_stateFile), Path.GetDirectoryName(_supervisionFile));
        Assert.NotEqual(Path.GetFileName(_stateFile), Path.GetFileName(_supervisionFile));
    }
}
