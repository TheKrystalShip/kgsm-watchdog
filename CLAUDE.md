# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> Workspace context lives in `../CLAUDE.md` (the `tks` umbrella) and the keystone
> `../system-architecture.md` — read those for the cross-repo dependency spine and ecosystem
> invariants. This file is kgsm-watchdog-specific. **`PLAN.md` is the as-built source of truth**
> (increment roadmap + what's live); `README.md` is the operator-facing summary.

## What this is

The resident **KGSM supervisor daemon** — the engine's *stateful* half, peer of `kgsm` (the
stateless bash CLI). It owns the `kgsm.slice` cgroup, spawns **native standalone** game-server
instances *into* per-instance cgroups, holds desired-state, and does crash-restart + boot-autostart.
CLI / kgsm-lib / the Discord bot are thin clients issuing lifecycle verbs over a unix socket, so
KGSM manages non-systemd servers headlessly with **zero per-operation privilege escalation**.
.NET 10, Native AOT — same shape as `kgsm-monitor`, but **the daemon ACTS where the monitor only
MEASURES** (it *writes* cgroup v2 files; the monitor reads them). It never becomes a metrics source.

## Build / test / publish

```bash
dotnet build  kgsm-watchdog.slnx                                    # note: .slnx, not .sln
dotnet test   kgsm-watchdog.slnx
dotnet test   kgsm-watchdog.slnx --filter "FullyQualifiedName~CgroupManagerTests"   # one class/test
dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64 # AOT — expect 0 IL2026/IL3050/ILC warnings
```

The AOT publish (or a clean `dotnet build`) must stay **0-warning**: any `IL2026`/`IL3050`/`ILC`
warning means something reaches for reflection and will fail at runtime, not compile time. This
project (and its kgsm-lib dependency) is `IsAotCompatible` with no reflection fallback.

> The test project hand-rolls `IInstanceService` fakes (e.g. `HotSwapCoordinatorTests.EmptyInstances`,
> `NativePlayerPresenceIngesterTests.FakeInstanceService`) that implement the *whole* interface, so a
> kgsm-lib bump that adds an interface member breaks test compilation until each fake gains the new
> member (the daemon source can build clean meanwhile). The kgsm-lib source is at `../kgsm-lib`.

## Deploy & live validation

This is a **live dev host** (see `../CLAUDE.md`): bounce the daemon freely; **prefer Factorio/Terraria**
for spawn tests. systemd-only, one canonical unit (`deploy/kgsm-watchdog.service`): `User=`/`Group=`
(the invoking user), `Slice=kgsm.slice`, `Delegate=yes`.

```bash
./deploy/setup.sh            # ONCE per host — asks for sudo; provisions and verifies the headless grant
./deploy/deploy.sh           # HOT-swap if running (zero-downtime SIGHUP re-exec), else COLD install
./deploy/deploy.sh --cold    # force stop → install → start
```

- **HOT** (default when the service is active): the new binary is `--selfcheck`'d, atomically
  `rename(2)`'d onto the live path, then `systemctl reload` (SIGHUP) makes the daemon `execv` the new
  image **in place — same PID** — carrying each game's stdin-FIFO fd across, so no supervised game
  restarts and no console EOF. Verified by a new `/version` + an **unchanged** `MainPID`.
- **`deploy.sh` needs no privilege at all.** `/opt/kgsm-watchdog` is yours (so installing a binary is
  a plain file write), the real unit lives in **user-owned** `/etc/kgsm-watchdog/systemd/` with
  `/etc/systemd/system/kgsm-watchdog.service` symlinked to it (so a unit change is also a plain file
  write), and the `systemctl` verbs — including the hot-swap `reload` — go through a polkit rule
  scoped to this project's units. `setup.sh` provisions all of that once, asking for sudo, and ends
  by verifying the grant with the same unprivileged calls `deploy.sh` makes. `deploy.sh` refuses
  **before building** with *"run `deploy/setup.sh`"* on an unprovisioned host. If some *other* op
  needs root, **stop and ask** — don't reintroduce `sudo` here.
- The three files in `deploy/` (`deploy-common.sh` + the two entry points) are self-contained: a
  standalone clone deploys with no other repo checked out. Every `kgsm-*` repo carries the same
  pattern, so what you learn here transfers.
- `deploy/validate-increment*.sh` are root-run end-to-end harnesses (persist → adopt → respawn → prune
  across real daemon restarts against a live game server). They are the regression net for boot/cgroup
  behavior that unit tests can't reach.

## Control plane (HTTP/1.1 over the unix socket)

FS perms on the socket are the only security boundary (auth lives in the surfaces above). No TCP port.

```bash
S=/run/kgsm-watchdog/control.sock
curl --unix-socket $S http://x/health            # readiness: 200 ready / 503 + {ready,detail}; /ready = deprecated alias
curl --unix-socket $S -X POST http://x/start/my-server
curl --unix-socket $S http://x/status/my-server  # ...also /list, /stop/{n}, /version
curl --unix-socket $S http://x/players           # every instance: its detection + who is connected
```

`/health` is the unified ecosystem probe — **`200` only when in-slice and able to spawn**; treat
anything else as "unavailable, retry". The daemon supervises native-standalone instances only; it
**no-ops on systemd/container** instances (owned by systemd / Docker).

## Architecture — the parts that span files

- **`InstanceSupervisor` is the brain and the single decision point.** Every state transition — both
  the control verbs (`start`/`stop`) *and* the periodic `Reconcile()` — lives here, under one
  `SemaphoreSlim` gate, so a timer and an exit-handler never race the same state (the classic
  supervisor bug). `CrashWatcher` is *just the clock*: a 1 Hz `PeriodicTimer` that calls
  `Reconcile()`. Reconcile **try-acquires** the gate and skips the tick if a verb holds it (never
  blocks behind a graceful-stop drain). Reads (`status`/`list`) are lock-free over a
  `ConcurrentDictionary`.
- **Detection vs. intent are separate axes.** *Detection* = a cgroup emptied (`cgroup.events`
  populated→0) while still desired-running — child-inclusive and race-free. *Intent* =
  `SupervisedInstance.DesiredRunning`, which a deliberate `stop` clears. The durable
  `SupervisedInstance` (keyed by name) **outlives** the live `RunningInstance` so the restart counter
  and phase (`running`/`restart-pending`/`stopped`/`failed`) survive the down-window. Restart =
  `BackoffPolicy` (exponential + give-up at `MAX_RETRIES` → `phase=failed`; streak resets after
  `STABILITY_SEC`). Default policy `always` (game exit codes are unreliable — many exit 0 on crash);
  `on-failure` leaves clean code-0 exits stopped.
- **Boot & privilege: systemd cgroup delegation, never a root step.** systemd creates `kgsm.slice` +
  `kgsm.slice/kgsm-watchdog.service`, enables controllers, and chowns the service subtree to the user.
  `CgroupBootstrap` then (1) **discovers** the delegated base from `/proc/self/cgroup` — never a
  hardcoded `kgsm.slice`; (2) **enters** a `supervisor` leaf (cgroup v2 forbids `subtree_control` on a
  cgroup holding processes); (3) **enables controllers** on the now-empty base. Per-instance cgroups
  are `kgsm.slice/kgsm-watchdog.service/<inst>` — **under the service, not siblings under the slice**:
  systemd reconciles a slice's own `subtree_control` on every `daemon-reload` and would strip
  `memory`/`pids`/`io` off siblings (the bug Inc 8 fixed — monitor read `memory.current`=0). Bootstrap
  failure sets `SupervisorState.Ready=false` (with a reason on `/start` and `/health`) rather than
  crashing.
- **`KillMode=process` is load-bearing.** Games live *under* the service cgroup, so `control-group`
  kill would SIGKILL them on every `systemctl stop/restart`. `process` kills only the daemon; games
  keep running and the next daemon **re-adopts** them. (Caveat: a daemon *cold* restart re-charges a
  surviving game's `memory.current` from zero until its next restart — inherent to cgroup v2; hot-swap
  and host reboot are unaffected.)
- **Spawn = self-move launcher.** `SpawnEngine` forks a `/bin/sh -c` launcher that does
  `echo $$ > <inst>/cgroup.procs` **before** `exec` — so the game and every child it forks are born
  inside the cgroup (containment-correct, not spawn-then-migrate). The daemon holds the stdin FIFO
  (`Instance.SocketFile`) open `O_RDWR` itself — **no `tail` keepalive process**; stdout appends to
  `Instance.LogFile`.
- **Three roles, all hosted services.** (a) *Native supervision* — the acting role above.
  (b) *Player-presence ingesters* (`PlayerPresenceIngester` for containers, `NativePlayerPresenceIngester`
  for native, `RconPlayerPresencePoller` for games that log connects but not disconnects) — pure
  observers that re-emit player join/left as kgsm wire events (`origin=system`) into the shared
  `PlayerSessionStore`. These never shell docker, never supervise; they're additive and decoupled from
  supervision. **Whether an instance is observed at all is `PlayerDetection`'s answer and only its** —
  the ingesters gate on it and `GET /players` reports it, so the daemon cannot poll a roster it calls
  unobservable. It is not a predicate a consumer can re-derive: it spans log patterns, RCON
  (port + password + command) and whether each pattern *compiles*, which is why the endpoint hands the
  verdict out rather than leaving every surface to guess from the same config.
  (c) *`UpnpReconciler`* — restores router forwards the IGD dropped under a running instance. Its own
  timer (`Watchdog__UpnpReconcileSeconds`, default 300, `0` off) rather than a sub-cadence of the 1 Hz
  supervision loop, because a sweep is a multi-second network round trip and that loop holds the gate;
  it needs no gate itself, reading the instance table lock-free like `/status` does. **A router that
  cannot be reached means the sweep does nothing** — an unreadable redirection table is not evidence
  of an empty one, and the whole design rests on not confusing the two.
- **Hot-swap entrypoints run before anything binds.** `--version` / `--selfcheck` (in `Program.cs`,
  before the host is built) let a deploy interrogate a freshly-installed binary as a cheap subprocess
  **without** binding the socket, entering the slice, or touching cgroups. `HotSwapCoordinator` +
  `HotSwapSignalListener` bridge SIGHUP → in-place `execv`. SIGTERM is a normal shutdown.
- **Two persistence files, two independent axes.**
  `desired-state.json` (`Watchdog__StateFile`) = the **boot/enable** axis (replaces `systemctl
  enable`/`WantedBy=`): `enable`/`disable` add/remove a name; on startup `StartupRestorer` re-adopts a
  still-live cgroup or respawns a dead one. Stores **intent only** — each spawn config is re-read fresh
  from kgsm-lib, so no stale-spec drift. `supervision-state.json` (`SupervisionStateStore`) persists
  restart counters / the give-up latch so they survive *any* death (OOM/SIGKILL), not just a planned
  hot-swap. The runtime `start`/`stop` axis is separate from the boot `enable`/`disable` axis.

## Project-specific invariants

- **kgsm-lib is the only path to KGSM, and READ-ONLY here.** `PackageReference
  TheKrystalShip.KGSM.Lib`. The watchdog uses it to *read* instance spawn config
  (`IInstanceService.GetInstanceInfo`) and watch lifecycle events — **never to start/stop** (that path
  spawns detached, which is exactly what this daemon replaces). Never shell `kgsm.sh` directly. (kgsm,
  conversely, routes its native `start/stop` *to* this daemon via `commands/handlers/watchdog.sh` when
  the socket is present.)
- **Reflection-free JSON, or it throws at runtime.** Every (de)serialized type must be registered in
  `Model/WatchdogJsonContext.cs` (source-gen). An unregistered type throws `NotSupportedException` at
  runtime — there is no reflection fallback. This is what lets the daemon ship as AOT.
- **Never fabricate state.** A status is measured (cgroup / `/proc`) or explicitly "unknown" — never
  invented. (The spine principle that killed the old kgsm-api.)
- **GC is tuned for an idle supervisor, deliberately.** `Watchdog.csproj` forces **Workstation,
  non-concurrent GC** (overriding the Web SDK's Server-GC default, which reserved a heap per core →
  ~100 MB idle RSS). `MemoryTrimmer` (hosted service) hands free pages back to the OS after activity
  bursts, growth-gated so a genuinely idle daemon just ticks. Don't flip these back to defaults.
- **Naming:** project `kgsm-watchdog`; namespace `TheKrystalShip.KGSM.Watchdog`; assembly
  `kgsm-watchdog`; config section `Watchdog`, so env overrides are `Watchdog__*`. Only
  `Watchdog__KgsmPath` is required.
- **`kgsm-watchdog.settings.json` is the source of truth for every knob** (the ecosystem names these
  `kgsm-<leaf>.settings.json`, never `appsettings.json`). It is loaded explicitly from
  `AppContext.BaseDirectory` because the slim builder under systemd has no working directory and
  default discovery finds nothing. An environment variable overrides one key of it; a variable naming
  an undeclared key binds to nothing and is logged at startup as a probable typo.
- **`AddEnvironmentVariables()` runs after the settings file, and the order is load-bearing.**
  Configuration resolves by source order, so a file registered after the environment provider
  outranks it and an override reads as applied while changing nothing. `AddWatchdogConfiguration`
  owns that order and is used by both the pre-host reads (`--selfcheck`, the required-knob check)
  and the host, so the two cannot diverge.
- **`KnownEnvVars` is derived from `WatchdogSettings`, not hand-listed** — adding a property is the
  only step needed to make a knob real, documented in `--help`, and exempt from the typo warning.
- **A knob lives in two places**: a `WatchdogSettings` property carrying `[LeafField]` and a
  `<panel>` doc tag, and a key in `kgsm-watchdog.settings.json`. `deploy/kgsm-watchdog.leaf.json` is
  **generated** from the first, with defaults read from the second, by
  `TheKrystalShip.KGSM.LeafConfig` on every build — so edit the settings class, never the JSON, and
  commit what the build produces. Miss either and the build fails naming the key. The package is
  build-only and declares no dependencies, so the AOT publish is unaffected. Format:
  `../leaf-config-descriptor.md`; mechanism: `../kgsm-leafconfig/README.md`.
- **A config-key rename cannot be hot-swapped.** The hot-swap `execv` inherits the live process's
  environment, which predates the rename, so `--selfcheck` on the new binary fails its required-knob
  check and the swap correctly aborts. Deploy those with `--cold`.

## Version tracking

- **Version source:** `<Version>` in `src/Watchdog/Watchdog.csproj`
- **Packaging reads it via `deploy/version.sh`** — `./deploy/version.sh` prints the declared version, `--pkgver` prints the pacman-safe form. A package never restates a version number; it asks for one.
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.
