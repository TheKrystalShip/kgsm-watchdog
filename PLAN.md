# kgsm-watchdog — Design

**Stack:** .NET 10, Native AOT. How the code is put together: `docs/internals.md`. The operator's view:
`README.md`. The daemon's role in the engine: `../kgsm-docs/systems/engine/watchdog.md`.

> The resident **KGSM supervisor daemon**. It owns the `kgsm.slice` cgroup, spawns
> native game-server instances *into* per-instance cgroups, and supervises them
> (crash detection + restart). CLI / kgsm-lib / the Discord bot are **thin
> clients** that issue lifecycle commands over a unix socket. This deliberately
> breaks KGSM's historical "stateless / no warm process" rule — accepted
> eyes-open, because the daemon **is** the watchdog the project always wanted.
>
> **Logging** follows the ecosystem convention (`../kgsm-docs/reference/logging-convention.md`):
> `Microsoft.Extensions.Logging` → `AddSystemdConsole()` (journald `<N>` priority prefix),
> levels from `kgsm-watchdog.settings.json` `Logging` + env (`Logging__LogLevel__Default`, default
> `Information`). The settings file is `kgsm-watchdog.settings.json` (collision-proof across the
> ecosystem's many `appsettings.json`-shipping services) and is loaded explicitly from
> `AppContext.BaseDirectory` — `CreateSlimBuilder` under systemd leaves the content root at `/`, so the
> framework's default discovery finds nothing; `deploy.sh` installs it beside the binary. The
> `CreateSlimBuilder` host binds the `Logging` section via `AddConfiguration`. `"Microsoft.AspNetCore":
> "Warning"` there silences ~5-Info-lines-per-request chatter that otherwise floods journald (rate-limiting
> useful lines) and allocates on every poll.
>
> **Idle memory** — see `MemoryTrimmer`: the ~50 MB idle floor is genuine (≈30 MB live
> ASP.NET-Core/AOT managed heap with committed == live + ~13 MB binary + native; GC knobs can't shrink it).
> The trimmer targets *creep*: a Workstation-GC daemon never GCs at idle, so polling + 1 Hz ticks ratchet RSS up
> unbounded (56 → 100 MB / 40 min) — `MemoryTrimmer` re-trims (compacting gen-2 + `malloc_trim`) every 120 s
> when the working set grew ≥ 8 MB since the last trim, capping it.

---

## 1. Why this exists

Game-server instances that don't go through systemd get no crash protection. The
fix is native cgroup v2 supervision (see `../kgsm/docs/specs/cgroup-supervision-plan.md`,
the bash-side foundation). But a hard constraint surfaced: **entering `kgsm.slice` needs
either root or a spawner already inside it** (cgroup-v2 delegation containment —
migrating a process needs write on the source/dest *common ancestor*). And KGSM
must run **headless** — callable by the Discord bot with **zero per-operation
privilege escalation**, never prompting for a password mid-install.

The only architecture that satisfies headless + rootless-at-runtime + remote-caller
is a **resident daemon** placed in `kgsm.slice` once at boot. Since crash-restart
*also* needs a resident watcher, the supervisor and the watchdog are the **same
component**. The rejected alternative is a setuid helper.

## 2. The problems it solves — in one component

| Problem | How the daemon solves it |
|---|---|
| **Headless cgroup entry** | It lives in the delegated `supervisor` leaf; forks games (born in-cgroup) and moves them to their per-instance cgroup — a move it is allowed to make. Clients never touch cgroups or need privilege. |
| **Crash detection** | It's the **parent** of every game process *and* reads per-instance `cgroup.events` (`populated 0/1`) — race-free, child-inclusive. |
| **Intent (crash vs. deliberate stop)** | It holds **desired state**: it received the `start`/`stop` command, so it knows what *should* run. cgroups solve detection; desired-state solves intent. |
| **Restart** | Exponential backoff + flap rate-limit + give-up state, all in the one process holding the state. |

## 3. Architecture & invariants

- **Sibling of kgsm-monitor.** Mirrors its conventions: `.slnx` + `src/Watchdog`,
  `WebApplication.CreateSlimBuilder` → `kestrel.ListenUnixSocket`, `BackgroundService`
  + `PeriodicTimer`, reflection-free options, source-generated
  JSON context, hardened `Type=simple` systemd unit. **net10.0**, `PublishAot=true`,
  `InvariantGlobalization=true`, `IsAotCompatible=true`.
- **Integrates kgsm-lib, never shells `kgsm.sh`** (the dependency spine).
  `PackageReference TheKrystalShip.KGSM.Lib`. Reads instance
  spawn config via `IInstanceService.GetAll()` / `GetInstanceInfo(name)`; subscribes
  to `IEventService` for create/remove awareness. A new deserialized type must be
  registered in `KgsmJsonContext` (no reflection fallback).
- **The daemon ACTS; the monitor MEASURES.** kgsm-monitor stays consumer-agnostic,
  measure-only (`../kgsm-docs/systems/engine/spine.md`). The watchdog is a separate resident that *acts*
  (spawn/kill/restart). They may cooperate (the monitor can read the cgroups the
  watchdog creates), but the watchdog never becomes a metrics source.
- **Cgroup ops, inverted from the monitor.** The monitor *reads* cgroup v2 files;
  the watchdog *writes* them (mkdir child, write `cgroup.procs`, write `cgroup.kill`,
  read `cgroup.events`) — a C# port of `kgsm/core/cgroup.sh` semantics.
- **Never fabricate state.** A status is measured (cgroup/`/proc`) or "unknown",
  never invented.
- **Naming:** project `kgsm-watchdog`; namespace `TheKrystalShip.KGSM.Watchdog`;
  assembly `kgsm-watchdog`; config section `Watchdog` (env prefix `Watchdog__`); control socket default
  `/run/kgsm-watchdog/control.sock`.

## 4. Boot & privilege model (the load-bearing sequence)

The host is **systemd-only**, and the model is **systemd cgroup delegation**.

The daemon runs as the KGSM user from the start — **no root step, no privilege drop**.
systemd does the privileged cgroup setup via the unit (`User=kgsm`, `Slice=kgsm.slice`,
`Delegate=yes`): it creates `kgsm.slice` + `kgsm.slice/kgsm-watchdog.service`, enables
the controllers on the slice, and **chowns the service subtree to the user**. The boot
sequence is then just (`CgroupBootstrap`, `CgroupDiscovery`):

1. **Discover** the delegated base from `/proc/self/cgroup` — the daemon's own service
   cgroup `kgsm.slice/kgsm-watchdog.service` (or its parent, when a hot-swap
   re-exec has already moved it into the supervisor leaf). Never a hardcoded `kgsm.slice`.
2. **Enter** the supervisor leaf: create `<base>/supervisor` and move into it — cgroup v2
   forbids enabling `subtree_control` on a cgroup that holds processes, and the daemon is
   born in the base.
3. **Enable controllers** on the (now process-free) base so each per-instance child
   inherits them.

After bootstrap: forks are born in `<base>/supervisor`; per-instance moves to
`<base>/<inst>` (= `kgsm.slice/kgsm-watchdog.service/<inst>`) are unprivileged. **No
per-operation escalation, ever.**

> **Fully rootless — and enforced.** The daemon and every game it spawns run as the
> unprivileged `User=` with **zero capabilities** (cgroup management is just owner-writes
> inside the user-owned delegated subtree). The unit locks this in: `NoNewPrivileges=yes`
> + `CapabilityBoundingSet=` (empty), so neither the daemon nor a spawned game can ever
> gain a privilege. The only privileged actor is systemd (PID 1) doing the one-time
> delegation when it starts the unit — standard `User=`+`Delegate=yes`, not the daemon
> escalating.

> **`KillMode=process` is load-bearing.** Games live *under* the service cgroup, so the
> default `control-group` kill would SIGKILL them on every `systemctl stop/restart`.
> `process` kills only the daemon; games keep running and the next daemon re-adopts them.
>
> Caveat (cgroup v2, inherent): a daemon **cold restart/crash** while a game survives
> re-charges that game's `memory.current` from zero — systemd clears the service cgroup's
> `subtree_control` on a fresh start, momentarily detaching the memory controller, and
> already-resident pages are not retroactively re-charged. It self-heals on the next game
> restart. **Not** triggered by the normal paths: hot-swap (execve, no stop) preserves the
> charge, and a host reboot respawns games fresh (charged from birth).

> **Why children live under the *service* cgroup, not as siblings under the slice.**
> systemd reconciles a slice's OWN `cgroup.subtree_control` on every `daemon-reload`
> (stripping the controllers off cgroups it does not manage). A `kgsm.slice/<inst>`
> sibling layout would wipe `memory`/`pids`/`io` off every instance
> — `cpu.stat` is unconditional so CPU survives, but `memory.current` vanishes and the
> monitor reads **0**. systemd leaves the delegated subtree *below
> the service* untouched, so the controllers persist. The per-instance path is coupled
> to the unit name; kgsm surfaces the matching `cgroup_path` (`config_cgroup_base_name
> = kgsm.slice/kgsm-watchdog.service`) in `Instance.CgroupPath`, and the monitor follows it.

## 5. Control protocol (clients → daemon)

A unix socket (FS-perms are the security boundary, exactly like the monitor —
owner/group gated; network-facing auth like Discord-OAuth lives in the surfaces
*above*, not here). HTTP/1.1 over the socket via Kestrel with source-gen JSON,
so kgsm-lib has a tiny typed client that the bot and CLI reuse. The verbs and their
status codes are in `docs/internals.md`; the contract is
`../kgsm-docs/systems/engine/watchdog.md`.

- `start <inst>` — read config via kgsm-lib, spawn into the instance's cgroup, record
  desired-state = running.
- `stop <inst>` — desired-state = stopped, graceful stop-command → bounded drain →
  `cgroup.kill` → remove.
- `status <inst>` / `list` — report desired vs. actual (populated), restart counts.
- `GET /health` — the **unified ecosystem health probe**. Carries *readiness*: `200` only
  when in-slice and able to spawn; `503` + `{ready,detail}` reason when up-but-unable; no
  answer when down. Consumers treat anything but `200` as "unavailable — retry until `200`".

## 6. Project structure (mirror kgsm-monitor)

```
kgsm-watchdog/
├── kgsm-watchdog.slnx
├── src/Watchdog/
│   ├── Watchdog.csproj            (net10.0, PublishAot, refs KGSM.Lib)
│   ├── Program.cs                 (CreateSlimBuilder, UDS, hosted services)
│   ├── WatchdogSettings.cs        (the bound Watchdog section, 1:1 with the settings file)
│   ├── WatchdogOptions.cs         (the validated form — FromSettings)
│   ├── Model/
│   │   ├── Contracts.cs           (control request/response records)
│   │   └── WatchdogJsonContext.cs (source-gen)
│   ├── Cgroup/
│   │   ├── CgroupManager.cs       (create/attach/kill/remove/events — C# port of core/cgroup.sh)
│   │   └── CgroupBootstrap.cs     (discover the delegated base, enter the supervisor leaf, enable controllers)
│   ├── Supervision/
│   │   ├── InstanceSupervisor.cs  (desired-state table, the registry)
│   │   ├── SpawnEngine.cs         (fork+exec into cgroup; FIFO-stdin / logfile-stdout)
│   │   ├── CrashWatcher.cs        (poll cgroup.events; restart policy)
│   │   └── BackoffPolicy.cs       (exponential + flap limit + give-up)
│   └── Control/
│       └── ControlEndpoints.cs    (start/stop/status/list)
├── tests/Watchdog.Tests/
└── deploy/
    └── kgsm-watchdog.service      (the ONE canonical unit: User=kgsm,
                                    Slice=kgsm.slice, Delegate=yes)
```

---

## 7. Capabilities

Each capability below is built; the code-level description is `docs/internals.md`.

- **Spawn.** The daemon forks the game through a self-move `/bin/sh -c` launcher
  (`echo $$ > <inst>/cgroup.procs` before `exec`), so every child is born inside the cgroup. Stdin is the
  instance FIFO (`Instance.SocketFile`), held `O_RDWR` by the daemon with no keepalive process; stdout
  appends to `Instance.LogFile`. The launcher replicates the bash native path's `$(`/backtick reject,
  `envsubst` and unquoted word-split, and `$instance_*` variables in `executable_arguments` resolve from
  the `Instance`. Stop is graceful stop-command, bounded drain, then `cgroup.kill`. Scope is
  native-standalone only; systemd and container instances are no-ops.
- **Log rotation on a fresh spawn.** `SpawnEngine.RotateLogFile` renames a non-empty log to a timestamped
  sibling before any cgroup or FIFO side effect, mirroring the bash reference, so the launcher's `>> log`
  lands on a fresh inode. Only `Spawn` rotates; the adopt paths (hot-swap re-attach and cold-restart
  re-attach) never call it, so a still-writing game's log is untouched by construction. The fresh inode
  is the clean re-arm signal for the readiness latch and the presence session map, and it keeps the
  readiness late-attach scan from seeing a prior run's ready line.
- **Supervision.** `CrashWatcher` polls `cgroup.events` at 1 Hz; `populated` going to 0 while the
  instance is desired-running restarts it with exponential backoff and a give-up at `MAX_RETRIES`
  (`phase=failed`), the streak resetting after `STABILITY_SEC`. The policy is `always` by default and
  `on-failure` leaves clean code-0 exits stopped; the `on-failure` clean-exit branch is exercised only by
  unit tests, since the live runs saw non-zero exits.
- **Clients.** kgsm-lib's `IWatchdogClient` is the typed client. `kgsm start|stop|restart|is-active`
  for native-standalone instances route to the daemon when its socket answers `/health` with `200`
  (`commands/handlers/watchdog.sh`): a connection failure is an error and never a silent double-spawn,
  `is-active` routes too because the daemon writes no PID file, and `stop` of an instance the daemon does
  not track falls through to the direct PID-file stop. The bot's `/supervision` command reads the
  daemon's state; start, stop and restart reach the daemon through `kgsm.sh`.
- **Boot persistence.** The enabled-for-boot set lives in `desired-state.json`; `kgsm autostart` writes it
  and `StartupRestorer` re-adopts a still-live cgroup or respawns a dead one at startup. The runtime
  start/stop axis is independent of the boot enable/disable axis. Systemd is used only to boot the
  daemon: there is no per-instance unit, `service.tp` or `lifecycle_manager=systemd` path.
- **Daemon-restart resilience.** Supervision runs off the cgroup, so it survives a daemon bounce, and the
  daemon re-adopts a live cgroup whether or not it is enabled. Console and graceful stop survive through
  hot-swap: `systemctl reload` sends SIGHUP and the daemon `execv`s the new image in place, carrying each
  game's stdin-FIFO fd across with the same PID. `--selfcheck` on the new binary is a safety gate that
  aborts a swap to a broken image and leaves the running one untouched; the abort is logged, not emitted
  as a kgsm event. `supervision-state.json` rehydrates restart counters and the give-up latch after an
  unclean death. A hot-swap-adopted instance is held `Process=null` while the game stays the daemon's
  child, so each adopted pid is tracked (`_reapable`) and reaped on the 1 Hz tick, which keeps an exited
  game from lingering as a zombie. The root-boot unit's `RuntimeDirectory=` is absent: the unit's owner
  would otherwise be re-chowned on reload and the re-exec'd daemon could not bind the control socket.
- **Systemd delegation.** The boot sequence in section 4 is what keeps the `memory`, `pids` and `io`
  controllers on per-instance cgroups across `daemon-reload`.
- **Readiness.** `instance-ready` is distinct from `instance-started`. `NativePlayerPresenceIngester`
  doubles as the readiness ingester, using `NativeReadinessMatcher` (a 100 ms ReDoS guard) over
  `Instance.StartupSuccessRegex`. The authoritative re-arm signal is the instance cgroup going from
  not-populated to populated, which holds across every spawn path with no change to `InstanceSupervisor`;
  a fresh log inode re-arms defensively too. An empty pattern is the honest immediate fallback (ready =
  observed started, no fabricated delay); an invalid non-empty pattern disables readiness and warns,
  never substituting the immediate rule. A one-shot whole-file scan on the start edge
  (`MatchesExistingContent`) catches a ready line logged before the edge was observed. An instance with a
  readiness pattern and no player patterns is not skipped. The emit is
  `events.EmitWithProvenance("instance-ready", "system", "system", [name])`.

## 8. Not built

- **Port-based readiness.** Only log-pattern readiness and the immediate fallback exist.
- **A `starting` phase in the watchdog's own state model.** The Control Panel API owns that state; the
  watchdog's job is the correct `instance-ready` emit.
- **Live re-validation of log rotation** on a real stop/start cycle of a Factorio instance, after the
  daemon is redeployed with it.

## 9. Open questions / risks

- **Spawn ownership detail.** The daemon owns fork+exec (so the game is its in-slice
  child) and replicates the FIFO/log wiring of `manage.native.d`; no keepalive process sits in the cgroup,
  so `populated` clears when the game exits.
- **Graceful-stop vs. `cgroup.kill` ordering** and the drain timeout
  (`stop_command_timeout_seconds`) reuse KGSM's existing semantics.
- **Control-socket authority.** The socket can start/kill game servers, a privileged
  surface gated by FS perms (owner/group), the same model as the monitor. There is no in-daemon
  authn; that is the surfaces' job.
- **Container instances** stay Docker-managed (Docker owns their cgroup + restart);
  the watchdog supervises **native** instances only and no-ops on container/systemd kinds.
- **Cgroup-base coordination with kgsm.** The base is `kgsm.slice/kgsm-watchdog.service`, coupled to the
  unit name and discovered at runtime; kgsm surfaces the same per-instance path in `Instance.CgroupPath`
  or the monitor samples the wrong directory. The monitor is downstream of `CgroupPath` and needs no change.

## 10. Source-of-truth pointers

- cgroup foundation (bash side) → `../kgsm/docs/specs/cgroup-supervision-plan.md`
- monitor conventions to mirror → `../kgsm-monitor/PLAN.md`, `src/Monitor/*`
- kgsm-lib consumable API → `../kgsm-lib/CLAUDE.md`, `Core/Models/Instance.cs`,
  `Core/Interfaces/*`, `Json/KgsmJsonContext.cs`
- ecosystem topology + invariants → `../kgsm-docs/architecture/README.md`; the daemon's role →
  `../kgsm-docs/systems/engine/watchdog.md`
- how the code is put together → `docs/internals.md`
