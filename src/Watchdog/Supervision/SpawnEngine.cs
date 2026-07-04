using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Forks a native game process <em>into its own cgroup</em>, replicating KGSM's native I/O model
/// (FIFO stdin, logfile stdout) but with the supervisor as the in-slice parent.
/// <para>
/// The spawn uses a tiny <c>/bin/sh -c</c> launcher that <b>self-moves before exec</b>:
/// <c>echo $$ &gt; &lt;inst&gt;/cgroup.procs ; cd &lt;launchdir&gt; ; exec &lt;exe&gt; &lt;args&gt; &lt; fifo &gt;&gt; log</c>.
/// Because the daemon already lives in <c>kgsm.slice/&lt;supervisor&gt;</c>, the launcher is born
/// in-slice and the move to the instance cgroup is an allowed intra-slice migration. Doing the
/// move <em>in the child, before the game exists</em> is the load-bearing detail: every process the
/// game forks is then born inside the instance cgroup, so nothing escapes <c>cgroup.events</c>
/// liveness or <c>cgroup.kill</c> teardown. (Spawn-then-write-cgroup.procs would migrate only the
/// leader and leak early children — a containment bug.)
/// </para>
/// <para>
/// The old native path kept the FIFO open with a <c>tail -f /dev/null --pid=…</c> outside the
/// cgroup. Here the daemon itself holds an O_RDWR fd on the FIFO (in the supervisor cgroup, i.e.
/// outside the instance cgroup) — so there is no keepalive process to place, and that same fd is
/// the channel for stop/console commands.
/// </para>
/// <para>
/// <b>Log rotation on every fresh spawn (<see cref="RotateLogFile"/>).</b> Before forking, a
/// non-empty pre-existing <c>Instance.LogFile</c> is <c>mv</c>'d to a timestamped sibling — mirroring
/// kgsm's bash reference (<c>_rotate_log_file</c>) — so the launcher's <c>&gt;&gt; log</c> lands on a
/// fresh inode every run. <see cref="Spawn"/> is called ONLY from a genuine fresh spawn
/// (<c>InstanceSupervisor.TrySpawn</c> — manual start, boot respawn-of-dead, crash-restart); adopt
/// paths (<c>AdoptLive</c>/<c>AdoptFromHandoff</c>) never call it, so a still-writing live game's log
/// is never rotated out from under it. This closes a real honesty bug: without rotation, the log
/// accumulated across runs, so <c>NativeReadinessMatcher</c>'s late-attach whole-file scan (and, in
/// principle, player-presence matching) could see a stale prior-run line and fire immediately on the
/// very next start.
/// </para>
/// </summary>
internal sealed class SpawnEngine(CgroupManager cgroups, ILogger<SpawnEngine> logger)
{
    // $0=sh $1=cgroup.procs $2=launchdir $3=exe $4=args(unquoted: word-split+glob, faithful) $5=fifo $6=log
    private const string LauncherBody =
        "echo $$ > \"$1\" && cd \"$2\" && exec \"$3\" $4 < \"$5\" >> \"$6\" 2>&1";

    /// <summary>
    /// Create the instance cgroup + FIFO, then fork the game into it. Returns a live handle on
    /// success; throws <see cref="InvalidOperationException"/> on any misconfiguration or syscall
    /// failure (the caller maps that to a failed control result). Best-effort cleanup runs on the
    /// throwing paths so a failed spawn leaves no half-built cgroup/FIFO behind.
    /// </summary>
    public RunningInstance Spawn(Instance instance)
    {
        // Fail fast on a malformed/unpopulated descriptor with an honest, name-safe message — before any
        // cgroup/FIFO side effects. See ValidateSpawnable: an unpopulated spec (no name) reports "no
        // usable info" instead of a per-field error whose {name} prefix would be blank, the missing
        // field that surfaced as `start failed: : executable_file is empty` in a crash alert.
        string? invalid = ValidateSpawnable(instance);
        if (invalid is not null) throw new InvalidOperationException(invalid);

        string name = instance.Name;
        string launchDir = FirstNonEmpty(instance.LaunchDir, instance.WorkingDir, instance.InstallDir);
        string exe = instance.ExecutableFile;
        string fifo = instance.SocketFile;
        string log = instance.LogFile;

        if (!Directory.Exists(launchDir)) throw new InvalidOperationException($"{name}: launch dir missing: {launchDir}");

        // Faithful to the bash native path's security gate: reject command-substitution patterns.
        string rawArgs = instance.ExecutableArguments ?? string.Empty;
        if (rawArgs.Contains("$(", StringComparison.Ordinal) || rawArgs.Contains('`'))
            throw new InvalidOperationException($"{name}: executable_arguments contains command-substitution patterns");
        // envsubst over BOTH the instance_* vars (KGSM exports these in the management script's
        // env, so blueprints reference e.g. $instance_install_dir in their args) and the process
        // environment. Resolving only against the daemon's env would silently blank $instance_*.
        string args = ExpandEnvironment(rawArgs, BuildVarResolver(instance));

        string? logDir = Path.GetDirectoryName(log);
        if (!string.IsNullOrEmpty(logDir))
            Directory.CreateDirectory(logDir);

        // 0. Rotate a PRIOR run's log out of the way so this fresh spawn starts a brand-new inode at
        //    `log` — see RotateLogFile for the full rationale (fixes the Increment-9 divergence noted
        //    in PLAN.md: a stale ready/player-presence line surviving into the 2nd+ run). Safe here
        //    because Spawn() is the sole fresh-spawn entry point (InstanceSupervisor.TrySpawn is its
        //    only caller, for manual start / boot respawn-of-dead / crash-restart) — adopt
        //    (AdoptLive/AdoptFromHandoff) never calls Spawn, so a still-writing live game's log is
        //    never touched.
        RotateLogFile(log);

        // 1. Instance cgroup (mkdir under the delegated base).
        if (!cgroups.Create(name))
            throw new InvalidOperationException($"{name}: failed to create cgroup {cgroups.PathFor(name)}");
        string cgProcs = Path.Combine(cgroups.PathFor(name), "cgroup.procs");

        // 1b. Apply resource caps from instance config (Phase 2). Written before the game is born so a
        //     memory.max cap bounds it from its first allocation; cpu.weight would re-apply live anyway.
        if (instance.CpuPriority is { } priority && priority != "normal")
            cgroups.SetCpuWeight(name, CgroupManager.CpuWeightFor(priority));
        if (instance.MemoryCapMb is { } capMb and > 0)
            cgroups.SetMemoryMax(name, capMb);

        // 2. Fresh FIFO for stdin.
        try { if (File.Exists(fifo)) File.Delete(fifo); } catch { /* recreated below */ }
        if (NativeMethods.mkfifo(fifo, Convert.ToUInt32("600", 8)) != 0)
        {
            int err = Errno();
            cgroups.Remove(name);
            throw new InvalidOperationException($"{name}: mkfifo {fifo} failed (errno {err})");
        }

        // 3. Hold the FIFO write-end open from the daemon (O_RDWR: non-blocking open, EPIPE not
        //    SIGPIPE on a dead reader; O_CLOEXEC so no spawned game inherits this fd).
        int fd = NativeMethods.open(fifo, NativeMethods.O_RDWR | NativeMethods.O_CLOEXEC, 0);
        if (fd < 0)
        {
            int err = Errno();
            TryDeleteFifo(fifo);
            cgroups.Remove(name);
            throw new InvalidOperationException($"{name}: open FIFO {fifo} failed (errno {err})");
        }

        // 4. Fork the launcher. It inherits the daemon's (supervisor) cgroup at fork, then moves
        //    itself into the instance cgroup before exec.
        var psi = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(LauncherBody);
        psi.ArgumentList.Add("sh");        // $0
        psi.ArgumentList.Add(cgProcs);     // $1
        psi.ArgumentList.Add(launchDir);   // $2
        psi.ArgumentList.Add(exe);         // $3
        psi.ArgumentList.Add(args);        // $4
        psi.ArgumentList.Add(fifo);        // $5
        psi.ArgumentList.Add(log);         // $6

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            NativeMethods.close(fd);
            TryDeleteFifo(fifo);
            cgroups.Remove(name);
            throw new InvalidOperationException($"{name}: failed to start launcher: {ex.Message}", ex);
        }

        logger.LogInformation("spawned {Instance}: pid {Pid} -> {Cgroup}", name, proc.Id, cgroups.PathFor(name));
        return new RunningInstance(
            name, proc, fd, fifo, instance.StopCommand, instance.StopCommandTimeoutSeconds, logger);
    }

    /// <summary>
    /// Re-open an EXISTING instance FIFO for writing (O_RDWR, O_CLOEXEC) — the command channel for an
    /// ADOPTED instance after a daemon bounce. Returns the fd, or -1 if the node is gone/unopenable.
    /// Deliberately does NOT create it: the game still holds the read end of the surviving node, so a
    /// fresh <c>mkfifo</c> would be a different inode the game never reads. O_RDWR matches <see cref="Spawn"/>
    /// so the reader never EOFs.
    /// </summary>
    public int ReopenFifo(string fifoPath)
    {
        if (string.IsNullOrEmpty(fifoPath) || !File.Exists(fifoPath))
            return -1;
        int fd = NativeMethods.open(fifoPath, NativeMethods.O_RDWR | NativeMethods.O_CLOEXEC, 0);
        if (fd < 0)
            logger.LogWarning("re-open FIFO {Fifo} failed (errno {Err})", fifoPath, Errno());
        return fd;
    }

    private static string FirstNonEmpty(params string[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? string.Empty;

    /// <summary>
    /// Validate that a resolved instance descriptor is spawnable, returning an honest, name-safe error
    /// message or <c>null</c> when it is fully populated. Pure (no I/O) so the spawn path can fail fast
    /// before any cgroup/FIFO side effect, and so it is unit-testable without a live cgroup.
    /// <para>
    /// The empty-descriptor case is first and deliberate: a spec that never resolved (no name — e.g.
    /// kgsm returned no usable info) reports that plainly, rather than a per-field message whose
    /// <c>{name}</c> prefix would be blank. That blank prefix was the missing field behind the crash
    /// alert reading <c>start failed: : executable_file is empty</c>. Every later message below is
    /// reached only with a non-empty name, so a failure reason always identifies the instance.
    /// </para>
    /// </summary>
    internal static string? ValidateSpawnable(Instance instance)
    {
        string name = instance.Name;
        if (string.IsNullOrEmpty(name))
            return "instance descriptor is empty (kgsm returned no usable info)";
        if (string.IsNullOrEmpty(instance.ExecutableFile))
            return $"{name}: executable_file is empty";
        if (string.IsNullOrEmpty(instance.SocketFile))
            return $"{name}: socket_file is empty";
        if (string.IsNullOrEmpty(instance.LogFile))
            return $"{name}: log_file is empty";
        if (string.IsNullOrEmpty(FirstNonEmpty(instance.LaunchDir, instance.WorkingDir, instance.InstallDir)))
            return $"{name}: no launch/working/install dir";
        return null;
    }

    private static void TryDeleteFifo(string fifo)
    {
        try { if (File.Exists(fifo)) File.Delete(fifo); } catch { /* best effort */ }
    }

    /// <summary>
    /// Rotate a prior run's log out of the way before a fresh spawn, so the game's stdout append
    /// (<c>&gt;&gt; log</c> in <see cref="LauncherBody"/>) creates a BRAND-NEW inode at <paramref name="logPath"/>
    /// instead of resuming an old one. Mirrors kgsm's bash reference (<c>_rotate_log_file</c> in
    /// <c>manage.native.d/10-logging.sh</c>): <c>mv</c> the existing file to a timestamped sibling — a
    /// rename, never an in-place truncate — because a genuinely fresh inode is what
    /// <see cref="EventChannelTail"/>'s inode-keyed rotation check needs to fire
    /// <c>LastReadResetSession</c> cleanly on the very next read (an in-place truncate only helps via
    /// its weaker same-inode "shrink" safety net).
    /// <para>
    /// <b>Why this matters (Increment 9 divergence, tracked in PLAN.md):</b> before this fix,
    /// <see cref="Spawn"/> appended to the same log path forever. <c>NativeReadinessMatcher.MatchesExistingContent</c>
    /// does a one-shot WHOLE-FILE scan on every fresh-spawn start edge to catch a late attach — on the
    /// 2nd+ start of an instance that scan re-read the PREVIOUS run's already-logged ready line and
    /// fired <c>instance-ready</c> immediately, collapsing the honest "Starting" window. Rotating the
    /// log on every fresh spawn means the whole-file scan (and the player-presence tail) can only ever
    /// see the CURRENT run's content.
    /// </para>
    /// <para>
    /// Best-effort and non-fatal: absent/empty file (first-ever spawn, or an already-rotated-clean
    /// state) is a silent no-op; a failed rotate (permissions, races) is logged and swallowed — an
    /// un-rotated log is a smaller regression than refusing to start the game over housekeeping. A
    /// same-second collision with a previous rotated filename falls back to a tick-suffixed name so it
    /// never silently drops the prior run's content.
    /// </para>
    /// </summary>
    internal void RotateLogFile(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length == 0)
                return; // nothing worth rotating — first-ever spawn, or already a fresh/empty file

            string dir = info.DirectoryName ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(logPath);
            string ext = Path.GetExtension(logPath); // includes the leading '.', or "" if none
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
            string rotated = Path.Combine(dir, $"{stem}.{timestamp}{ext}");

            if (File.Exists(rotated))
                rotated = Path.Combine(dir, $"{stem}.{timestamp}.{DateTime.UtcNow.Ticks}{ext}");

            File.Move(logPath, rotated);
            logger.LogInformation("rotated prior-run log {Log} -> {Rotated} (fresh spawn)", logPath, rotated);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "failed to rotate log {Log} before spawn — continuing without rotation (a stale prior-run " +
                "line may linger in the log)", logPath);
        }
    }

    private static int Errno() => System.Runtime.InteropServices.Marshal.GetLastPInvokeError();

    /// <summary>
    /// The <c>instance_*</c> variables KGSM exports into the management script's environment, mapped
    /// from the <see cref="Instance"/> — so a blueprint's <c>$instance_install_dir</c> resolves the
    /// same way the bash native path's envsubst resolves it. Falls back to the process environment
    /// for everything else (PATH/HOME/etc.), matching envsubst's full-environment expansion.
    /// </summary>
    internal static Func<string, string?> BuildVarResolver(Instance i)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instance_name"] = i.Name,
            ["instance_install_dir"] = i.InstallDir,
            ["instance_working_dir"] = i.WorkingDir,
            ["instance_launch_dir"] = i.LaunchDir,
            ["instance_saves_dir"] = i.SavesDir,
            ["instance_backups_dir"] = i.BackupsDir,
            ["instance_logs_dir"] = i.LogsDir,
            ["instance_temp_dir"] = i.TempDir,
            ["instance_level_name"] = i.LevelName,
            ["instance_blueprint"] = i.Blueprint,
            ["instance_ports"] = i.Ports.ToUfwSpec(),
            ["instance_executable_file"] = i.ExecutableFile,
            ["instance_executable_subdirectory"] = i.ExecutableSubdirectory,
        };
        return name => map.TryGetValue(name, out var v) ? v : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>Expand against the process environment only (used where there is no instance context).</summary>
    internal static string ExpandEnvironment(string input)
        => ExpandEnvironment(input, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Expand <c>$VAR</c> / <c>${VAR}</c> via <paramref name="resolve"/> — the envsubst step the bash
    /// native path runs before word-splitting. Unknown variables expand to empty (envsubst
    /// semantics). No command/arithmetic expansion; the result is then word-split + globbed by the
    /// launcher's unquoted <c>$4</c>.
    /// </summary>
    internal static string ExpandEnvironment(string input, Func<string, string?> resolve)
    {
        if (input.IndexOf('$') < 0)
            return input;

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c != '$') { sb.Append(c); i++; continue; }
            if (i + 1 >= input.Length) { sb.Append('$'); break; }

            char n = input[i + 1];
            if (n == '{')
            {
                int close = input.IndexOf('}', i + 2);
                if (close < 0) { sb.Append(input, i, input.Length - i); break; }
                string varName = input.Substring(i + 2, close - (i + 2));
                sb.Append(resolve(varName) ?? string.Empty);
                i = close + 1;
            }
            else if (n == '_' || char.IsLetter(n))
            {
                int j = i + 1;
                while (j < input.Length && (input[j] == '_' || char.IsLetterOrDigit(input[j])))
                    j++;
                string varName = input.Substring(i + 1, j - (i + 1));
                sb.Append(resolve(varName) ?? string.Empty);
                i = j;
            }
            else
            {
                sb.Append('$');
                i++;
            }
        }
        return sb.ToString();
    }
}
