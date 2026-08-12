using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the durable desired-running set: that intent round-trips across daemon restarts (a fresh
/// store instance reads what a prior one wrote), that Add/Remove are incremental and idempotent, and
/// — the load-bearing robustness property — that a missing or corrupt file degrades to "nothing to
/// restore" instead of throwing and wedging boot. Each test points the store at a private temp file
/// via <see cref="WatchdogOptions.StateFile"/>, so nothing touches a real HOME.
/// </summary>
[Collection(EnvironmentCollection.Name)] // default-path tests mutate HOME/XDG_DATA_HOME — serialize with the other env-touching classes
public sealed class DesiredStateStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public DesiredStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kgsm-wd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "nested", "desired-state.json"); // nested: Save must create the dir
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DesiredStateStore NewStore() =>
        TestState.Desired(new WatchdogOptions { StateFile = _file });

    [Fact]
    public void Load_missing_file_is_empty()
    {
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void Add_then_load_returns_the_name_and_creates_the_dir()
    {
        var store = NewStore();
        store.Add("7dtd");

        Assert.True(File.Exists(_file));
        Assert.Equal(new[] { "7dtd" }, NewStore().Load());
    }

    [Fact]
    public void Add_is_idempotent_no_duplicates()
    {
        var store = NewStore();
        store.Add("factorio");
        store.Add("factorio");

        Assert.Equal(new[] { "factorio" }, store.Load());
    }

    [Fact]
    public void Survives_a_fresh_store_instance()
    {
        // The whole point: a NEW store (≈ a daemon restart) sees what a prior one persisted.
        NewStore().Add("alpha");
        NewStore().Add("beta");

        Assert.Equal(new[] { "alpha", "beta" }, NewStore().Load());
    }

    [Fact]
    public void Remove_drops_only_that_name()
    {
        var store = NewStore();
        store.Add("alpha");
        store.Add("beta");

        store.Remove("alpha");

        Assert.Equal(new[] { "beta" }, NewStore().Load());
    }

    [Fact]
    public void Remove_absent_name_is_a_noop()
    {
        var store = NewStore();
        store.Add("beta");

        store.Remove("ghost"); // never added

        Assert.Equal(new[] { "beta" }, NewStore().Load());
    }

    [Fact]
    public void Corrupt_file_loads_as_empty_without_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ this is not valid json ]");

        var store = NewStore();

        Assert.Empty(store.Load());           // degrades, doesn't throw
        store.Add("recovered");               // and the next write heals the file
        Assert.Equal(new[] { "recovered" }, NewStore().Load());
    }

    // ---- the DEFAULT (empty StateFile) path — the branch every real operator hits ----------------
    // These exercise ResolvePath()'s else-branch, which derives the file under the dropped KGSM user's
    // data dir from HOME/XDG_DATA_HOME (resolved lazily, post-bootstrap). Pinning StateFile in the
    // tests above never reaches it, so it would otherwise be wholly untested.

    private DesiredStateStore DefaultStore() =>
        TestState.Desired(new WatchdogOptions()); // StateFile = "" -> derive it

    [Fact]
    public void Default_path_derives_under_HOME_when_XDG_unset()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", _dir);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);

            DefaultStore().Add("from-home");

            var expected = Path.Combine(_dir, ".local", "share", "kgsm-watchdog", "desired-state.json");
            Assert.True(File.Exists(expected), $"expected state at {expected}");
            Assert.Equal(new[] { "from-home" }, DefaultStore().Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
        }
    }

    [Fact]
    public void Default_path_prefers_XDG_DATA_HOME_over_HOME()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            var xdgDir = Path.Combine(_dir, "xdg");
            Environment.SetEnvironmentVariable("HOME", Path.Combine(_dir, "home")); // must NOT be used
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDir);

            DefaultStore().Add("from-xdg");

            var expected = Path.Combine(xdgDir, "kgsm-watchdog", "desired-state.json");
            Assert.True(File.Exists(expected), $"expected state at {expected}");
            Assert.False(File.Exists(Path.Combine(_dir, "home", ".local", "share", "kgsm-watchdog", "desired-state.json")),
                "HOME must not be used when XDG_DATA_HOME is set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
        }
    }
}
