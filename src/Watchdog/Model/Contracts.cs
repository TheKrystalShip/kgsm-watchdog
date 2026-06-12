namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>Result of a control action (<c>start</c>/<c>stop</c>): which instance, success, and a reason.</summary>
public sealed record ActionResult(string Instance, bool Ok, string Message);

/// <summary>
/// Reported state of a supervised instance: the <em>desired</em> state the daemon holds vs. the
/// <em>actual</em> liveness measured from <c>cgroup.events</c>, plus the supervision phase and the
/// current restart-failure streak. Never fabricated — <c>populated</c> is read from the kernel, and
/// an instance the daemon does not track simply does not appear.
/// </summary>
public sealed record InstanceState(
    string Name,
    string Desired,       // "running" | "stopped"
    bool Populated,       // measured from cgroup.events
    int? Pid,             // the spawned leader PID, when known
    string CgroupPath,
    string Phase,         // "running" | "restart-pending" | "stopped" | "failed" | "unknown"
    int Restarts,         // consecutive-failure streak since last stability (0 when healthy)
    string Reason);       // last transition reason (e.g. "crashed (exit 139); restart in 2s")

/// <summary>Readiness of the supervisor itself: whether it is in-slice and able to spawn.</summary>
public sealed record ReadyState(bool Ready, string Detail);
