using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Cgroup;

/// <summary>
/// cgroup v2 process-supervision primitives — the C# port of <c>kgsm/core/cgroup.sh</c>.
/// Where the monitor <em>reads</em> cgroup counters, the watchdog <em>writes</em> the control
/// files: create a child cgroup, move a PID in (<c>cgroup.procs</c>), atomically kill the whole
/// subtree (<c>cgroup.kill</c>), and read child-inclusive liveness (<c>cgroup.events</c>).
/// <para>
/// All operations target KGSM's delegated base (<see cref="WatchdogOptions.CgroupBasePath"/>).
/// Methods return <c>bool</c> (success) and log on hard failure rather than throwing — except
/// argument validation, which throws (mirrors the kgsm-lib failure-channel convention).
/// Callers must gate runtime use on <see cref="Supported"/> first.
/// </para>
/// </summary>
internal sealed class CgroupManager(WatchdogOptions options, ILogger<CgroupManager> logger)
{
    private readonly WatchdogOptions _opts = options;
    private readonly ILogger<CgroupManager> _log = logger;

    // Runtime-discovered delegated base (the daemon's own systemd service cgroup, from
    // /proc/self/cgroup — set by CgroupBootstrap). Null until discovery runs, in which case we
    // fall back to the configured CgroupBasePath. See CgroupDiscovery / PLAN Increment 8.
    private string? _resolvedBase;

    /// <summary>
    /// Absolute path of KGSM's delegated cgroup base — the daemon's own systemd-delegated service
    /// cgroup once <see cref="UseResolvedBase"/> has been called (e.g.
    /// <c>/sys/fs/cgroup/kgsm.slice/kgsm-watchdog.service</c>), else the configured fallback
    /// (<see cref="WatchdogOptions.CgroupBasePath"/>). Per-instance cgroups are children of this.
    /// </summary>
    public string Base => _resolvedBase ?? _opts.CgroupBasePath;

    /// <summary>
    /// Adopt the delegated base discovered at runtime from <c>/proc/self/cgroup</c> (PLAN Inc 8). This
    /// is authoritative over the configured <c>CgroupBaseName</c>, which becomes a fallback used only
    /// when discovery fails. Set once at bootstrap, before any cgroup is created.
    /// </summary>
    public void UseResolvedBase(string absoluteBase)
    {
        if (!string.IsNullOrEmpty(absoluteBase))
            _resolvedBase = absoluteBase;
    }

    /// <summary>The supervisor leaf the daemon lives in (e.g. <c>/sys/fs/cgroup/kgsm.slice/supervisor</c>).</summary>
    public string SupervisorPath => $"{Base}/{_opts.SupervisorLeaf}";

    /// <summary>
    /// Per-instance cgroup path under the base. A trailing <c>.ini</c> is stripped (KGSM passes
    /// instance config filenames in places). Throws <see cref="ArgumentException"/> on empty input.
    /// </summary>
    public string PathFor(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName))
            throw new ArgumentException("instance name must not be empty", nameof(instanceName));

        string name = instanceName.EndsWith(".ini", StringComparison.Ordinal)
            ? instanceName[..^4]
            : instanceName;
        return $"{Base}/{name}";
    }

    /// <summary>The configured controllers in <c>+x</c> subtree_control form, e.g. <c>"+cpu +memory +io +pids"</c>.</summary>
    public string EnableString()
        => string.Join(' ', _opts.CgroupControllers.Select(c => "+" + c));

    /// <summary>
    /// Enable the configured controllers in a cgroup's <c>subtree_control</c> so its children
    /// inherit them. Idempotent — controllers already enabled are skipped (writing a duplicate
    /// <c>+x</c> is harmless, but skipping avoids noise). Used at bootstrap on the mount root and
    /// on KGSM's base. Returns false if the file is absent or a write is rejected.
    /// </summary>
    public bool EnableControllers(string cgDir)
    {
        string sc = Path.Combine(cgDir, "cgroup.subtree_control");
        if (!File.Exists(sc))
        {
            _log.LogWarning("cgroup.subtree_control missing in {Dir}", cgDir);
            return false;
        }

        string current;
        try { current = File.ReadAllText(sc); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not read {File}", sc);
            return false;
        }

        var enabled = current.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var controller in _opts.CgroupControllers)
        {
            if (enabled.Contains(controller))
                continue;
            if (!TryWrite(sc, "+" + controller))
            {
                _log.LogWarning("failed to enable controller {Controller} in {Dir}", controller, cgDir);
                return false;
            }
        }
        return true;
    }

    /// <summary>True if the running kernel is &gt;= 5.14 (when <c>cgroup.kill</c> landed).</summary>
    public static bool KernelHasKill()
    {
        string release;
        try { release = File.ReadAllText("/proc/sys/kernel/osrelease").Trim(); }
        catch { return false; }

        // e.g. "7.0.11-arch1-1" -> major 7, minor 0
        var dot = release.IndexOf('.');
        if (dot <= 0)
            return false;

        if (!int.TryParse(release.AsSpan(0, dot), out int major))
            return false;

        var rest = release[(dot + 1)..];
        int end = 0;
        while (end < rest.Length && char.IsDigit(rest[end]))
            end++;
        if (end == 0 || !int.TryParse(rest.AsSpan(0, end), out int minor))
            return false;

        return major > 5 || (major == 5 && minor >= 14);
    }

    /// <summary>
    /// Whether cgroup v2 supervision is usable: unified mount present, kernel &gt;= 5.14, and KGSM's
    /// base exists and is writable (delegated). No output, no side effects — a pure gate.
    /// </summary>
    public bool Supported()
    {
        if (!File.Exists(Path.Combine(_opts.CgroupMountPoint, "cgroup.controllers")))
            return false;
        if (!KernelHasKill())
            return false;
        if (!Directory.Exists(Base))
            return false;
        // Faithful to bash `[[ -w "$base" ]]`: can we actually write the delegated base?
        return NativeMethods.access(Base, NativeMethods.W_OK) == 0;
    }

    /// <summary>True if the per-instance cgroup directory currently exists.</summary>
    public bool Exists(string instanceName) => Directory.Exists(PathFor(instanceName));

    /// <summary>Create the per-instance cgroup (idempotent mkdir). Controllers are inherited from the base.</summary>
    public bool Create(string instanceName)
    {
        string cg = PathFor(instanceName);
        try
        {
            Directory.CreateDirectory(cg);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to create cgroup {Cgroup}", cg);
            return false;
        }
    }

    /// <summary>Move a process (and its future children) into the instance cgroup by writing its PID.</summary>
    public bool Attach(string instanceName, int pid)
        => AttachToDir(PathFor(instanceName), pid);

    /// <summary>Move a process into an arbitrary cgroup dir (used by bootstrap to enter the supervisor leaf).</summary>
    public bool AttachToDir(string cgDir, int pid)
    {
        if (pid <= 0)
            throw new ArgumentException("pid must be positive", nameof(pid));
        return TryWrite(Path.Combine(cgDir, "cgroup.procs"), pid.ToString());
    }

    /// <summary>Translates a priority name to a cgroup cpu.weight value (Linux default 100 = "normal").</summary>
    public static int CpuWeightFor(string priority) => priority switch
    {
        "low"  => 50,
        "high" => 400,
        _      => 100,   // "normal" or unknown → Linux default
    };

    /// <summary>
    /// Write the instance cgroup's <c>cpu.weight</c> (Phase 2, CPU priority). Live-applied — the kernel
    /// re-weights the running cgroup immediately. Returns false (and logs) if the cgroup is absent
    /// (instance not running), so the caller can report "will apply at next start".
    /// </summary>
    public bool SetCpuWeight(string instanceName, int weight)
    {
        string cg = PathFor(instanceName);
        if (!Directory.Exists(cg))
        {
            _log.LogDebug("SetCpuWeight: cgroup {Cg} does not exist — instance not running", cg);
            return false;
        }
        return TryWrite(Path.Combine(cg, "cpu.weight"), weight.ToString());
    }

    /// <summary>
    /// Write the instance cgroup's <c>memory.max</c> (Phase 2, memory cap). A cap of 0 or null writes
    /// <c>max</c> (uncapped). The kernel enforces the limit immediately, but a process already over it
    /// is not retro-shrunk — so this is only guaranteed clean when set at spawn (before the game grows).
    /// Returns false if the cgroup is absent.
    /// </summary>
    public bool SetMemoryMax(string instanceName, long? capMb)
    {
        string cg = PathFor(instanceName);
        if (!Directory.Exists(cg))
        {
            _log.LogDebug("SetMemoryMax: cgroup {Cg} does not exist — instance not running", cg);
            return false;
        }
        string value = capMb is null or <= 0 ? "max" : (capMb.Value * 1024 * 1024).ToString();
        return TryWrite(Path.Combine(cg, "memory.max"), value);
    }

    /// <summary>
    /// Liveness: true if the instance cgroup still has live processes, false if empty/absent.
    /// Reads <c>cgroup.events</c> <c>populated</c> — child-inclusive and race-free, unlike a PID check.
    /// </summary>
    public bool IsPopulated(string instanceName)
    {
        string events = Path.Combine(PathFor(instanceName), "cgroup.events");
        if (!File.Exists(events))
            return false;
        try
        {
            foreach (var line in File.ReadLines(events))
                if (line.StartsWith("populated ", StringComparison.Ordinal))
                    return line.AsSpan("populated ".Length).Trim().SequenceEqual("1");
        }
        catch
        {
            // teardown race: the file vanished between Exists and read -> treat as empty.
        }
        return false;
    }

    /// <summary>
    /// The instance names whose cgroup directly under <see cref="Base"/> currently holds a live process,
    /// excluding the supervisor leaf the daemon itself runs in. Used at startup to re-adopt instances that
    /// outlived a daemon restart (running but not in the persisted boot-autostart set). Returns empty when
    /// the base is absent or after a host reboot — the cgroups died with the host — so it adds nothing then.
    /// </summary>
    public IReadOnlyList<string> LivePopulatedInstances()
    {
        if (!Directory.Exists(Base))
            return [];

        var live = new List<string>();
        foreach (string dir in Directory.EnumerateDirectories(Base))
        {
            string name = Path.GetFileName(dir);
            if (string.Equals(name, _opts.SupervisorLeaf, StringComparison.Ordinal))
                continue; // the daemon's own leaf, not a game instance
            if (IsPopulated(name))
                live.Add(name);
        }
        return live;
    }

    /// <summary>
    /// The first PID listed in the instance cgroup's <c>cgroup.procs</c>, or null if empty/absent. Used to
    /// recover a display PID for an ADOPTED instance: the daemon is no longer its parent, so the PID comes
    /// from the cgroup, not a child <c>Process</c>. The kernel lists procs in numeric order, so the first
    /// entry is the lowest PID — in practice the launched leader. Best-effort (a teardown race reads null).
    /// </summary>
    public int? FirstPid(string instanceName)
    {
        string procs = Path.Combine(PathFor(instanceName), "cgroup.procs");
        try
        {
            foreach (var line in File.ReadLines(procs))
                if (int.TryParse(line.Trim(), out int pid))
                    return pid;
        }
        catch
        {
            // absent / teardown race -> unknown
        }
        return null;
    }

    /// <summary>Atomically SIGKILL every process in the instance cgroup (whole subtree) via <c>cgroup.kill</c>.</summary>
    public bool Kill(string instanceName)
    {
        string killFile = Path.Combine(PathFor(instanceName), "cgroup.kill");
        if (!File.Exists(killFile))
        {
            _log.LogWarning("cgroup.kill missing for {Instance} (kernel < 5.14?)", instanceName);
            return false;
        }
        return TryWrite(killFile, "1");
    }

    /// <summary>
    /// Remove the (now-empty) instance cgroup. Best-effort: a populated cgroup cannot be removed,
    /// so callers kill + wait for it to drain first. Already-gone is success.
    /// </summary>
    public bool Remove(string instanceName)
    {
        string cg = PathFor(instanceName);
        if (!Directory.Exists(cg))
            return true;
        try
        {
            Directory.Delete(cg); // non-recursive: equivalent to rmdir, fails if still populated
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "failed to remove cgroup (still populated?) {Cgroup}", cg);
            return false;
        }
    }

    /// <summary>
    /// Write a single value to a cgroup control file. Mirrors bash <c>{ echo "$x" &gt; "$f"; } 2&gt;/dev/null</c>:
    /// any failure (EACCES on a non-delegated file, ENOENT on a torn-down cgroup) is swallowed to a
    /// false return, not an exception.
    /// </summary>
    private bool TryWrite(string file, string value)
    {
        try
        {
            File.WriteAllText(file, value);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "write '{Value}' -> {File} failed", value, file);
            return false;
        }
    }
}
