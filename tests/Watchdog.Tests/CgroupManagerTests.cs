using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using Xunit.Abstractions;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The C# counterpart of <c>kgsm/tests/unit/test_cgroup.sh</c>: deterministic coverage of the
/// pure cgroup helpers (no privilege needed), plus one live create → attach → kill → remove
/// round-trip that runs ONLY when a real delegated base exists and the test's own cgroup can enter
/// it. With no mocks, it exercises real cgroupfs or skips — never fakes the kernel. The round-trip
/// is bounded and always reaps its helper, so it can never hang the suite (the lesson from the
/// bash test that blocked the terminal).
/// </summary>
public sealed class CgroupManagerTests(ITestOutputHelper output)
{
    private static CgroupManager Make(WatchdogOptions opts)
        => new(opts, NullLogger<CgroupManager>.Instance);

    private static WatchdogOptions Defaults() => new();

    [Fact]
    public void Base_resolves_mount_and_base_from_config()
    {
        var mgr = Make(new WatchdogOptions { CgroupMountPoint = "/sys/fs/cgroup", CgroupBaseName = "kgsm.slice" });
        Assert.Equal("/sys/fs/cgroup/kgsm.slice", mgr.Base);
    }

    [Fact]
    public void PathFor_appends_instance_and_strips_ini()
    {
        var mgr = Make(Defaults());
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/factorio-42", mgr.PathFor("factorio-42"));
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/factorio-42", mgr.PathFor("factorio-42.ini"));
    }

    [Fact]
    public void PathFor_rejects_empty()
    {
        var mgr = Make(Defaults());
        Assert.Throws<ArgumentException>(() => mgr.PathFor(""));
    }

    [Fact]
    public void EnableString_formats_controllers()
    {
        var mgr = Make(new WatchdogOptions { CgroupControllers = ["cpu", "memory", "io", "pids"] });
        Assert.Equal("+cpu +memory +io +pids", mgr.EnableString());
    }

    [Fact]
    public void KernelHasKill_matches_running_kernel()
    {
        // cgroup.kill landed in 5.14. Compute the expectation from the running kernel rather than
        // hard-coding, so this stays correct on any host/CI.
        string release = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
        var parts = release.Split('.', '-');
        int major = int.Parse(parts[0]);
        int minor = int.Parse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()));
        bool expected = major > 5 || (major == 5 && minor >= 14);

        Assert.Equal(expected, CgroupManager.KernelHasKill());
    }

    [Fact]
    public void Supported_false_when_base_missing()
    {
        var mgr = Make(new WatchdogOptions
        {
            CgroupMountPoint = "/sys/fs/cgroup",
            CgroupBaseName = $"kgsm-nonexistent-{Guid.NewGuid():N}.slice",
        });
        Assert.False(mgr.Supported());
    }

    [Fact]
    public void IsPopulated_false_for_absent_cgroup()
    {
        var mgr = Make(new WatchdogOptions { CgroupBaseName = $"kgsm-nonexistent-{Guid.NewGuid():N}.slice" });
        Assert.False(mgr.IsPopulated("ghost-instance"));
    }

    [Fact]
    public void Remove_absent_cgroup_is_success()
    {
        var mgr = Make(new WatchdogOptions { CgroupBaseName = $"kgsm-nonexistent-{Guid.NewGuid():N}.slice" });
        Assert.True(mgr.Remove("ghost-instance"));
    }

    [Fact]
    public void Lifecycle_roundtrip_create_attach_kill_remove()
    {
        var mgr = Make(Defaults());

        // Only run against a real delegated base; otherwise skip (no mocks, no fakes).
        if (!mgr.Supported())
        {
            output.WriteLine("skip: no delegated cgroup base (run 'kgsm system setup-cgroups')");
            return;
        }

        string inst = $"kgsm-cgtest-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string cg = mgr.PathFor(inst);

        Assert.True(mgr.Create(inst), "create should succeed on a delegated base");
        Assert.True(Directory.Exists(cg));

        // Spawn a short-lived, bounded helper and try to move it into the cgroup. Rootless entry
        // only works from INSIDE the user-owned delegated subtree (cgroup-v2 delegation
        // containment). An SSH/system.slice login or CI without delegation cannot — so we skip,
        // not fail. The helper is always reaped before any wait, so this cannot block the suite.
        Process? helper = null;
        try
        {
            helper = Process.Start(new ProcessStartInfo("/bin/sleep", "5") { UseShellExecute = false });
            Assert.NotNull(helper);

            if (!mgr.Attach(inst, helper.Id))
            {
                output.WriteLine("skip: cannot enter delegated base from this cgroup context (needs root or a systemd user session)");
                return;
            }

            Assert.True(mgr.IsPopulated(inst), "a cgroup with a live process must read populated");

            Assert.True(mgr.Kill(inst), "cgroup.kill should succeed");

            // Bounded drain (<= ~5s); never an unbounded wait.
            for (int i = 0; i < 50 && mgr.IsPopulated(inst); i++)
                Thread.Sleep(100);

            Assert.False(mgr.IsPopulated(inst), "cgroup should be empty after kill");
            Assert.True(mgr.Remove(inst), "cgroup remove should succeed");
            Assert.False(Directory.Exists(cg));
        }
        finally
        {
            // kill -9 is a no-op safety net (cgroup.kill already reaped it), and runs only after a
            // kill, so it cannot block. Then tidy the cgroup if a skip/early assert left it behind.
            try { helper?.Kill(true); } catch { /* already gone */ }
            try { helper?.WaitForExit(2000); } catch { /* ignore */ }
            helper?.Dispose();
            try { if (Directory.Exists(cg)) Directory.Delete(cg); } catch { /* best effort */ }
        }
    }
}
