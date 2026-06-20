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
> levels from `appsettings.json` `Logging` + env (`Logging__LogLevel__Default`, default
> `Information`). The `CreateSlimBuilder` host binds the section explicitly via `AddConfiguration`.

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

The daemon is started once at boot and self-bootstraps its cgroup, then drops
privilege. Two entry contexts, one code path:

- **Started as root** (non-systemd init script, or a root systemd unit): create
  `kgsm.slice` + enable controllers + `chown` the subtree to the kgsm user
  (subsumes `kgsm system setup-cgroups`), create + enter `kgsm.slice/supervisor`,
  then **drop to the kgsm user** (uid change does NOT change cgroup membership — it
  stays in-slice). Everything after is unprivileged.
- **Placed by systemd with delegation** (`User=kgsm`, `Slice=kgsm.slice`,
  `Delegate=yes`): it's already in a writable delegated cgroup → detect that and
  **skip** the root bootstrap.

After bootstrap: forks are born in `kgsm.slice/supervisor`; per-instance moves to
`kgsm.slice/<inst>` are intra-slice and unprivileged. **No per-operation
escalation, ever.** The only privileged moment is boot — normal daemon behavior.

> Relationship to Increment 0's `kgsm system setup-cgroups`: kept as a manual /
> diagnostic tool and for non-daemon use, but the daemon's root-boot path performs
> the same delegation itself, so a separate setup run becomes optional.

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
    ├── kgsm-watchdog.service      (systemd: User=kgsm, Slice=kgsm.slice, Delegate=yes)
    └── kgsm-watchdog.openrc       (non-systemd init: root → enter slice → drop privs)
```

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

## 9. Source-of-truth pointers

- cgroup foundation + Increment 0 (bash side) → `kgsm/docs/specs/cgroup-supervision-plan.md`
- monitor conventions to mirror → `kgsm-monitor/PLAN.md`, `src/Monitor/*`
- kgsm-lib consumable API → `kgsm-lib/CLAUDE.md`, `Core/Models/Instance.cs`,
  `Core/Interfaces/*`, `Json/KgsmJsonContext.cs`
- ecosystem topology + invariants → `system-architecture.md` (the keystone)
