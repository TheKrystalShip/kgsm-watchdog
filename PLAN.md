# kgsm-watchdog — Project Plan

**Status:** Planning · **Created:** 2026-06-12 · **Stack:** .NET 10, Native AOT

> The resident **KGSM supervisor daemon**. It owns the `kgsm.slice` cgroup, spawns
> native game-server instances *into* per-instance cgroups, and supervises them
> (crash detection + restart). CLI / kgsm-lib / the Discord bot become **thin
> clients** that issue lifecycle commands over a unix socket. This deliberately
> breaks KGSM's historical "stateless / no warm process" rule — accepted
> eyes-open, because the daemon **is** the watchdog the project always wanted.

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

### Increment 2 — watchdog behavior: crash detection + restart
- [ ] Desired-state table (running/stopped) keyed by instance.
- [ ] `CrashWatcher`: `poll()` per-instance `cgroup.events`; populated→0 with
      desired=running ⇒ crash.
- [ ] `BackoffPolicy`: exponential backoff + flap rate-limit + give-up/"failed".
- [ ] Distinguish clean exit vs. crash where possible (exit status / stop-requested).
- **Exit:** a crashed standalone instance restarts; a deliberately-stopped one
  does not; a crash-loop gives up and reports "failed".

### Increment 3 — clients + boot integration (in the `kgsm` repo + here)
- [ ] kgsm-lib: typed `IWatchdogClient` for the control socket.
- [ ] `kgsm.sh start/stop` (native) route to the daemon when present; the bash
      `manage.native.d` direct-spawn path is superseded for native.
- [ ] systemd-native: drop `service.tp` `Restart=on-failure` (no dueling
      supervisors); add `Delegate=yes` where systemd hosts the daemon.
- [ ] Ship `deploy/` units for the three boot variants (init script / systemd /
      rootless `enable-linger`).
- **Exit:** the Discord bot can start/stop/auto-restart a native server end-to-end,
  headless.

### Increment 4 — cross-repo finish (additive)
- [ ] kgsm-lib: `Instance.CgroupPath`; register in `KgsmJsonContext`.
- [ ] kgsm-monitor: native folds into the cgroup kinds (reads the watchdog's
      cgroups → fixes CPU/mem accuracy + PID-reuse fragility; `io.stat` still
      controller-gated; per-server net still uncovered).
- [ ] Keystone: add the supervisor to the topology, **amend §4 statelessness**,
      fix the §6 ledger + `kgsm-lib/docs/host-monitoring-inventory.md` native anchor.

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
