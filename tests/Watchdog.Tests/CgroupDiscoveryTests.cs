using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Pure coverage of the systemd-delegation base discovery (PLAN Increment 8): parsing
/// <c>/proc/self/cgroup</c> and folding "already in the supervisor leaf" (hot-swap re-exec) back to
/// the same base. No kernel needed — golden inputs only.
/// </summary>
public sealed class CgroupDiscoveryTests
{
    [Fact]
    public void ParseUnifiedPath_reads_the_v2_line()
    {
        Assert.Equal("/kgsm.slice/kgsm-watchdog.service",
            CgroupDiscovery.ParseUnifiedPath("0::/kgsm.slice/kgsm-watchdog.service\n"));
    }

    [Fact]
    public void ParseUnifiedPath_root_cgroup_is_slash()
    {
        Assert.Equal("/", CgroupDiscovery.ParseUnifiedPath("0::/\n"));
    }

    [Fact]
    public void ParseUnifiedPath_null_for_cgroup_v1_hybrid()
    {
        // No "0::" unified line (v1/hybrid: numbered controller hierarchies only).
        Assert.Null(CgroupDiscovery.ParseUnifiedPath(
            "12:pids:/kgsm.slice\n11:memory:/kgsm.slice\n10:cpu,cpuacct:/kgsm.slice\n"));
    }

    [Fact]
    public void ResolveBaseFromSelf_fresh_boot_base_is_the_service_cgroup()
    {
        // Daemon born in its delegated service cgroup -> that IS the base.
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service",
            CgroupDiscovery.ResolveBaseFromSelf(
                "/sys/fs/cgroup", "/kgsm.slice/kgsm-watchdog.service", "supervisor"));
    }

    [Fact]
    public void ResolveBaseFromSelf_after_reexec_strips_supervisor_leaf()
    {
        // Hot-swap re-exec: daemon already moved into <base>/supervisor -> base is the parent.
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service",
            CgroupDiscovery.ResolveBaseFromSelf(
                "/sys/fs/cgroup", "/kgsm.slice/kgsm-watchdog.service/supervisor", "supervisor"));
    }

    [Fact]
    public void ResolveBaseFromSelf_root_cgroup_maps_to_mount()
    {
        Assert.Equal("/sys/fs/cgroup",
            CgroupDiscovery.ResolveBaseFromSelf("/sys/fs/cgroup", "/", "supervisor"));
    }

    [Fact]
    public void UseResolvedBase_overrides_configured_base_for_paths()
    {
        var mgr = new CgroupManager(
            new WatchdogOptions { CgroupMountPoint = "/sys/fs/cgroup", CgroupBaseName = "kgsm.slice" },
            NullLogger<CgroupManager>.Instance);

        // Before discovery: falls back to the configured base.
        Assert.Equal("/sys/fs/cgroup/kgsm.slice", mgr.Base);

        mgr.UseResolvedBase("/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service");

        // After discovery: instance + supervisor paths hang off the delegated service cgroup.
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service", mgr.Base);
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service/factorio-test",
            mgr.PathFor("factorio-test"));
        Assert.Equal("/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service/supervisor",
            mgr.SupervisorPath);
    }

    [Fact]
    public void UseResolvedBase_ignores_empty()
    {
        var mgr = new CgroupManager(
            new WatchdogOptions { CgroupMountPoint = "/sys/fs/cgroup", CgroupBaseName = "kgsm.slice" },
            NullLogger<CgroupManager>.Instance);
        mgr.UseResolvedBase("");
        Assert.Equal("/sys/fs/cgroup/kgsm.slice", mgr.Base);
    }
}
