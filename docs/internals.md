# Internals

How the daemon is put together: the control plane's behaviour, the supervisor, the boot and privilege
sequence, the spawn path, the hosted services, hot-swap and the state files. The design rationale is in
`../PLAN.md`; the operator's view is `../README.md`; the engine-level model is
`../../kgsm-docs/systems/engine/watchdog.md`.

## Control plane (HTTP/1.1 over the unix socket)

File permissions on the socket are the only security boundary; authentication lives in the surfaces
above. There is no TCP port.

```bash
S=/run/kgsm-watchdog/control.sock
curl --unix-socket $S http://x/health            # readiness: 200 ready / 503 + {ready,detail}
curl --unix-socket $S -X POST http://x/start/my-server   # 200 started / 507 no room / 409 failed
curl --unix-socket $S -X POST 'http://x/maintenance/begin/my-server?origin=scheduler'  # park / release
curl --unix-socket $S http://x/status/my-server  # also /list, /stop/{n}, /version
curl --unix-socket $S http://x/players           # every instance: its detection and who is connected
```

- `/health` is the unified ecosystem probe: `200` only when the daemon is in its slice and able to
  spawn. Anything else means "unavailable, retry".
- `start` and `restart` separate a **capacity refusal (`507`)** from a failure (`409`) on the status
  line, because the kgsm CLI's transport reads the code and discards the body. The body says the same
  thing in `ActionResult.refusal` for callers that read JSON, and kgsm maps `507` to
  `EC_INSUFFICIENT_MEMORY`, so both engine halves refuse with one code.
- `start` and `restart` refuse an instance locked by an instance transfer: `409` with
  `refusal: "instance_locked"`. The lock is the instance config's `transfer_lock` key, read through
  `IInstanceService.GetInstanceConfigValue` on every call and never cached. `InstanceSupervisor.StartAsync`
  checks it before taking its gate and `RestartAsync` before its stop, so a restart of a locked instance
  stops nothing. A config the engine cannot read is a plain failure (`409`, no refusal), never a pass.
- `POST /maintenance/begin|end/{name}?origin=<leaf>` is the park a leaf runs disruptive work behind: the
  normal graceful drain with desired-state left `running`. Crash-restart is suppressed by the phase
  (`maintenance`), and the failure streak and give-up latch come out as they went in. The work happens
  between the two calls and never inside the supervisor's gate. `Watchdog__MaintenanceMaxMinutes`
  releases a park nothing else releases, and a park predating the daemon's start is released at boot, so
  a leaf that dies mid-sequence costs a window, never a server.
- `POST /start/{name}?force=true` is the only way past the capacity check: a person's override, carried
  from `kgsm start --force`. It still takes the reservation. Autostart, crash-restart and `/restart`
  never set it.
- The daemon supervises native-standalone instances only; it no-ops on systemd and container instances.

## The supervisor

- **`InstanceSupervisor` is the single decision point.** Every state transition, both the control verbs
  (`start`, `stop`) and the periodic `Reconcile()`, runs under one `SemaphoreSlim` gate, so a timer and
  an exit handler never race the same state. `CrashWatcher` is only the clock: a 1 Hz `PeriodicTimer`
  calling `Reconcile()`. Reconcile try-acquires the gate and skips the tick when a verb holds it, so it
  never blocks behind a graceful-stop drain. Reads (`status`, `list`) are lock-free over a
  `ConcurrentDictionary`.
- **Detection and intent are separate axes.** Detection is a cgroup emptying (`cgroup.events`
  populated to 0) while the instance is still desired-running: child-inclusive and race-free. Intent is
  `SupervisedInstance.DesiredRunning`, which a deliberate `stop` clears. The durable
  `SupervisedInstance` (keyed by name) outlives the live `RunningInstance`, so the restart counter and
  phase (`running`, `restart-pending`, `stopped`, `failed`) survive the down-window. Restart follows
  `BackoffPolicy`: exponential, giving up at `MAX_RETRIES` with `phase=failed`, the streak resetting
  after `STABILITY_SEC`. The default policy is `always`, because game exit codes are unreliable and many
  exit 0 on a crash; `on-failure` leaves clean code-0 exits stopped.

## Boot and privilege

systemd cgroup delegation provides everything; no step runs as root. systemd creates `kgsm.slice` and
`kgsm.slice/kgsm-watchdog.service`, enables controllers and chowns the service subtree to the user.
`CgroupBootstrap` then discovers the delegated base from `/proc/self/cgroup` (never a hardcoded
`kgsm.slice`), enters a `supervisor` leaf (cgroup v2 forbids `subtree_control` on a cgroup holding
processes), and enables controllers on the now-empty base. Per-instance cgroups are
`kgsm.slice/kgsm-watchdog.service/<inst>`, under the service and not siblings under the slice: systemd
reconciles a slice's own `subtree_control` on every `daemon-reload` and would strip `memory`, `pids` and
`io` off siblings, leaving their accounting files reading `0`. A bootstrap failure sets
`SupervisorState.Ready=false`, with a reason on `/start` and `/health`, rather than crashing.

`KillMode=process` is load-bearing. Games live under the service cgroup, so `control-group` would
SIGKILL them on every `systemctl stop` or `restart`. `process` kills only the daemon; games keep
running and the next daemon re-adopts them. A cold restart re-charges a surviving game's
`memory.current` from zero until its next restart, which is inherent to cgroup v2; hot-swap and host
reboot are unaffected.

## Spawn

`SpawnEngine` forks a `/bin/sh -c` launcher that runs `echo $$ > <inst>/cgroup.procs` before `exec`, so
the game and every child it forks are born inside the cgroup. The daemon holds the stdin FIFO
(`Instance.SocketFile`) open `O_RDWR` itself, with no keepalive process; stdout appends to
`Instance.LogFile`.

## Hosted services

- **Native supervision**, the acting role above.
- **Player-presence ingesters.** `PlayerPresenceIngester` (containers), `NativePlayerPresenceIngester`
  (native) and `RconPlayerPresencePoller` (games that log connects but not disconnects) are pure
  observers that re-emit player join and left as kgsm wire events (`origin=system`) into the shared
  `PlayerSessionStore`. They never shell docker and never supervise. Whether an instance is observed at
  all is `PlayerDetection`'s answer and only its: the ingesters gate on it and `GET /players` reports
  it. It spans log patterns, RCON (port, password, command) and whether each pattern compiles, so the
  endpoint hands the verdict out rather than leaving every surface to guess from the same config.
- **`UpnpReconciler`** restores router forwards the IGD dropped under a running instance. It runs on its
  own timer (`Watchdog__UpnpReconcileSeconds`, default 300, `0` off), because a sweep is a multi-second
  network round trip and the supervision loop holds the gate; it reads the instance table lock-free,
  like `/status`. A router that cannot be reached means the sweep does nothing: an unreadable
  redirection table is not evidence of an empty one. Doing nothing is still reported: every `upnpc` call
  goes through `UpnpService.RunAgainstRouterAsync`, which tells `UpnpRouterHealth` whether a router
  answered. Calls unanswered across 15 minutes mark the `upnp-router` component degraded, the first
  answer recovers it, and the sweep keeps listing while degraded so the recovery is observed. The same
  path retries a call whose discovery got no answer at the router's last description URL
  (`UpnpGatewayMemory`, `upnpc -u`).

## Hot-swap

`--version` and `--selfcheck` run in `Program.cs` before the host is built, so a deploy can interrogate
a freshly installed binary as a cheap subprocess without binding the socket, entering the slice or
touching cgroups. `HotSwapCoordinator` and `HotSwapSignalListener` bridge SIGHUP to an in-place
`execv`. SIGTERM is a normal shutdown. The `execv` inherits the live process's environment, so a
config-key rename cannot be hot-swapped: `--selfcheck` fails the required-knob check and the swap
aborts. Such a deploy uses `--cold`.

## State files

All resolve their location through `StatePathResolver`.

- `desired-state.json` (`Watchdog__StateFile`) is the boot and enable axis. `enable` and `disable` add
  and remove a name; on startup `StartupRestorer` re-adopts a still-live cgroup or respawns a dead one.
  It stores intent only: each spawn config is re-read from kgsm-lib, so no stale spec drifts. The
  runtime `start`/`stop` axis is separate.
- `supervision-state.json` (`SupervisionStateStore`) persists restart counters and the give-up latch so
  they survive any death, including OOM and SIGKILL.
- `run-history.json` (`RunHistoryStore`) records how each run ended, so a crash can be joined to the
  console file holding its output.
- `player-names.json` (`PlayerNameStore`) holds each instance's `accountId` to display name, which lets
  a join event carry a name on a game that prints one only on disconnect.
- `readiness-state.json` (`ReadinessStateStore`) records which run each instance was last announced
  ready for. `instance-ready` is a transition inferred from the daemon's first sight of a populated
  cgroup, and that inference repeats on every daemon start for a server that never stopped.
- `upnp-gateway` (`UpnpGatewayMemory`) is one line: the description URL the router last answered
  discovery from, persisted because the outage it serves can outlast a deploy.

## Measured, never invented

A cgroup read that fails is `CgroupPopulation.Unknown`, never empty: `IsPopulated` treats it as
occupied, since every act on emptiness (a crash, a purge, a spawn) destroys or duplicates something, and
the reconcile tick decides nothing on it. The daemon raises its soft open-file limit to the hard limit at
startup, because exhausting it is what makes every read fail; the spawn launcher puts games back to 1024.

A run's age is one of these measurements. `SupervisedInstance.SpawnedAt` is when this daemon took
charge (the grace window and stability reset are measured from it, so it restarts at every adoption),
while `RunStartedAt` is the leader's own start read from `/proc` via `ProcessStartClock`. `/status`,
`/runtimes` and the run ledger report the latter, and null when the leader cannot be read; reporting the
supervision clock as an uptime would date a days-old server to the last deploy.
