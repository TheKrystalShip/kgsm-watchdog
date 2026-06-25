namespace TheKrystalShip.KGSM.Watchdog.Cgroup;

/// <summary>
/// Resolves the daemon's <b>own</b> delegated cgroup base from <c>/proc/self/cgroup</c> — the
/// keystone of the systemd-delegation model (PLAN Increment 8). Under a systemd unit with
/// <c>Delegate=yes</c> the service is placed at <c>&lt;mount&gt;/kgsm.slice/kgsm-watchdog.service</c>
/// and that subtree is handed to the service user, so the base is <em>discovered</em>, never a
/// hardcoded <c>kgsm.slice</c>. Per-instance cgroups are then created as children of this base
/// (<c>&lt;base&gt;/&lt;inst&gt;</c>) — <b>not</b> as siblings of it under the slice. systemd reconciles a
/// slice's own <c>cgroup.subtree_control</c> (stripping controllers off siblings — the bug that made
/// per-server memory metrics read 0), but it leaves the delegated subtree below the service untouched.
/// </summary>
internal static class CgroupDiscovery
{
    /// <summary>
    /// The cgroup v2 unified path from a <c>/proc/self/cgroup</c> body — the single
    /// <c>0::&lt;path&gt;</c> line. Returns the path (always begins with <c>/</c>, or the literal
    /// <c>/</c> for the root cgroup), or <c>null</c> when there is no v2 line (cgroup v1/hybrid).
    /// Pure + golden-file testable.
    /// </summary>
    internal static string? ParseUnifiedPath(string procSelfCgroup)
    {
        foreach (var line in procSelfCgroup.Split('\n'))
        {
            // cgroup v2 unified hierarchy is always the "0::" entry (controller field empty).
            if (!line.StartsWith("0::", StringComparison.Ordinal))
                continue;
            string rel = line.AsSpan(3).Trim().ToString();
            return rel.Length == 0 ? "/" : rel;
        }
        return null;
    }

    /// <summary>
    /// Compute the delegated base directory from the daemon's current cgroup path and the
    /// supervisor leaf name. On a fresh boot the daemon is born in its service cgroup, so that
    /// <em>is</em> the base. After a hot-swap re-exec (Inc 7) the daemon has already moved into
    /// <c>&lt;base&gt;/&lt;supervisorLeaf&gt;</c>, so the base is its <b>parent</b> — handled here so
    /// re-running bootstrap resolves the same base both times. Pure + testable.
    /// </summary>
    internal static string ResolveBaseFromSelf(string mountPoint, string unifiedPath, string supervisorLeaf)
    {
        string abs = unifiedPath == "/" ? mountPoint : mountPoint + unifiedPath;
        // Already inside the supervisor leaf (re-exec) -> the base is one level up.
        if (string.Equals(Path.GetFileName(abs), supervisorLeaf, StringComparison.Ordinal))
            abs = Path.GetDirectoryName(abs) ?? abs;
        return abs;
    }

    /// <summary>
    /// Read <c>/proc/self/cgroup</c> and return the absolute delegated base, or <c>null</c> when the
    /// file is unreadable or the host is not cgroup v2 (callers then fall back to the configured base).
    /// </summary>
    internal static string? ResolveDelegatedBase(string mountPoint, string supervisorLeaf)
    {
        string content;
        try { content = File.ReadAllText("/proc/self/cgroup"); }
        catch { return null; }

        string? rel = ParseUnifiedPath(content);
        return rel is null ? null : ResolveBaseFromSelf(mountPoint, rel, supervisorLeaf);
    }
}
