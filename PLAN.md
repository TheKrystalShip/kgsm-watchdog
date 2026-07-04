# kgsm-watchdog — Project Plan

**Status:** Planning · **Created:** 2026-06-12 · **Stack:** .NET 10, Native AOT

> The resident **KGSM supervisor daemon**. It owns the `kgsm.slice` cgroup, spawns
> native game-server instances *into* per-instance cgroups, and supervises them
> (crash detection + restart). CLI / kgsm-lib / the Discord bot become **thin
> clients** that issue lifecycle commands over a unix socket. This deliberately
> breaks KGSM's historical "stateless / no warm process" rule — accepted
> eyes-open, because the daemon **is** the watchdog the project always wanted.
>
> **Logging** follows the ecosystem convention (`../logging-convention.md`):
> `Microsoft.Extensions.Logging` → `AddSystemdConsole()` (journald `<N>` priority prefix),
> levels from `kgsm-watchdog.settings.json` `Logging` + env (`Logging__LogLevel__Default`, default
> `Information`). The settings file is renamed from `appsettings.json` (collision-proof across the
> ecosystem's many `appsettings.json`-shipping services) and loaded explicitly from
> `AppContext.BaseDirectory` — `CreateSlimBuilder` under systemd leaves the content root at `/`, so the
> framework's default discovery finds nothing; `deploy.sh` installs it beside the binary. The
> `CreateSlimBuilder` host binds the `Logging` section via `AddConfiguration`. `"Microsoft.AspNetCore":
> "Warning"` there silences ~5-Info-lines-per-request chatter that otherwise floods journald (rate-limiting
> useful lines) and allocates on every poll.
>
> **Idle memory** — see [memory note] / `MemoryTrimmer`: the ~50 MB idle floor is genuine (≈30 MB live
> ASP.NET-Core/AOT managed heap with committed == live + ~13 MB binary + native; GC knobs can't shrink it).
> The fix targets *creep*: a Workstation-GC daemon never GCs at idle, so polling + 1 Hz ticks ratchet RSS up
> unbounded (56 → 100 MB / 40 min) — `MemoryTrimmer` re-trims (compacting gen-2 + `malloc_trim`) every 120 s
> when the working set grew ≥ 8 MB since the last trim, capping it.

---

## 1. Why this exists

Game-server instances that don't go through systemd get no crash protection. The
fix is native cgroup v2 supervision (see `kgsm/docs/specs/cgroup-supervision-plan.md`,
Increment 0 — done). But a hard constraint surfaced: **entering `kgsm.slice` needs
either root or a spawner already inside it** (cgroup-v2 delegation containment —
migrating a process needs write on the source/dest *common ancestor*). And KGSM
must run **headless** — callable by the Discord bot with **zero per-operation
privilege escalation**, never prompting for a password mid-install.

The only architecture that satisfies headless + rootless-at-runtime + remote-caller
is a **resident daemon** placed in `kgsm.slice` once at boot. Since crash-restart
*also* needs a resident watcher, the supervisor and the watchdog are the **same
component**. (Full reasoning + the rejected setuid-helper alternative live in
memory `kgsm-watchdog` / `kgsm-cgroup-supervision`.)

## 2. The two problems it solves — in one component

| Problem | How the daemon solves it |
|---|---|
| **Headless cgroup entry** | It lives in `kgsm.slice/supervisor`; forks games (born in-slice) and moves them to `kgsm.slice/<inst>` — an intra-slice move it's allowed to make. Clients never touch cgroups or need privilege. |
| **Crash detection** | It's the **parent** of every game process *and* reads per-instance `cgroup.events` (`populated 0/1`) — race-free, child-inclusive. |
| **Intent (crash vs. deliberate stop)** | It holds **desired state**: it received the `start`/`stop` command, so it knows what *should* run. cgroups solve detection; desired-state solves intent. |
| **Restart** | Exponential backoff + flap rate-limit + give-up state, all in the one process holding the state. |

## 3. Architecture & invariants

- **Sibling of kgsm-monitor.** Mirror its conventions: `.slnx` + `src/Watchdog`,
  `WebApplication.CreateSlimBuilder` → `kestrel.ListenUnixSocket`, `BackgroundService`
  + `PeriodicTimer`, reflection-free `FromEnvironment()` options, source-generated
  JSON context, hardened `Type=simple` systemd unit. **net10.0**, `PublishAot=true`,
  `InvariantGlobalization=true`, `IsAotCompatible=true`.
- **Integrates kgsm-lib, never shells `kgsm.sh`** (the dependency spine).
  `PackageReference TheKrystalShip.KGSM.Lib` (monitor uses 1.1.0). Reads instance
  spawn config via `IInstanceService.GetAll()` / `GetInstanceInfo(name)`; subscribes
  to `IEventService` for create/remove awareness. A new deserialized type must be
  registered in `KgsmJsonContext` (no reflection fallback).
- **The daemon ACTS; the monitor MEASURES.** kgsm-monitor stays consumer-agnostic,
  measure-only (keystone §4). The watchdog is a separate resident that *acts*
  (spawn/kill/restart). They may cooperate (the monitor can read the cgroups the
  watchdog creates), but the watchdog never becomes a metrics source.
- **Cgroup ops, inverted from the monitor.** The monitor *reads* cgroup v2 files;
  the watchdog *writes* them (mkdir child, write `cgroup.procs`, write `cgroup.kill`,
  read `cgroup.events`) — a C# port of `kgsm/core/cgroup.sh` semantics.
- **Never fabricate state.** A status is measured (cgroup/`/proc`) or "unknown",
  never invented (the spine principle that killed kgsm-api).
- **Naming:** project `kgsm-watchdog`; namespace `TheKrystalShip.KGSM.Watchdog`;
  assembly `kgsm-watchdog`; env prefix `KGSM_WATCHDOG_*`; control socket default
  `/run/kgsm-watchdog/control.sock`.

## 4. Boot & privilege model (the load-bearing sequence)

> **As-built since Increment 8 (2026-06-25).** The host is **systemd-only**; the old
> root-boot self-bootstrap + openrc variants are gone (their history is in §7 Inc 1
> and Inc 8). The model below is **systemd cgroup delegation**.

The daemon runs as the KGSM user from the start — **no root step, no privilege drop**.
systemd does the privileged cgroup setup via the unit (`User=kgsm`, `Slice=kgsm.slice`,
`Delegate=yes`): it creates `kgsm.slice` + `kgsm.slice/kgsm-watchdog.service`, enables
the controllers on the slice, and **chowns the service subtree to the user**. The boot
sequence is then just (`CgroupBootstrap`, `CgroupDiscovery`):

1. **Discover** the delegated base from `/proc/self/cgroup` — the daemon's own service
   cgroup `kgsm.slice/kgsm-watchdog.service` (or its parent, when an Inc-7 hot-swap
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
> `process` kills only the daemon; games keep running and the next daemon re-adopts them
> (the "games outlive a daemon bounce" property the old root-boot model got for free by
> keeping games outside the service cgroup).
>
> Caveat (cgroup v2, inherent): a daemon **cold restart/crash** while a game survives
> re-charges that game's `memory.current` from zero — systemd clears the service cgroup's
> `subtree_control` on a fresh start, momentarily detaching the memory controller, and
> already-resident pages are not retroactively re-charged. It self-heals on the next game
> restart. **Not** triggered by the normal paths: hot-swap (execve, no stop) preserves the
> charge, and a host reboot respawns games fresh (charged from birth).

> **Why children live under the *service* cgroup, not as siblings under the slice.**
> systemd reconciles a slice's OWN `cgroup.subtree_control` on every `daemon-reload`
> (stripping the controllers off cgroups it does not manage). With the old
> `kgsm.slice/<inst>` sibling layout that wiped `memory`/`pids`/`io` off every instance
> — `cpu.stat` is unconditional so CPU survived, but `memory.current` vanished and the
> monitor read **0** (the bug Inc 8 fixes). systemd leaves the delegated subtree *below
> the service* untouched, so the controllers persist. The per-instance path is coupled
> to the unit name; kgsm must surface the matching `cgroup_path` (`config_cgroup_base_name
> = kgsm.slice/kgsm-watchdog.service`), the monitor follows it. See Increment 8.

> `kgsm system setup-cgroups` (Increment 0) is now **legacy** — systemd does the
> delegation. Kept only as a non-systemd / diagnostic tool.

## 5. Control protocol (clients → daemon)

A unix socket (FS-perms are the security boundary, exactly like the monitor —
owner/group gated; network-facing auth like Discord-OAuth lives in the surfaces
*above*, not here). Verbs the daemon accepts:

- `start <inst>` — read config via kgsm-lib, spawn into `kgsm.slice/<inst>`, record
  desired-state = running.
- `stop <inst>` — desired-state = stopped, graceful stop-command → bounded drain →
  `cgroup.kill` → remove.
- `status <inst>` / `list` — report desired vs. actual (populated), restart counts.
- `GET /health` — the **unified ecosystem health probe** (one `/health` on every leaf,
  2026-06-15). Carries *readiness*: `200` only when in-slice and able to spawn; `503` +
  `{ready,detail}` reason when up-but-unable; no answer when down. Consumers treat anything
  but `200` as "unavailable — retry until `200`". Replaces the old split (`/healthz` liveness
  + `/ready` readiness); the bare-liveness `/healthz` is gone, and `/ready` survives as a
  deprecated alias of `/health` for one transition release (so a not-yet-updated kgsm CLI /
  kgsm-lib never silently drops off the daemon), to be removed next release.
- (later) `reload`, backoff overrides.

Transport mirrors the monitor (HTTP/1.1 over UDS via Kestrel, source-gen JSON), so
kgsm-lib gets a tiny typed client and the bot/CLI reuse it. **Open:** HTTP-over-UDS
vs. a line/JSON raw socket — decide in Increment 1.

## 6. Project structure (mirror kgsm-monitor)

```
kgsm-watchdog/
├── kgsm-watchdog.slnx
├── src/Watchdog/
│   ├── Watchdog.csproj            (net10.0, PublishAot, refs KGSM.Lib)
│   ├── Program.cs                 (CreateSlimBuilder, UDS, hosted services)
│   ├── WatchdogOptions.cs         (KGSM_WATCHDOG_* FromEnvironment)
│   ├── Model/
│   │   ├── Contracts.cs           (control request/response records)
│   │   └── WatchdogJsonContext.cs (source-gen)
│   ├── Cgroup/
│   │   ├── CgroupManager.cs       (create/attach/kill/remove/events — C# port of core/cgroup.sh)
│   │   └── CgroupBootstrap.cs     (root boot: slice + controllers + chown + enter + drop privs)
│   ├── Supervision/
│   │   ├── InstanceSupervisor.cs  (desired-state table, the registry)
│   │   ├── SpawnEngine.cs         (fork+exec into cgroup; FIFO-stdin / logfile-stdout)
│   │   ├── CrashWatcher.cs        (poll cgroup.events; restart policy)
│   │   └── BackoffPolicy.cs       (exponential + flap limit + give-up)
│   └── Control/
│       └── ControlEndpoints.cs    (start/stop/status/list)
├── tests/Watchdog.Tests/
└── deploy/
    └── kgsm-watchdog.service      (systemd, the ONE canonical unit: User=kgsm,
                                    Slice=kgsm.slice, Delegate=yes — see Inc 8)
```

> Inc 8 collapses the three former units (`kgsm-watchdog.service` root-boot +
> `kgsm-watchdog.rootless.service` + `kgsm-watchdog.openrc`) into a single
> delegated systemd unit. openrc and the root-boot self-bootstrap are dropped
> (systemd-only host).

---

## 7. Increment roadmap (how we build it)

### Increment 1 — daemon skeleton: spawn one instance into its cgroup  ◀ BUILT (2026-06-12)
- [x] Scaffold the AOT project (slnx, csproj, slim builder, UDS socket + chmod,
      `WatchdogOptions.FromEnvironment`, source-gen `WatchdogJsonContext`, `/healthz`).
      net10 AOT publish is **0-warning** (no IL2026/IL3050/ILC); 11 MB native binary.
- [x] `CgroupManager` (C# port of `core/cgroup.sh`: base/path, create, attach,
      is-populated, kill, remove, capability detection) + `NativeMethods` interop
      (`mkfifo`/`open`/`write`/`close`/`chown`/`access`/`setgroups`/`setres*id`).
- [x] `CgroupBootstrap` — root-boot path (slice + controllers + chown + enter
      supervisor + drop privs in `setgroups`→`setresgid`→`setresuid` order) and a
      non-root already-delegated detect-and-enter path; fails *gracefully* (Ready=false
      + precise reason on `/ready`) when launched outside the slice.
- [x] `SpawnEngine`: reads `Instance` from kgsm-lib, forks the game via a **self-move
      `/bin/sh -c` launcher** (`echo $$ > <inst>/cgroup.procs` before `exec`, so every
      child is born in-cgroup — containment-correct, not spawn-then-migrate). FIFO-stdin
      (`Instance.SocketFile`) + append logfile (`Instance.LogFile`); the daemon holds the
      FIFO O_RDWR fd itself (no `tail` keepalive process). Replicates the bash native
      path's `$(`/backtick reject + envsubst + unquoted word-split.
- [x] Control: `POST /start/{n}` / `POST /stop/{n}` (graceful stop-cmd → bounded drain →
      `cgroup.kill` → remove), `GET /status/{n}` / `GET /list` / `GET /ready`. Scope-guard:
      native-standalone only (no-ops on systemd/container).
- [x] Tests: 19 green — deterministic `CgroupManager` coverage + a hang-proof,
      capability-gated live round-trip (probe-and-skip on delegation containment, always
      reaps its helper), `SpawnEngine.ExpandEnvironment`, and `WatchdogOptions` parsing.
- [x] `deploy/kgsm-watchdog.service` (root-boot + drop model) + `deploy/validate-increment1.sh`
      + README.
- **VERIFIED under root (`validate-increment1.sh`, 8/8):** `/ready` = root-bootstrapped +
  dropped to uid 1000; daemon in `0::/kgsm.slice/supervisor`; `Groups: 1000` (root group
  discarded — `setgroups` proven); real `7dtd` native spawn lands in
  `kgsm.slice/7dtd/cgroup.procs` (self-move launcher proven); `stop` tears the cgroup down via
  `cgroup.kill`. Also verified non-root: fast-fail on missing `KGSM_WATCHDOG_KGSM_PATH`, and the
  control plane stays diagnosable when launched outside the slice (`/ready` 503 + reason).
- **Fix found during root validation:** after the uid drop, `HOME` was still root's, so shelling
  `kgsm.sh` read `/root/.local/share/kgsm` and `GetInstanceInfo` returned null. The bootstrap now
  sets `HOME`/`USER`/`LOGNAME` to the target user's (via `/etc/passwd`, or `KGSM_WATCHDOG_HOME`),
  like `su -`/systemd `User=`. Also fixed: `$instance_*` vars in `executable_arguments` now resolve
  from the `Instance` (7dtd uses `$instance_install_dir`), not the daemon's blank env.
- **Known gap (by design, Inc 2/3):** if the daemon dies, running games orphan and lose stdin;
  re-adoption is not in Inc 1. Graceful stop of a *fully-loaded* server (vs. the `cgroup.kill`
  fallback, which is proven) is best re-checked with factorio in Inc 2. "spawn+stop works" ≠
  "supervision works" yet.

### Increment 2 — watchdog behavior: crash detection + restart  ◀ BUILT (2026-06-12)
- [x] Durable desired-state table: `SupervisedInstance` (keyed by name) that **outlives**
      `RunningInstance` — desired-state, the restart counter, and the phase
      (`running`/`restart-pending`/`stopped`/`failed`) must survive the down-window, where no
      live process exists to hang them on. Intent (`DesiredRunning`) moved off the handle onto it.
- [x] `CrashWatcher` (`BackgroundService` + `PeriodicTimer`, `KGSM_WATCHDOG_POLL_INTERVAL_MS`,
      default 1 Hz) → calls `InstanceSupervisor.Reconcile()` each tick. The watcher is just the clock;
      **every state transition lives in the supervisor — one decision point**, so a timer and an
      exit-handler never race the same state. Reconcile *try-acquires* the gate and skips a tick if a
      control verb holds it (never blocks behind a graceful-stop drain).
- [x] Detection = cgroup `populated`→0 (child-inclusive, race-free) **while desired-running**. The
      leader's **exit code** (the launcher `exec`s into the game, so its `Process.ExitCode` is the
      game's) is logged and shown in `reason`, and gates restart under one **configurable policy**
      (`KGSM_WATCHDOG_RESTART_POLICY`): **`always` (default)** restarts on any exit — the only "stay
      down" is a deliberate `stop`, since operator intent lives authoritatively in `DesiredRunning` and
      a game server's exit code is an unreliable crash signal; **`on-failure`** (opt-in, systemd-style)
      leaves a clean code-0 exit `stopped` and restarts only non-zero/signal/unknown exits.
- [x] `BackoffPolicy` (pure, unit-tested): exponential delay **between** restarts
      (`base·2^(n-1)`, capped, overflow-safe) **decoupled** from give-up. Give-up =
      **consecutive failures since last stability ≥ maxRetries**, NOT failures-within-a-window — a
      window limit is defeated by the backoff itself (the delay spaces restarts past the window so it
      never fills). `NoteStable()` resets the streak once uptime ≥ `StabilityThreshold` (so "crashed
      once after a week up" ≠ one step closer to failed); a manual start `Reset()`s the give-up latch.
      A **grace window** after each (re)spawn suppresses crash-detection so a slow start isn't
      self-flagged as a crash.
- [x] `/status` + `/list` surface `phase`, `restarts` (the streak), and a human `reason`.
- [x] Tests: 26 green (20 Inc 1 + 6 `BackoffPolicy`/`RestartTracker` — exponential growth+cap,
      give-up at maxRetries, **stability-reset never accumulates across healthy runs**, manual-start
      clears give-up, `FromOptions` mapping). AOT publish still **0-warning**.
- [x] `deploy/validate-increment2.sh` (shellcheck-clean) — env-tunes grace/backoff/maxRetries so
      give-up runs in seconds; simulates crashes via the instance's own `cgroup.kill`, spacing kills by
      waiting for the new PID (no racing).
- **VERIFIED under root (`validate-increment2.sh`, 12/12)** against real `7dtd`: crash (`cgroup.kill`)
  → respawn with a **new PID** back in the cgroup, `phase=running restarts=1`; second crash → respawn;
  third crash exceeds `maxRetries=2` → `phase=failed restarts=3 reason="restart limit reached (3
  consecutive failures, last exit 137); gave up after 2 retries"` and **no further restarts**; manual start **clears the give-up latch**
  (fresh PID); deliberate `stop` → stays down + dropped from the table (404). `exit 137` (128+SIGKILL)
  confirms the exit-code discriminator. Teardown clean: no stray process, no leftover cgroup, slice
  still delegated to the user.
- **Exit (met):** a crashed standalone instance restarts; a deliberately-stopped one does not; a
  crash-loop gives up and reports "failed".
- **Restart policy made configurable (was hardcoded on-failure).** The user flagged the on-failure
  footgun — a game server that exits **0** on a fatal error (many do) or on SIGTERM would be silently
  left dead. Since operator intent for a deliberate stop is already held by `DesiredRunning`, the exit
  code was a redundant, unreliable second-guess. Resolution: `KGSM_WATCHDOG_RESTART_POLICY`, **default
  `always`** (no footgun by default — any exit while desired-running restarts), with `on-failure` as
  opt-in. The decision is a pure one-liner (`BackoffPolicy.ShouldRestartAfter`) unit-tested for both
  modes (clean/non-zero/signal/unknown).
- **Coverage gap (narrowed, by design):** the live run only exercised non-zero exits (every
  `cgroup.kill` → SIGKILL → 137), so the **`on-failure` clean-exit-stays-stopped** branch ran in
  neither the unit *integration* nor the live run — a live test needs a fake instance that exits 0 on
  its own (deliberately not built). Under the **default `always`** that branch is inert (exit 0 is
  restarted), so the original footgun is gone by default; `ShouldRestartAfter(0)` is unit-covered for
  both modes. (`NoteStable()`'s uptime-reset was likewise not hit live — `STABILITY_SEC=3600` — but its
  mechanics are unit-covered and the wiring is a one-line comparison.)
- **Note (carried from Inc 1, still open):** graceful stop of a *fully-loaded* server vs. the
  `cgroup.kill` fallback — the 7dtd `stop` again hit the kill fallback because it was mid-world-load
  (`killed (timeout)`). The no-restart invariant holds regardless; re-confirm a true graceful drain
  with a fast-loading server (factorio) when convenient. Daemon-death orphan re-adoption remains Inc 3.

### Increment 3 — clients + boot integration (in the `kgsm` repo + here)  ◀ PARTIAL (2026-06-12)
- [x] kgsm-lib: typed `IWatchdogClient` for the control socket — `WatchdogClient`
      (HTTP/1.1 over UDS via `SocketsHttpHandler.ConnectCallback`), the 3 DTOs mirrored
      with explicit camelCase `[JsonPropertyName]` + registered in `KgsmJsonContext`
      (reflection-free), `AddKgsmWatchdogClient(...)` DI. 13 unit tests (wire-shape casing
      guards + ctor/DI), full lib suite **420 green**, `IsAotCompatible` build **0-warning**,
      packed **1.2.0** locally. Decoupled from `AddKgsmServices` — a surface can take either.
- [x] `kgsm.sh start/stop` (native standalone) route to the daemon when present — new
      `commands/handlers/watchdog.sh` (`__watchdog_available` gates on socket + `/health`=200
      — unified 2026-06-15, was `/ready`;
      `__watchdog_dispatch_lifecycle` maps 200/409→event code, conn-fail→`EC_ERROR` with **no
      silent double-spawn**), wired into `__logic_{start,stop,is_active}_standalone_instance`.
      **is-active also routes** (the daemon writes no PID file, so the management script's check
      would be stale); a "not tracked" result falls through to the direct check. **stop is gated
      on `__watchdog_tracks`** (orphan safety): an instance the daemon does not track — a direct-path
      orphan started before the daemon — falls through to the direct PID-file stop, so the daemon
      can't false-report "stopped" while the orphan runs (and `restart` therefore can't double-spawn).
      Absent/curl-less → direct path unchanged. 18 unit tests / 27 assertions
      (`test_watchdog_routing.sh`), lifecycle regression 44/44, shellcheck clean.
      **Residual (documented): explicit `start` on a live direct-path orphan still double-spawns —
      the genuine orphan re-adoption gap, out of scope here.**
- [x] Ship `deploy/` units for the boot variants: `kgsm-watchdog.service` (root-boot,
      recommended), `kgsm-watchdog.rootless.service` (`User=kgsm, Slice=kgsm.slice, Delegate=yes`
      — the real `Delegate=yes` half of the bullet below; **requires `kgsm system setup-cgroups`**
      since per-service delegated-base discovery is unwired), `kgsm-watchdog.openrc` (non-systemd).
      systemd units `systemd-analyze verify` clean; openrc shellcheck clean.
- [ ] **(its own future increment — direction REVISED 2026-06-12 to a COMPLETE HARD BREAK from systemd.)**
      Not the earlier "migrate systemd-native onto the watchdog, then drop `Restart=on-failure`" — instead
      **remove the systemd *runtime* entirely**: delete `service.tp`/`socket.tp`, `files.systemd.sh` (both
      layers), the `systemctl`/`journalctl` lifecycle branches, and the `enable_systemd` / `lifecycle_manager=systemd`
      path, so every native instance is watchdog-supervised and systemd is used **only** to boot the daemon
      (`deploy/*.service`). **Sequenced AFTER Increment 5 (boot persistence) — which is now BUILT** — because
      the watchdog must be able to restore boot auto-start before systemd's `enable`/`WantedBy` is torn out.
      The systemd surface is mapped (≈4 deletable files + ≈4 editable; the four `lifecycle.sh` dispatchers
      branch only on `lifecycle_manager`, so the container path is untouched). The kgsm-lib `Instance` model
      keeps its now-vestigial `LifecycleManager`/`EnableSystemd` fields (they deserialize to defaults — no
      breaking 1.3.0). Nothing is deployed, so it's a clean deletion, not a live migration. (The `Delegate=yes`
      half is already done, in the rootless unit above.)
- **VERIFIED under root (`validate-increment3.sh`, 8/8)** against real `7dtd`: `kgsm start 7dtd`
  (the bash CLI) routed to the running daemon → the process entered `kgsm.slice/7dtd` **and** appeared
  in the daemon's `/list` (a direct spawn would do neither); `kgsm is-active` reported active off the
  daemon's `/status` (not a stale PID file); `kgsm stop` tore the cgroup down and dropped it from
  `/list` (is-active then inactive); and with the daemon killed the routing gate
  (`__watchdog_available`) went false → fallback to the direct path. Teardown clean (no stray process,
  no leftover instance cgroup, slice still delegated to the user).
- **Exit (met — pending the operator's live Discord run):** the bash + lib + boot plumbing is in place, unit-verified, and the
  `kgsm → daemon` round-trip is **verified live**. kgsm-lib **1.2.0** is distributed via the
  `/home/heisen/local-nuget` local feed (the bot's `nuget.config` already references it), and the **bot
  capstone is wired**: 3 csprojs bumped 1.1.0→1.2.0; a **read-only** supervision surface added
  (`IWatchdogService` over `IWatchdogClient`, `GetWatchdogStatusQuery`, `/supervision` command) — start/stop/
  restart are left on the existing kgsm-lib→`kgsm.sh` path, which already routes to the daemon, so no
  second C# write path was introduced (Option A). Routing is verified, not assumed: kgsm-lib invokes
  `kgsm.sh lifecycle <verb>`, which (kgsm.sh:196 ≡ :214) resolves to the *same* `lifecycle.sh <verb>` path
  `validate-increment3.sh` proved live; `restart` decomposes into routed stop+start (can't fight the
  supervisor). Bot suite 8/8, 0-warning build, assets resolve 1.2.0. **Only the literal Discord-token run
  is the operator's manual step.** The clarified systemd `service.tp` decision is its own future increment.

### Increment 4 — cross-repo finish (additive)
- [ ] kgsm-lib: `Instance.CgroupPath`; register in `KgsmJsonContext`.
- [ ] kgsm-monitor: native folds into the cgroup kinds (reads the watchdog's
      cgroups → fixes CPU/mem accuracy + PID-reuse fragility; `io.stat` still
      controller-gated; per-server net still uncovered).
- [ ] Keystone: add the supervisor to the topology, **amend §4 statelessness**,
      fix the §6 ledger + `kgsm-lib/docs/host-monitoring-inventory.md` native anchor.

### Increment 5 — boot persistence: desired-state restore across restarts  ◀ DONE (built 2026-06-12, root-verified 2026-06-13)

The in-house replacement for systemd's `systemctl enable` + `WantedBy=multi-user.target`, and the
**prerequisite for the systemd hard-break** (the user's revised order, 2026-06-12: stand up the
in-house boot auto-start FIRST, remove the systemd runtime SECOND — never tear out boot auto-start
before the watchdog can take over the job). Until now desired-state lived only in memory, so a daemon
restart or host reboot came up empty and auto-started nothing.

- [x] **`DesiredStateStore`** — persists the desired-running *name set only* (the spawn config is
      re-read fresh from kgsm-lib on restore → no stale-spec drift) to a versioned JSON file. Path:
      `KGSM_WATCHDOG_STATE_FILE`, else `${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/desired-state.json`,
      **resolved lazily** (post-bootstrap, so it lands in the dropped KGSM user's data tree — writable by
      construction in all boot paths, no new privileged setup step, no `/var/lib` chown asymmetry).
      Mutation is **incremental** (`Add` on start / `Remove` on stop), NOT a snapshot of the live table:
      an instance whose config kgsm-lib can't read at restore (a transient miss) is therefore never
      silently dropped from auto-start — intent persists until an *explicit* stop, exactly like a systemd
      unit stays enabled until `disable`. Atomic same-dir temp+rename; missing→empty, corrupt→warn+empty
      (a bad file can never wedge boot). AOT-safe via a new `PersistedDesiredState` DTO in `WatchdogJsonContext`.
- [x] **`store.Add` on start / `store.Remove` on stop** (`InstanceSupervisor`, under the existing gate).
      Add fires *after the scope guard, before the spawn attempt* — so "start it on boot" persists even if
      this spawn fails (systemd parity: enablement is independent of the current run). Stop removes on
      every path (so an operator can `stop` even a not-running stale entry to prune it).
- [x] **`RestoreAsync` + `StartupRestorer`** (an `IHostedService` registered *before* `CrashWatcher`, so the
      table is fully restored before the first reconcile tick; runs after `CgroupBootstrap`, so the
      supervisor is ready and HOME is the dropped user's). For each persisted name it re-reads the spec and
      applies a **pure** decision (`RestorePlan.Classify`, unit-tested like `BackoffPolicy`):
      - **Adopt** — cgroup already populated (a process that outlived a *daemon* restart): re-attach as
        `Running` with `Current=null`, **no kill, no respawn**. Supervised by cgroup liveness; on its first
        crash it respawns through the normal path (rebuilding a real handle), and `ShouldRestartAfter(null)`
        restarts under both policies so it never falls out of supervision. Two documented limits until that
        next respawn: a stop is a hard `cgroup.kill` (the FIFO/PID weren't recovered — FIFO-reopen deferred),
        and there's no exit code. **Deliberately does NOT route through `StartAsync`**, whose purge-before-spawn
        would `cgroup.kill` the live instance.
      - **Spawn** — cgroup empty (a *host reboot* left nothing running): `TrySpawn` fresh (mirrors the restart
        path, no per-instance synchronous `WaitForPopulated` that would delay the socket bind by 5s×N during
        boot; the reconcile loop confirms liveness a tick later).
      - **Skip** — gone (kgsm-lib returned no config; intent KEPT, logged loudly) or out-of-scope (became
        container/systemd). Restore **never re-writes** the set, so a skip can't prune durable intent.
- [x] **Deliberate call — `Failed` / cleanly-stopped instances DO auto-start on boot.** With the incremental
      model, `_instances.Where(DesiredRunning)` ≡ everything not explicitly stopped, so a crash-looped
      (`Failed`) or `on-failure` clean-exit instance stays in the set and restores with a **fresh**
      `RestartTracker` (streak + give-up reset). Faithful to what it replaces — `enable` + `WantedBy` restart
      an enabled unit on boot regardless of its last exit or rate-limit state.
- [x] **stdin-EOF nuance (honest limit).** The daemon holds the game's stdin FIFO open, so killing the daemon
      closes the only writer and the game *may* hit stdin EOF and exit (game-dependent). The **Adopt** path is
      thus reachable only for EOF-tolerant processes; otherwise the game exits and restore takes the **Spawn**
      branch (a brief restart) — correct either way, just not seamless. The deferred FIFO-reopen would help but
      still can't cover the gap between daemon death and restart, so this is a graceful-degrade, not a guarantee.
- [x] Config: `KGSM_WATCHDOG_STATE_FILE` (+ `KnownEnvVars` + `--help` row; the completeness test covers it).
- [x] Tests: **55 green** (was 38) — `DesiredStateStoreTests` (round-trip across a fresh store = a restart,
      Add/Remove idempotence, missing/corrupt→empty, dir auto-create, **and the default empty-`StateFile`
      HOME/XDG derivation** every operator hits), `RestorePlanTests` (the 5-way fork), 2 options tests. AOT
      publish **0 ILC warnings** (native ELF; the new source-gen JSON path is clean).
- [x] **VERIFIED under root (`deploy/validate-increment4.sh`, real `7dtd`, 2026-06-13) — 13/13 pass:**
      A `kgsm start` persists intent; B daemon restart with the game alive (FIFO held open) → adopt (same
      pid 1777045, no respawn); C daemon down + `cgroup.kill` → respawn fresh (new pid 1778232); D `kgsm stop`
      prunes the set → a later restart does not auto-start. Trap left no orphaned daemon, cgroup, or state file.
- **Exit (met):** desired-state survives a daemon/host restart on disk; the daemon restores it on boot
  (adopt-live / spawn-dead), proven live against a real server — the in-house stand-in for systemd boot
  auto-start, which unblocks the systemd hard-break (next).

### Increment 6 — daemon-restart resilience: keep console + graceful stop across a bounce  ◀ Option 1 DONE (2026-06-25); Option 3 = chosen long-term

**The problem.** Adoption (Inc 5) + live-orphan re-adoption (2026-06-25, commit `0affcf2`: the daemon now
also re-adopts a *started-not-enabled* live cgroup, not just the persisted set) re-attach **supervision**
across a daemon bounce — crash-detection, hard stop, status, and metrics all run off the cgroup, so they
never depended on process parentage. But the daemon lost the game's **command channel**: the old `AdoptLive`
left `Current=null`, so until the next respawn a stop was a hard `cgroup.kill` (no in-game save) and
**console input was unavailable**. Worse, the old `Dispose()`-on-shutdown *deleted* each live game's stdin
FIFO, so the game kept an unlinked inode no successor daemon could re-open, and the game re-parented to
PID 1 (expected — Linux re-parents an orphan to init; you **cannot** re-parent a live process onto a new
daemon, confirmed). In production, supervising N games, a routine watchdog update meant losing console +
graceful stop for all N until each next restart. Unacceptable for a control plane.

Three ways to close it (smallest → most seamless):

- **Option 1 — re-open the FIFO on adopt (+ cgroup-recovered PID). ◀ DONE 2026-06-25.**
  The stdin FIFO is a named pipe on disk; only the daemon's *write-fd* dies with the daemon, not the
  channel. So: (a) on daemon shutdown, **release the handle without deleting the FIFO**
  (`RunningInstance.ReleaseKeepingFifo`; `InstanceSupervisor.Dispose` no longer `Dispose()`s — that delete
  was the orphaning bug); (b) on adopt, **re-open the surviving FIFO** O_RDWR (`SpawnEngine.ReopenFifo` —
  never re-`mkfifo`, which would be a new inode the game can't see) into a Process-less adopted
  `RunningInstance` (`RunningInstance.Adopt`), and recover the display PID from `cgroup.procs`
  (`CgroupManager.FirstPid`). Graceful stop already drains via the **cgroup** (`WaitForDrainAsync`), not
  `waitpid`, so it needs only a working `SendLine` — which the re-opened fd provides. Net: **console +
  graceful stop survive any restart cause** (update *or* crash/OOM), no respawn needed. The only residual
  loss for an adopted instance is the exit *code* (`ExitCode=null` — a non-child has no `waitpid`; the
  cgroup gives liveness, not status). *Limit (LIVE-CONFIRMED 2026-06-25):* the FIFO node survives a daemon
  bounce and is re-opened (pid recovered, `/list` reads "full control"), and a freshly-spawned instance
  stops gracefully — BUT during the ~4 s daemon-down window the FIFO momentarily has **no writer**, so an
  EOF-sensitive game closes its stdin: factorio logged `InterruptibleStdioStream: Got EOF on stdin; closing`
  exactly at the bounce, after which the re-opened `/quit` is not honored and the stop falls back to
  `cgroup.kill` until the next respawn. The daemon's *write* side is fully restored; whether the *game* still
  listens depends on its EOF tolerance. This daemon-down EOF gap is unavoidable for a process-restart
  approach and is exactly what **Option 3** (same PID, fds never close → the game never sees EOF) eliminates
  — concrete motivation for the chosen long-term direction. (Option 1 still fully helps EOF-tolerant games,
  recovers the honest PID, and — crucially — stops the daemon DELETING live FIFOs, the prerequisite for
  Options 2/3.) A game spawned by a *pre-fix* daemon (FIFO already deleted) stays cgroup-only until its next
  respawn. Tests: `RunningInstanceTests` (adopted PID/no-exit-code; release-keeps / dispose-deletes the
  FIFO), `CgroupManagerTests.FirstPid`. AOT clean.
- **Option 2 — systemd fd store (`FDSTORE=1`).** Idiomatic zero-loss across a *restart*: stash the FIFO fds
  via `sd_notify(FDSTORE=1, FDNAME=<instance>)`; systemd hands them back through `sd_listen_fds()` on the
  next start, so the fds never close → no gap at all. Needs `FileDescriptorStoreMax=N` in the unit, and
  note the store survives `systemctl restart` but **not** a separate stop+start unless
  `FileDescriptorStorePreserve=yes` — our `deploy/deploy.sh` currently does stop+start. From .NET/AOT it's a
  Unix-socket `sendmsg` + `SCM_RIGHTS` (no managed dep). Not built.
- **Option 3 — self-re-exec hot-swap (`execve`, nginx/HAProxy-style). ◀ CHOSEN long-term direction. Detailed phased build plan: Increment 7 below.**
  On a signal the daemon re-execs the updated binary **in place**: same PID, so the games
  stay its children (**no reparenting to PID 1 at all**) and open fds survive the exec — the FIFO write-fd
  never closes, so the game never sees the stdin EOF that defeats Option 1 for EOF-sensitive games. Work: the
  FIFO fds must shed `O_CLOEXEC` at swap time (we set it today at `SpawnEngine` — clear it just before exec),
  hand the fd→instance map + restart counters across the exec, and change the deploy to install-then-reload
  instead of bouncing the unit. Most code, but truly seamless — instances never notice an update. This is
  the bulletproof end state the user wants; Option 1 (above) is the pragmatic interim that already covers
  the *crash/OOM* restart case Option 3 alone can't. **Decisions locked (2026-06-25): trigger = SIGHUP via
  `systemctl reload`; restart counters disk-persisted (survive an unclean daemon death too, not only a swap).**
  Not built — see Increment 7 for the phased plan.

Refs: [systemd File Descriptor Store](https://systemd.io/FILE_DESCRIPTOR_STORE/), [pidfd_open(2)](https://man7.org/linux/man-pages/man2/pidfd_open.2.html),
[NGINX binary upgrade](https://0x0f.me/blog/nginx-zero-downtime-upgrade-code-analysis/), [HAProxy seamless reloads](https://www.haproxy.com/blog/truly-seamless-reloads-with-haproxy-no-more-hacks).

---

### Increment 7 — Option 3: self-re-exec hot-swap  ◀ BUILT + LIVE-VALIDATED (2026-06-25)

> **Validated live on the test host (2026-06-25), factorio-test.** A genuine version-delta hot-swap
> (`systemctl reload` → SIGHUP → `execve`) was proven end-to-end: daemon **MainPID unchanged**, the game
> **PID unchanged** (zero downtime), **no new `Got EOF on stdin`** in the game log (the exact thing Option 1
> can't deliver), an **immediate graceful `/stop` → "stopped gracefully"** right after the swap, and the
> **restart counter preserved** across it. Also verified: the `--selfcheck` **safety gate aborts** a swap to
> a broken binary and stays on the running image (games untouched); and Phase 2's **counters survive an
> unclean `kill -9`** of the daemon (rehydrated from disk on the systemd restart). `deploy.sh` default path
> reports `hot-swap verified … on UNCHANGED PID … ✓`.
>
> **Three bugs found in live validation and fixed (commits `c9dbebb`, `ee2d567`):**
> 1. **EACCES on the re-exec'd socket bind.** The root-boot unit used systemd `RuntimeDirectory=`, which
>    systemd RE-CHOWNS to the unit user (root) on `systemctl reload`. The uid-dropped, re-exec'd daemon then
>    couldn't bind the control socket and crashed (SIGABRT → systemd `Restart=always` masked it as a normal
>    restart — caught via a temporary bind-probe). **Fix:** drop `RuntimeDirectory=` from the root-boot unit
>    (kept in the rootless unit, where the run-user owns it); the root bootstrap's `PrepareSocketDir` is now
>    the sole owner — creates the dir, locks it 0750, chowns it to the target user, and it then persists
>    across reloads/hot-swaps.
> 2. **Abort emitted an unregistered kgsm event** (`watchdog-hotswap-aborted` → "Invalid event type"). **Fix:**
>    log-only (the plan's documented fallback); the abort is already surfaced loudly to the journal.
> 3. **`deploy.sh` verify mis-reported a successful swap** — it substring-matched `--version`'s combined
>    `X+<hash>` against the `/version` JSON `{version,commit}`, which never contains that combined form.
>    **Fix:** compare on the commit hash.
>
> **Post-validation hardening — zombie reaper (commit `a6766bd`, live-validated).** A hot-swap-adopted
> instance is held `Process=null` and the game stays the daemon's child (same PID, no reparent-to-init), so
> when it later exits nothing `waitpid`s it → it lingers as a zombie until the next daemon restart. Fix: track
> each adopted pid (`_reapable`, registered in `AdoptHandoffEntry`) and reap it on the 1 Hz tick with
> `waitpid(pid, WNOHANG)` — **only those specific pids, never `waitpid(-1)`**, which would race the CLR
> reaping its own children and corrupt their exit codes. Proven live: a post-swap stop left a zombie for ~1s,
> then the reaper cleared it (0 zombies).
>
> **Build note:** all phases implemented (parallel subagents, integrated to `main`); build 0-warn, tests 166,
> AOT 0-warn. **Open-risk #1 resolved during the build: it was TRUE** — on .NET 10
> `Environment.SetEnvironmentVariable` does NOT write through to libc `environ`, so the planned
> `SetEnvironmentVariable` + `execv` would not carry the handoff. Implemented the plan's documented
> fallback instead: marshal an explicit `envp` (current env + the handoff override) and call **`execve`**.
> Proven in a standalone harness; confirmed live below.

The end state of Inc 6's Option 3, spelled out. **Goal:** a watchdog binary update with **zero** game
downtime and **no** loss of console or graceful stop — even for an EOF-sensitive game like factorio that
Option 1 cannot fully cover. **The mechanism in one line:** on `systemctl reload` the daemon `execve()`s the
freshly-deployed binary *in place* — same PID, and every open fd without `O_CLOEXEC` is carried into the new
image, so each game's stdin-FIFO **write-fd stays open continuously across the swap** and the game never sees
EOF on stdin. That continuity is the whole win and the one thing a process-restart (Option 1/2) can't give.

**Decisions locked (asked + answered 2026-06-25):**
- **Trigger = SIGHUP via `systemctl reload`.** `ExecReload=/bin/kill -HUP $MAINPID` + a `PosixSignal.SIGHUP`
  handler. Host-local, zero cross-repo churn; because the swap keeps the **same PID**, systemd's main-PID
  tracking never even notices (it is not a restart from systemd's view). A control-socket `/upgrade` command
  was deferred — the binary must be on the host first, so a remote trigger buys little today.
- **Restart counters are disk-persisted** (companion state file), so `ConsecutiveFailures` / `GaveUp` survive
  an **unclean** daemon death (SIGKILL/OOM) too — not only a planned swap. Closes the counter-reset honesty
  gap everywhere, and Phase 2 below is independently shippable value even before the swap exists.

**Grounding facts (verified in this codebase, 2026-06-25):**
- Host is `WebApplication.CreateSlimBuilder` + Kestrel on a unix socket; **no** `UseSystemd()`/`sd_notify`,
  **no** signal handling today, unit is `Type=simple` `Restart=always` (`deploy/kgsm-watchdog.service`).
- FIFO fds are opened `O_RDWR | O_CLOEXEC` (`SpawnEngine.cs:88`, and `ReopenFifo`). `O_CLOEXEC` is *correct*
  in steady state — it stops a spawned game from inheriting sibling instances' fds — so the plan **keeps it
  and clears it only for the instant of the exec**, then re-sets it in the new image.
- `fcntl`/`execve` are **not** bound today; `NativeMethods` has `open/close/write/mkfifo/statx/...` only
  (`Interop/NativeMethods.cs`). The self-exe path is available without P/Invoke via `Environment.ProcessPath`
  (Native-AOT single-file runs in place — not extracted — so it resolves to the real install path).
- The **control socket is Kestrel-owned and opaque** — it cannot be handed across the exec. Accepted
  non-goal: the control plane blips for <1 s while the new image re-binds (the bind path already
  `File.Delete`s + re-listens, `Program.cs:87`). **Games are unaffected** — their fds, cgroups, and PIDs are
  untouched. Existing clients (kgsm-lib, the deploy health-poll) already tolerate transient unavailability.
- State that **must** cross the swap: each live FIFO **fd number** (inherited as-is by execve), and per
  instance `ConsecutiveFailures`/`GaveUp` (`RestartTracker`), `Phase`, `SpawnedAt`, `NextRestartAt`,
  `LastReason`, `DesiredRunning` (`SupervisedInstance`). Safely **re-derived** in the new image: cgroup
  liveness (`IsPopulated`), display PID (`FirstPid` ← `cgroup.procs`), and the spec (kgsm-lib).

**Why `execve` is safe from a multi-threaded AOT runtime:** execve discards the entire process image — all
CLR/AOT threads and GC/heap state vanish and a fresh image starts at `Main`. The only requirement is that
everything the successor needs is committed **before** the call: the handoff (env + disk) and a log flush.
On the AOT-clean path we hand-marshal a small `argv` and pass the handoff through the environment, so there
is no managed state to preserve. `execve` replaces the image **only on success**; on any failure it returns
and the *old* image continues intact — which is what makes the safety gate below trustworthy.

Phases are ordered by dependency; **Phase 2 ships standalone value** (crash-resilient honest counters) even
if the swap itself slips.

- **Phase 0 — `--version` / `--selfcheck` + (optional) `GET /version`.** A swap must not exec a broken
  binary. Add a top-of-`Main` arg branch (mirroring the existing `--help` block, before the host is built):
  `--version` prints the assembly informational version and exits 0; `--selfcheck` runs a no-side-effect
  validation (parse `WatchdogOptions`, confirm the binary loads — **without** binding the socket or touching
  cgroups) and exits 0/non-zero. This is the contract the swap's safety gate (Phase 3) invokes as a
  subprocess on the *new* binary before committing. Optionally expose `GET /version` (+ `WatchdogVersionInfo`
  in `WatchdogJsonContext`) so the deploy script can confirm the post-swap build. *Small, independent,
  zero-risk; testable on its own.*

- **Phase 1 — native interop: `fcntl`, `execv`, cloexec helpers.** Add to `NativeMethods`: `fcntl(int fd,
  int cmd, int arg)` with `F_GETFD=1`/`F_SETFD=2`/`FD_CLOEXEC=1`, and `execv(byte* path, byte** argv)`
  (manually marshalled — build a NULL-terminated `char**` of UTF-8 argv via `Marshal`, AOT-safe; **prefer
  `execv` + `Environment.SetEnvironmentVariable` over `execve`** so we never hand-marshal `envp` — on
  .NET/Linux `SetEnvironmentVariable` calls libc `setenv`, updating the `environ` that `execv` inherits;
  *verify this on net10 in this phase, fallback = marshal `envp` explicitly*). New `Interop/ReExec.cs`:
  `ClearCloexec(fd)`/`SetCloexec(fd)` (fcntl `F_SETFD`), `Exec(path, argv)` (returns only on failure, with
  errno). Capture the self path once at boot from `Environment.ProcessPath`. *Unit-test the pure argv-marshal
  builder; `execv` itself is exercised only in the Phase 6 live test (it replaces the image).* Re-publish AOT
  and confirm 0 ILC/IL2026/IL3050 — the hand-marshalling is the one place that can regress this.

- **Phase 2 — disk-persist supervision counters (independently shippable).** New `PersistedSupervisionState`
  + `InstanceRestartState { ConsecutiveFailures, GaveUp, Phase, SpawnedAt, NextRestartAt, LastReason }`,
  registered in `WatchdogJsonContext` (source-gen, AOT). A `supervision-state.json` companion alongside
  `desired-state.json` (reuse the `DesiredStateStore` pattern / its directory). Write on each meaningful
  transition in `ReconcileOne` (crash registered, give-up, stability-reset) + on graceful stop/disable —
  cheap, since counters only move on a crash. Add `RestartTracker.Restore(consecutiveFailures, gaveUp)`
  (keeps encapsulation). On boot, a new `RehydrateCountersAsync` (run by `StartupRestorer` *after*
  `RestoreAsync` + `AdoptLiveOrphansAsync`) re-applies persisted counters/phase/timing onto matching live
  instances and prunes entries for instances that are gone. **Payoff even without the swap:** an OOM/SIGKILL
  of the daemon no longer silently resets a crash-looping instance's counter to 0/5 — honest alerting across
  *any* daemon death.

- **Phase 3 — the hot-swap routine + SIGHUP trigger.** New `HotSwapCoordinator`:
  1. **Guard** — refuse if a swap is already running, or `SupervisorState` not ready.
  2. **Resolve** target = the boot-captured `Environment.ProcessPath`.
  3. **Validate (safety gate)** — `Process.Start(path, "--selfcheck")` with a bounded timeout; require exit 0
     (optionally diff `--version` to spot a no-op). **On failure → ABORT**: log loudly, emit a `system`-origin
     event, stay on the old image. This is what makes a bad deploy survivable.
  4. **Quiesce** — acquire the supervisor `_gate` (serializes against reconcile + control verbs; `CrashWatcher`
     already `Wait(0)`-skips when the gate is held) and set a `_swapping` flag.
  5. **Serialize handoff** — build `{ name → fifoFd, counters, phase, timing, desiredRunning }` for every live
     `Current`; flush the Phase-2 disk file too (belt-and-suspenders); set
     `KGSM_WATCHDOG_HOTSWAP_HANDOFF=<base64 json>` via `Environment.SetEnvironmentVariable`.
  6. **Shed cloexec** — `ReExec.ClearCloexec(fd)` on each live FIFO fd so it survives the exec.
  7. **Flush logs**, then `ReExec.Exec(path, [path, "--resumed"])`.
  8. **On return (exec failed)** — re-`SetCloexec` the fds (restore the steady-state invariant), unset the env
     var, clear `_swapping`, release the gate, log + emit. The old image soldiers on; no game harmed.
  Register a `PosixSignal.SIGHUP` handler (in `Program.cs` or a tiny `IHostedService`) that kicks the
  coordinator off the signal thread. **SIGTERM is unchanged** — a clean stop still `ReleaseKeepingFifo`s
  (Option 1 remains the fallback for any non-hot-swap restart).

- **Phase 4 — boot-time adopt-from-handoff (the no-EOF resume).** If `KGSM_WATCHDOG_HOTSWAP_HANDOFF` is set,
  `StartupRestorer` takes the handoff path **before** the normal restore: per entry, verify the inherited fd
  is still open (`fcntl(fd, F_GETFD) >= 0`), re-derive the spec, recover the PID from the cgroup, and build
  `RunningInstance.Adopt(name, inheritedFd, …)` using **the inherited fd directly — NOT `ReopenFifo`** (same
  continuously-open fd → no new inode → the game never saw its writer vanish → a post-swap `/quit` is honored
  immediately). Restore counters/phase/timing from the handoff; re-`SetCloexec` the inherited fd. Mark these
  done so the subsequent `RestoreAsync`/`AdoptLiveOrphans` skip them; then **unset the env var** and run the
  normal restore for anything enabled-but-not-running. **Per-entry graceful degradation:** an unexpectedly
  invalid fd falls back to `ReopenFifo` from disk (Option 1) for that one instance, logged as a downgrade.
  This phase is exactly where the Option 1 EOF gap closes.

- **Phase 5 — deploy + systemd.** Unit: add `ExecReload=/bin/kill -HUP $MAINPID` (type stays `simple`; the
  same-PID execve is transparent — no fd store needed). `deploy/deploy.sh`: add a hot path that (1) publishes
  the AOT binary to a staging file, (2) installs it as `<path>.new` **in the same dir then `mv -f` onto the
  live path** — `rename(2)` is atomic and leaves the running process on its old (now-unlinked) inode while the
  path points at the new one; **never `install`/`cp` over the running inode** (ETXTBSY / corrupts the mmap'd
  image), (3) `systemctl reload kgsm-watchdog`, (4) verify: poll `/version` until it reports the new build,
  confirm the **daemon PID is unchanged** (proves in-place swap, not a systemd restart), and confirm each
  instance is still `populated` with an unchanged PID. Keep the current stop→install→start as an explicit
  `--cold` fallback (and the existing failure-trap restart).

- **Phase 6 — tests + live validation.**
  - *Pure/unit:* `RestartTracker.Restore` round-trip; `PersistedSupervisionState` JSON round-trip (AOT
    context); handoff blob serialize/deserialize; argv-marshal builder; coordinator **abort path** (a failing
    `--selfcheck` leaves cloexec set + env unset + gate released). AOT publish 0-warn.
  - *Live (factorio-test, sacrificial):* spawn factorio → crash it a couple times to push the restart counter
    up → `deploy.sh` hot path. **Assert the headline wins:** (a) daemon PID **unchanged** across the swap;
    (b) **no new `Got EOF on stdin`** line in factorio's log at the swap (the exact thing Option 1 could not
    achieve); (c) a post-swap `/stop` is honored as a graceful `/quit` → "stopped gracefully", *not*
    `cgroup.kill`; (d) `/version` shows the new build; (e) the restart counter **carried across** the swap;
    (f) factorio PID unchanged (zero downtime).
  - *Safety gate, live:* stage a deliberately-broken binary (`--selfcheck` exits non-zero) → `systemctl
    reload` → assert the daemon **aborts**, stays on the old image, games untouched, loud log + event.
  - *Crash-resilience, live (Phase 2 payoff):* `kill -9` the daemon (not a swap) → `Restart=always` brings it
    back → assert counters are **restored from disk**.

**Open risks to watch (revisit during build):** (1) `SetEnvironmentVariable`→`execv` visibility on net10 —
verify in Phase 1, fallback is explicit `envp` marshalling. (2) A crash arriving *during* the quiesce window
— the gate serializes it and the successor's first reconcile re-evaluates against live cgroup state, so it
self-corrects; acceptable. (3) The <1 s control-socket blip — documented non-goal; preserving the Kestrel
listener across exec would be a much larger lift (raw socket out of Kestrel) and is explicitly out of scope.

Refs (Option 3 mechanics): [execve(2)](https://man7.org/linux/man-pages/man2/execve.2.html) (fd inheritance
& `FD_CLOEXEC` semantics), [fcntl(2)](https://man7.org/linux/man-pages/man2/fcntl.2.html) (`F_SETFD`),
[NGINX binary upgrade](https://0x0f.me/blog/nginx-zero-downtime-upgrade-code-analysis/),
[HAProxy seamless reloads](https://www.haproxy.com/blog/truly-seamless-reloads-with-haproxy-no-more-hacks).

---

### Increment 8 — systemd cgroup delegation (fix per-server memory metric)  ◀ BUILT + LIVE-VALIDATED (2026-06-25)

**Why (the bug).** kgsm-web's per-server **memory** chart read **0** while CPU
worked. Root cause (diagnosed + proven live on `hotrod`, 2026-06-25): the
watchdog's base `kgsm.slice` is a **`.slice`-named cgroup with no systemd unit**,
so systemd adopts it as an *implicit* slice it believes it owns (`Act. Units: 0`)
and, on **every `systemctl daemon-reload`**, resets `kgsm.slice/cgroup.subtree_control`
to empty — wiping the watchdog's `+cpu +memory +io +pids`. Consequence in the
per-instance cgroup:

- `cpu.stat` exists **unconditionally** in every cgroup v2 dir → CPU keeps working.
- `memory.current` / `pids.current` / `io.stat` exist **only when the controller is
  enabled in the parent's `subtree_control`** → they vanish → the monitor's
  `CgroupSampler` reads `memory.current`-missing as **0** (honest, not fabricated —
  the no-fabricate invariant held).

The pipeline (monitor → api `MetricsMapping` 1:1 → `MetricsPump` ~1s push on
`servers/{id}/metrics` → web) was never at fault.

**Empirical evidence (live, reproducible):**

| Test | Result |
|---|---|
| `daemon-reload` with the bare `kgsm.slice` | `subtree_control` `[cpu io memory pids]`→`[]`; `memory.current` vanishes. Reproducible. |
| `kgsm.slice` unit with `Delegate=yes` but **no tracked member** | Does **NOT** help — still wiped. |
| Base **not** named `*.slice` (e.g. `/sys/fs/cgroup/kgsm`) | Survives `daemon-reload` ×N (this was "Option B"). |
| **Delegated service, instances UNDER the service cgroup** (this increment) | systemd enables `[cpuset cpu io memory pids]` on the slice, **chowns the service cgroup + its `subtree_control` to the kgsm user**, and **leaves everything below the service untouched** across `daemon-reload` ×2. Robust. ✅ |

**Secondary nuance (must design around it):** enabling the memory controller on a
cgroup that *already* has a running process does **not** retroactively charge its
faulted pages (observed `memory.current` = 0 vs ~380 MB RSS). Accurate accounting
requires the instance to be **(re)spawned into a cgroup that already has the
controller** — which the delegated model gives for free on every spawn.

**Decision.** Host is **systemd-only** → drop openrc and the root-boot
self-bootstrap. Adopt **textbook systemd delegation**: the watchdog runs as a real
systemd service (`User=kgsm`, `Slice=kgsm.slice`, `Delegate=yes`) and manages each
instance cgroup as a child of **its own delegated service cgroup**
(`kgsm.slice/kgsm-watchdog.service/<inst>`), **not** as a sibling under
`kgsm.slice`. systemd then owns controller-enablement + ownership and stops
reconciling the delegated subtree. Chosen over Option B (rename base off `.slice`)
because it is the systemd-correct design *and* deletes the manual
create-slice/enable/chown/drop-privilege bootstrap. (Option B was the smaller
fallback; rejected since openrc — its only real advantage, one code path across
init systems — is gone.)

**Tasks** (all done 2026-06-25)

- [x] **Deploy/unit.** `deploy/kgsm-watchdog.service` is the single canonical delegated
      unit (`Type=simple`, `User=`/`Group=` templated by deploy.sh, `Slice=kgsm.slice`,
      `Delegate=yes`, `RuntimeDirectory=kgsm-watchdog` — safe now that no root→user drop
      happens). **Deleted** `kgsm-watchdog.openrc` + `kgsm-watchdog.rootless.service`.
      `deploy.sh` rewrites `User=`/`Group=` (was UID/GID) and installs the unit.
- [x] **Bootstrap rework (`CgroupBootstrap`).** Root-boot dance removed. New flow:
      discover the delegated base, create + enter the supervisor sub-leaf, enable
      controllers on the delegated base. No privilege step.
- [x] **Base discovery, not hardcode (`CgroupDiscovery` + `CgroupManager.UseResolvedBase`).**
      Base derived from `/proc/self/cgroup` at startup (folds the supervisor leaf back on a
      hot-swap re-exec); `CgroupBaseName` is now only a discovery-failure fallback.
- [x] **kgsm coordination = shared config, identical by construction.** kgsm
      `config_cgroup_base_name` → `kgsm.slice/kgsm-watchdog.service` (config.default.ini +
      migration `004_v3_to_v4` + core/cgroup.sh fallback); the watchdog logs the resolved
      base and the value kgsm must match. **Monitor unchanged** (reads `CgroupPath`).
- [x] **`kgsm system setup-cgroups` marked legacy** (help + config/cgroup.sh comments).
- [x] **Tests.** Watchdog 182 green incl. new `CgroupDiscoveryTests`; AOT publish 0-warn.
      kgsm: migration 004 tests + cgroup/merge/instances suites green.
- [x] **Live migration on hotrod.** Stopped factorio + old root daemon, removed the stale
      manual `kgsm.slice`, cold-deployed the delegated unit, restarted factorio. Validated:
      daemon in `kgsm.slice/kgsm-watchdog.service/supervisor`; `memory.current` = 345M from
      birth; monitor `memBytes` ≈ 343 MiB; **survives `daemon-reload` ×2**.
- [x] **Docs.** §4 rewritten, §6 tree updated, this increment + README, openrc refs removed.

**Result.** The original bug is durably fixed: `systemctl daemon-reload` no longer wipes
the controllers (systemd leaves the delegated subtree below the service alone), so
per-server memory survives. The hotrod hand-stopgap (controllers re-enabled on
`kgsm.slice`) is superseded by this real fix.

**Rootless locked in as canon (2026-06-25 follow-up).** The runtime is fully rootless and
now *enforced* (validated live on hotrod): daemon + games run as the unprivileged user with
`NoNewPrivs=1`, empty `CapabilityBoundingSet`, `CapEff=0`. Added `KillMode=process` so games
(now under the service cgroup) outlive a daemon stop/restart and are re-adopted — without it
the default `control-group` would kill every game on restart. Removed the dead root-drop
config surface (`KGSM_WATCHDOG_UID/GID/HOME`, `TargetUid/Gid`, the geteuid/chown/setres*
interop). Known caveat (see §4): a daemon cold-restart/crash re-charges a *surviving* game's
`memory.current` from zero (self-heals on next game restart); hot-swap and fresh spawns are
unaffected.

---

### Increment 9 — `instance-ready`: readiness detection distinct from `instance-started`  ◀ BUILT (2026-07-04)

**Why.** `instance-started` fires at spawn time (process exec) — it says nothing about whether the
game has actually *finished booting* and is joinable. The Control Panel needs a separate, honest
"ready" signal to flip a server from "Starting" to "Running". Deferred: port-based readiness (only
log-pattern + the honest empty-pattern fallback are built).

- [x] **Home: `NativePlayerPresenceIngester` doubles as the readiness ingester** (not a new
      hosted service) — it already polls every native instance's config + tails its log once per
      tick, so adding a second detector to the same pass avoids a second file-read path and a second
      per-instance config cache. New pure sibling `NativeReadinessMatcher` (mirrors
      `NativeLogMatcher`'s 100 ms ReDoS-guard + honesty rules) compiles `Instance.StartupSuccessRegex`.
- [x] **Start-edge = the authoritative re-arm signal**, not log rotation: the instance's cgroup
      transitioning not-populated → populated (`CgroupManager.IsPopulated`, injected into the
      ingester — the same child-inclusive liveness signal `CrashWatcher` already polls). This is
      universal across every existing spawn path (`StartAsync`, `RespawnFresh`,
      `ReconcileRestartPending`, daemon-restart re-adopt) with **zero changes to
      `InstanceSupervisor`** — fully decoupled from supervision, exactly like presence detection
      already is. Also re-arms defensively on `EventChannelTail.LastReadResetSession`.
      **Known divergence found along the way — FIXED in 1.6.1, see Increment 9a below:** the bash
      reference truncates `instance_log_file` to a fresh inode on every start (`_rotate_log_file` +
      `&>` in `manage.native.d/03-lifecycle.sh`); `SpawnEngine` instead appended (`>>`) forever,
      never rotating — which is exactly why the cgroup-populated edge, not
      `LastReadResetSession`, had to be the primary signal. This divergence turned out to be more
      than cosmetic: it let the whole-file late-attach scan below resurrect a stale prior-run ready
      line on an instance's 2nd+ start (Increment 9a).
- [x] **Empty pattern → honest immediate fallback** (ready = observed started, no fabricated delay);
      **invalid (non-empty) pattern → disabled + warned, never silently substituted** with the
      immediate rule (a real blueprint bug shouldn't be papered over).
- [x] **Late-attach gotcha**: `primeAtEnd` means a normal tail attach after the ready line already
      went by would miss it. Fixed with a one-shot whole-file scan on the start edge
      (`NativeReadinessMatcher.MatchesExistingContent`, the .NET analog of the bash reference's
      `watchers.logs.sh` `__logic_test_log_pattern` whole-file `grep -q`), read independently of the
      shared `EventChannelTail` so it never perturbs its offset/inode bookkeeping.
- [x] **Widened the enable/skip gate**: an instance with a readiness pattern but no
      `player_joined_regex`/`player_left_regex` (factorio/minecraft/terraria-shaped) is no longer
      skipped — the gate is now "has player patterns OR a valid readiness pattern OR the immediate
      fallback applies".
- [x] Wire: `events.EmitWithProvenance("instance-ready", "system", "system", [name])` — already
      registered downstream (kgsm `events.sh`, kgsm-lib `InstanceReadyData`), so this only adds the
      emitter.
- [x] Tests: +11 (`NativePlayerPresenceIngesterTests` readiness section + new
      `NativeReadinessMatcherTests`) — immediate fallback, pattern-based fire-once-and-rearm across a
      simulated crash-restart, not-skipped-without-player-patterns, late-attach whole-file scan, and
      invalid-pattern honesty (disabled but doesn't take out an unrelated valid player pattern on the
      same instance). Full suite 223/223 green; AOT publish 0 IL2026/IL3050/ILC warnings.
- **Out of scope (deferred):** port-based readiness; a `starting` phase in the watchdog's own state
  model (the Control Panel API owns that state — the watchdog's only job is the correct
  `instance-ready` emit); fixing `SpawnEngine`'s log-append-not-rotate divergence from the bash
  reference (see Increment 9a — this turned out NOT to be cosmetic, and was fixed same-day).

---

### Increment 9a — fix: `SpawnEngine` rotates the log on every fresh spawn  ◀ BUILT (2026-07-04, v1.6.1)

**The bug.** Increment 9's whole-file late-attach scan (`NativeReadinessMatcher.MatchesExistingContent`)
reads the ENTIRE current log on every fresh-spawn start edge, to catch a ready line that was already
logged before the edge was observed. Combined with the append-not-rotate divergence noted above, this
meant the scan saw not just the current run's content but every PRIOR run's too: on an instance's 2nd
and every later start, it re-matched the previous run's already-logged ready line and fired
`instance-ready` immediately — collapsing the honest "Starting" window and reporting a not-yet-booted
server as ready. Live-validated on a real Factorio instance: 1st boot correctly held "starting" for
10-20s through a full cold boot; the 2nd start (after a stop/start cycle) fired ready instantly, before
the game had even re-loaded its save.

- [x] **Fix: `SpawnEngine.RotateLogFile`** (`src/Watchdog/Supervision/SpawnEngine.cs`) — called from
      `Spawn` right after the log directory is ensured, before any cgroup/FIFO side effect. Mirrors the
      bash reference's `_rotate_log_file`: a non-empty pre-existing log is `mv`'d to a timestamped
      sibling (`<name>.<timestamp>.log`, collision-suffixed with ticks in the rare same-second case) —
      a rename, not an in-place truncate, so the launcher's `>> log` always lands on a genuinely fresh
      inode. Best-effort/non-fatal: an absent or already-empty log is a silent no-op; a failed rotate
      (permissions) is logged and swallowed rather than aborting the spawn.
- [x] **Spawn-only, never adopt/hot-swap — a structural guarantee, not a runtime check.**
      `InstanceSupervisor.TrySpawn` is the ONLY caller of `SpawnEngine.Spawn` (from `StartAsync` —
      manual start, `RespawnFresh` — boot bring-up of a dead instance, and
      `ReconcileRestartPending` — crash-restart); neither adopt path (`AdoptLive` /
      `AdoptFromHandoff`/`AdoptHandoffEntry` — hot-swap re-attach and cold-restart re-attach to a
      still-live game) calls `Spawn` or `TrySpawn` at all — they only call `SpawnEngine.ReopenFifo`.
      So a still-writing live game's log is never touched by construction, not by a conditional guard
      that could be gotten wrong.
- [x] **`EventChannelTail` reset behavior confirmed for the fresh-inode choice over in-place
      truncate:** a fresh inode is the PRIMARY, clean trigger (`ReadNewLines`'s `_inode != inode`
      branch fires `LastReadResetSession = true` on any attach past the first) — this is what re-arms
      both the readiness latch and the player-presence session map cleanly on the very next read.
      An in-place truncate only benefits from the tail's secondary "shrink" safety net (`length <
      _offset`), a weaker path documented as a partial-coverage residual — confirming the fresh-inode
      rename was the right choice, not just a style preference.
- [x] Tests (+8, full suite 231/231; AOT publish 0 IL2026/IL3050/ILC warnings):
  - `SpawnEngineTests` — `RotateLogFile` moves a non-empty log to a fresh-inode sibling and preserves
    its content; no-ops on an absent or already-empty log; never throws (missing directory).
  - `NativePlayerPresenceIngesterTests` — a 2nd fresh spawn (simulated by calling `RotateLogFile`
    directly, the way `Spawn` does, since the fork itself needs a real cgroup and is exercised live not
    in the unit suite) does NOT resurrect run 1's stale ready line, and still fires ready honestly once
    run 2's OWN ready line appears; the same for a stale player-join line.
  - `AdoptDoesNotRotateLogTests` (new file) — hot-swap `AdoptFromHandoff` and cold-restart
    `AdoptLiveOrphansAsync` both leave a real log file byte-for-byte unchanged at the SAME inode,
    proving the spawn/adopt distinction holds in practice, not just by code inspection.
- **Owed:** live re-validation on hotrod's Factorio instance across a real stop/start cycle (the
  daemon has not yet been redeployed with this fix).

---

## 8. Open questions / risks

- **kgsm-lib distribution.** Monitor uses `PackageReference ...KGSM.Lib 1.1.0`;
  ecosystem finding #1 flags "stranded lib distribution" (no clean feed). The
  watchdog needs the package available (local pack or feed) — confirm before Inc 1.
- **net9 vs net10.** kgsm-lib targets net9.0; the monitor (net10.0) consumes it
  fine. Watchdog → net10.0, consume the net9.0 package. Verify AOT publish is clean.
- **Spawn ownership detail.** The daemon owns fork+exec (so the game is its in-slice
  child); it must replicate the FIFO/keepalive/log wiring of `manage.native.d`
  faithfully — and the keepalive `tail`/FIFO writer must stay **outside** the
  cgroup or `populated` never clears.
- **Graceful-stop vs. `cgroup.kill` ordering** and the drain timeout
  (`stop_command_timeout_seconds`) — reuse KGSM's existing semantics.
- **Control-socket authority.** The socket can start/kill game servers → privileged
  surface; gate by FS perms (owner/group), same model as the monitor. No in-daemon
  authn; that's the surfaces' job.
- **Container instances** stay Docker-managed (Docker owns their cgroup + restart);
  the watchdog only supervises **native** instances. Be explicit so the daemon
  no-ops on container/systemd kinds.
- **Watchdog↔kgsm cgroup-base coordination (Inc 8).** Under systemd delegation the
  base is `kgsm.slice/kgsm-watchdog.service` (coupled to the unit name), discovered
  at runtime. kgsm must surface the *same* per-instance path in `Instance.CgroupPath`
  or the monitor samples the wrong dir. Settle the contract: who is the authority for
  the base path, and how kgsm learns it (report-from-daemon vs shared config vs
  identical derivation). The monitor is downstream of `CgroupPath` and needs no change.

## 9. Source-of-truth pointers

- cgroup foundation + Increment 0 (bash side) → `kgsm/docs/specs/cgroup-supervision-plan.md`
- monitor conventions to mirror → `kgsm-monitor/PLAN.md`, `src/Monitor/*`
- kgsm-lib consumable API → `kgsm-lib/CLAUDE.md`, `Core/Models/Instance.cs`,
  `Core/Interfaces/*`, `Json/KgsmJsonContext.cs`
- ecosystem topology + invariants → `system-architecture.md` (the keystone)
