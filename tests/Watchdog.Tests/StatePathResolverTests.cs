using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Watchdog.Supervision;

using Xunit;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Where the state files land, and that the ones already written somewhere else come along.
/// <para>
/// The carry-over is the part worth testing hardest. <c>desired-state.json</c> is the only record of
/// which instances come back after a reboot, and resolving to a new directory while leaving it behind
/// fails silently: nothing errors, nothing starts, and nothing says why.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // pins HOME/XDG_DATA_HOME/STATE_DIRECTORY — serialize with the other env mutators
public sealed class StatePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kgsm-wd-paths-" + Guid.NewGuid().ToString("N"));

    private readonly string _home;
    private readonly string? _priorHome;
    private readonly string? _priorXdg;
    private readonly string? _priorStateDir;

    public StatePathResolverTests()
    {
        // A private HOME/XDG_DATA_HOME so the "legacy" directory is this test's own, never the real
        // one — a resolver test that reads the developer's own state would be both flaky and rude.
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);

        _priorHome = Environment.GetEnvironmentVariable("HOME");
        _priorXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _priorStateDir = Environment.GetEnvironmentVariable("STATE_DIRECTORY");

        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _priorHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _priorXdg);
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", _priorStateDir);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static StatePathResolver Resolve(WatchdogOptions options) =>
        new(options, NullLogger<StatePathResolver>.Instance);

    private string LegacyDir => Path.Combine(_home, ".local", "share", "kgsm-watchdog");

    private void SeedLegacy(string file, string content)
    {
        Directory.CreateDirectory(LegacyDir);
        File.WriteAllText(Path.Combine(LegacyDir, file), content);
    }

    [Fact]
    public void SystemdsStateDirectoryWins_OverTheHomeFallback()
    {
        string systemd = Path.Combine(_root, "var-lib");
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", systemd);

        var resolver = Resolve(new WatchdogOptions());

        Assert.Equal(systemd, resolver.StateDirectory);
        Assert.Equal(Path.Combine(systemd, "run-history.json"),
            resolver.PathFor(StatePathResolver.RunHistoryFile));
    }

    [Fact]
    public void AnExplicitStateFileOutranksSystemd_AndKeepsTheNameItWasGiven()
    {
        // An operator who named the file gets that name, and the companions land beside it.
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", Path.Combine(_root, "var-lib"));
        string chosen = Path.Combine(_root, "operator", "my-state.json");

        var resolver = Resolve(new WatchdogOptions { StateFile = chosen });

        Assert.Equal(chosen, resolver.DesiredStatePath);
        Assert.Equal(Path.Combine(_root, "operator", "supervision-state.json"),
            resolver.PathFor(StatePathResolver.SupervisionStateFile));
    }

    [Fact]
    public void OutsideSystemd_FallsBackToTheHomeDirectory()
    {
        var resolver = Resolve(new WatchdogOptions());

        Assert.Equal(LegacyDir, resolver.StateDirectory);
    }

    [Fact]
    public void TheAutostartSetComesAcross_AndTheOldCopyIsGone()
    {
        // The failure this exists to prevent: the daemon resolves to /var/lib, the autostart set stays
        // in the home directory, and nothing starts at the next boot with no error anywhere.
        SeedLegacy(StatePathResolver.DesiredStateFile, """{"version":1,"desiredRunning":["romestead"]}""");
        SeedLegacy(StatePathResolver.SupervisionStateFile, """{"version":1,"instances":{}}""");

        string systemd = Path.Combine(_root, "var-lib");
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", systemd);

        var resolver = Resolve(new WatchdogOptions());
        string carried = Path.Combine(resolver.StateDirectory, StatePathResolver.DesiredStateFile);

        Assert.True(File.Exists(carried));
        Assert.Contains("romestead", File.ReadAllText(carried));
        Assert.True(File.Exists(
            Path.Combine(resolver.StateDirectory, StatePathResolver.SupervisionStateFile)));

        // Copied THEN deleted, so a crash between the two leaves the original intact rather than
        // nothing at all — and once it has landed the old copy does not linger to confuse anyone.
        Assert.False(File.Exists(Path.Combine(LegacyDir, StatePathResolver.DesiredStateFile)));
    }

    [Fact]
    public void AFileAlreadyInPlaceIsNeverOverwritten()
    {
        // The destination copy is the one the running daemon has been writing, so it is the newer of
        // the two by definition. Clobbering it with an older home-directory copy would resurrect
        // instances an operator had since disabled.
        SeedLegacy(StatePathResolver.DesiredStateFile, """{"version":1,"desiredRunning":["stale"]}""");

        string systemd = Path.Combine(_root, "var-lib");
        Directory.CreateDirectory(systemd);
        File.WriteAllText(Path.Combine(systemd, StatePathResolver.DesiredStateFile),
            """{"version":1,"desiredRunning":["current"]}""");
        Environment.SetEnvironmentVariable("STATE_DIRECTORY", systemd);

        var resolver = Resolve(new WatchdogOptions());

        string landed = File.ReadAllText(
            Path.Combine(resolver.StateDirectory, StatePathResolver.DesiredStateFile));
        Assert.Contains("current", landed);
        Assert.DoesNotContain("stale", landed);
    }

    [Fact]
    public void AnExplicitStateFileDoesNotDragTheHomeDirectorysFilesAlong()
    {
        // Carry-over is for the systemd move. An operator pointing at their own path did not ask for
        // files they never put there to appear in it.
        SeedLegacy(StatePathResolver.DesiredStateFile, """{"version":1,"desiredRunning":["romestead"]}""");
        string chosen = Path.Combine(_root, "operator", StatePathResolver.DesiredStateFile);

        var resolver = Resolve(new WatchdogOptions { StateFile = chosen });

        Assert.False(File.Exists(resolver.DesiredStatePath));
        Assert.True(File.Exists(Path.Combine(LegacyDir, StatePathResolver.DesiredStateFile)));
    }
}
