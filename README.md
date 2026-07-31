# kgsm-watchdog

The resident **KGSM supervisor daemon**: it owns the `kgsm.slice` cgroup, spawns native
game-server instances *into* per-instance cgroups, and (from Increment 2) supervises them with
crash detection + restart. CLI / kgsm-lib / the Discord bot become thin clients that issue
lifecycle commands over a unix socket — so KGSM can manage standalone (non-systemd) servers
headlessly, with **zero per-operation privilege escalation**.

It deliberately breaks KGSM's historical "stateless / no warm process" rule, eyes-open, because
the daemon **is** the watchdog the project always wanted. See [`PLAN.md`](PLAN.md) for the full
rationale and increment roadmap, and `../kgsm/docs/specs/cgroup-supervision-plan.md` for the
cgroup foundation (Increment 0, in the `kgsm` repo).

> **Status: Increment 2 — crash detection + restart.** Spawn/stop one native instance into its
> cgroup over the control socket (Inc 1), and **supervise it**: a 1 Hz crash watcher detects cgroup
> `populated`→0 while desired-running and restarts with **exponential backoff + give-up** (consecutive
> failures since last stability ≥ maxRetries → `phase=failed`). Restart policy is configurable —
> **`always`** by default (any exit restarts; only a deliberate `stop` keeps it down), or `on-failure`
> (leave clean code-0 exits stopped).
>
> **Increment 3 — clients + boot integration (done).** kgsm-lib ships a typed
> `IWatchdogClient`; `kgsm start|stop|restart|is-active` (native standalone) auto-routes to the daemon
> when present (falling back to direct spawn when absent); three boot variants ship in `deploy/`; and the
> Discord bot consumes kgsm-lib 1.2.0 (from the local feed) — start/stop/restart route to the daemon
> transparently via `kgsm.sh`, and a read-only `/supervision` command surfaces the daemon's supervision
> state. Only the literal Discord-token-in-the-loop run is left to the operator.
>
> **Increment 5 — boot persistence (done).** The in-house replacement for systemd's
> `systemctl enable` + `WantedBy=` — and the prerequisite for the planned systemd hard-break. The
> enabled-for-boot set is persisted to disk (`KGSM_WATCHDOG_STATE_FILE`, default under the KGSM user's
> data dir) — written by `enable`/`disable` (independent of runtime `start`/`stop`) — and **restored on
> daemon startup**: an instance whose cgroup is still
> live (survived a *daemon* restart) is **re-adopted** without a respawn; one whose cgroup is empty (a
> *host reboot*) is **spawned fresh**. So a reboot brings every previously-running native instance back
> up, with no systemd unit involved. Unit-verified (55 tests, AOT-clean) and **validated as root against a
> live `7dtd` server** — `deploy/validate-increment4.sh` exercises persist → adopt → respawn → prune
> across four real daemon restarts (13/13 checks pass).

## Architecture (sibling of kgsm-monitor)

- **.NET 10, Native AOT** — same shape as `kgsm-monitor`: `WebApplication.CreateSlimBuilder` →
  Kestrel `ListenUnixSocket`, reflection-free source-gen JSON, `FromEnvironment()` options.
- **The daemon ACTS; the monitor MEASURES.** It *writes* cgroup v2 files (`CgroupManager`, a C#
  port of `kgsm/core/cgroup.sh`) where the monitor only reads them.
- **kgsm-lib is the only path to KGSM** (`PackageReference TheKrystalShip.KGSM.Lib`). Used to
  *read* instance config; never to start/stop (that path spawns detached — what this replaces).
- **Spawn = self-move launcher.** A `/bin/sh -c` launcher does `echo $$ > <inst>/cgroup.procs`
  before `exec`, so the game and every child it forks are born inside the instance cgroup —
  nothing escapes `cgroup.events` liveness or `cgroup.kill` teardown. The daemon holds the FIFO
  open itself (no `tail` keepalive process).

## Build / test

```bash
dotnet build kgsm-watchdog.slnx
dotnet test  kgsm-watchdog.slnx
dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64   # expect 0 ILC warnings
```

## Run

The daemon runs under **systemd cgroup delegation** — as the KGSM user, no root, no privilege
drop. systemd places it in `kgsm.slice` (`Delegate=yes`) and hands it the
`kgsm.slice/kgsm-watchdog.service` subtree; the daemon discovers that base from
`/proc/self/cgroup` and creates per-instance cgroups under it. Use the deploy script (below) —
it installs the unit and starts the service. (Running the binary by hand outside a delegated
cgroup reports `/health` 503 with the reason.)

Control plane (HTTP/1.1 over the unix socket — `curl --unix-socket`):

```bash
S=/run/kgsm-watchdog/control.sock
curl --unix-socket $S http://x/health    # readiness: 200 ready / 503 + reason (/ready = deprecated alias)
curl --unix-socket $S -X POST http://x/start/my-server
curl --unix-socket $S http://x/status/my-server
curl --unix-socket $S http://x/list
curl --unix-socket $S -X POST http://x/stop/my-server
```

The watchdog supervises **native standalone** instances only; it no-ops on systemd/container
instances (those are owned by systemd / Docker).

## Deploy

systemd-only, one unit ([`deploy/kgsm-watchdog.service`](deploy/)): `User=`/`Group=` (the KGSM
user, templated by `deploy.sh`), `Slice=kgsm.slice`, `Delegate=yes`. Configuration is shared via
[`kgsm-watchdog.env.example`](deploy/kgsm-watchdog.env.example).

```bash
./deploy/setup.sh            # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh           # hot-swap if running (zero-downtime re-exec), else cold install
./deploy/deploy.sh --cold    # force stop → install → start
```

`setup.sh` provisions the host: `/opt/kgsm-watchdog` chowned to you, the real unit installed into
user-owned `/etc/kgsm-watchdog/systemd/` with `/etc/systemd/system/kgsm-watchdog.service` symlinked
to it, a polkit rule scoped to this project's units, the unit enabled — then it verifies the grant
with the same unprivileged `systemctl` calls the deploy will make.

`deploy.sh` then needs **no privilege and asks nothing**: installing a binary and refreshing a unit
are plain file writes into directories you own, and the `systemctl` verbs (including the hot-swap
`reload`) go through the polkit grant. On an unprovisioned host it stops before building and tells
you to run `setup.sh`.

systemd creates `kgsm.slice` + the delegated `kgsm.slice/kgsm-watchdog.service` subtree, enables
the controllers, and chowns the subtree to the user — so per-instance cgroups (and their
`memory`/`pids`/`io` controllers) live below the service cgroup and **survive `daemon-reload`**.
(Children must NOT sit as siblings under `kgsm.slice`: systemd reconciles a slice's own
`subtree_control` and would strip their controllers — the bug PLAN Increment 8 fixes.)

**Fully rootless, enforced.** The daemon and every game it spawns run as the unprivileged
`User=` with **zero capabilities** — no root step, no privilege drop (systemd's `User=` runs
it directly; the delegated subtree is owner-writable, needing no privilege). The unit locks
this in with `NoNewPrivileges=yes` + `CapabilityBoundingSet=` (empty). It also sets
`KillMode=process` so games (which live under the service cgroup) **survive a daemon
stop/restart** and are re-adopted, rather than being killed with the daemon. Stop a *game*
with `kgsm stop <instance>` — stopping the daemon leaves games running. (One caveat: a daemon
*cold* restart/crash re-charges a surviving game's memory metric from zero until it next
restarts; hot-swap deploys and host reboots are unaffected — see PLAN §4.)

## Clients (Increment 3)

C# consumers reach the daemon through **kgsm-lib's** typed `IWatchdogClient`
(`AddKgsmWatchdogClient(socketPath)`) — `start`/`stop`/`status`/`list`/`ready` over the
control socket, source-gen JSON, AOT-safe — keeping all watchdog integration in the one
KGSM chokepoint. On the bash side, `kgsm start|stop` for **native standalone** instances
auto-routes to the daemon when its socket is present and `/health` is 200, and falls back to
the legacy direct-spawn path when it is absent (so installs without the daemon are
unchanged). See the kgsm repo's `commands/handlers/watchdog.sh`.

## Configuration

All configuration is via environment variables (idiomatic for a systemd/init daemon — set them in
the unit's `Environment=` / `EnvironmentFile=`). **The compiled binary is self-documenting** — it
prints the full list with live defaults, so config is never invisible to an operator:

```bash
kgsm-watchdog --help
```

A ready-to-edit template ships as [`deploy/kgsm-watchdog.env.example`](deploy/kgsm-watchdog.env.example)
(point the unit at it with `EnvironmentFile=`). Only **`KGSM_WATCHDOG_KGSM_PATH` is required**;
everything else has a working default. A misspelled `KGSM_WATCHDOG_*` var is logged as a warning at
startup (it would otherwise silently fall back to its default).

| Env | Default | Meaning |
|---|---|---|
| `KGSM_WATCHDOG_KGSM_PATH` | *(required)* | absolute path to `kgsm.sh` (read via kgsm-lib for spawn config) |
| `KGSM_WATCHDOG_KGSM_SOCKET` | `/run/kgsm-watchdog/events.sock` | kgsm-lib event socket |
| `KGSM_WATCHDOG_SOCKET` | `/run/kgsm-watchdog/control.sock` | control unix-domain socket path |
| `KGSM_WATCHDOG_SOCKET_MODE` | `0660` | octal perms applied to the control socket |
| `KGSM_WATCHDOG_CGROUP_MOUNT` | `/sys/fs/cgroup` | cgroup v2 mount point |
| `KGSM_WATCHDOG_CGROUP_BASE` | `kgsm.slice` | fallback base only; the real base is discovered from `/proc/self/cgroup` under delegation (= `kgsm.slice/kgsm-watchdog.service`) |
| `KGSM_WATCHDOG_CGROUP_CONTROLLERS` | `cpu memory io pids` | controllers enabled on the base subtree |
| `KGSM_WATCHDOG_SUPERVISOR_LEAF` | `supervisor` | leaf cgroup the daemon itself lives in (under the delegated base) |
| `KGSM_WATCHDOG_POLL_INTERVAL_MS` | `1000` | how often each instance's `cgroup.events` is polled |
| `KGSM_WATCHDOG_RESTART_POLICY` | `always` | `always` = restart any exit; `on-failure` = leave clean code-0 exits stopped |
| `KGSM_WATCHDOG_RESTART_BASE_DELAY_MS` | `1000` | first-restart delay; doubles each consecutive failure |
| `KGSM_WATCHDOG_RESTART_MAX_DELAY_MS` | `60000` | ceiling on the exponential delay |
| `KGSM_WATCHDOG_RESTART_MAX_RETRIES` | `5` | consecutive failures before giving up (`phase=failed`) |
| `KGSM_WATCHDOG_RESTART_STABILITY_SEC` | `300` | uptime after which the failure streak resets |
| `KGSM_WATCHDOG_RESTART_GRACE_SEC` | `10` | post-spawn window where crash-detection is suppressed |
| `KGSM_WATCHDOG_STATE_FILE` | *(`~/.local/share/kgsm-watchdog/desired-state.json`)* | boot-autostart (enabled) set persisted here + restored on boot (replaces systemd `enable`/`WantedBy`) |

A manual `start` clears a `failed` instance's give-up latch. A deliberate `stop` is never restarted.

> **Restart policy.** Default **`always`**: any exit while the instance is desired-running is
> restarted — the only way to keep a server down is to `stop` it through the watchdog. This suits game
> servers, whose exit codes are an unreliable crash signal (many exit **0** even on a fatal error).
> Set `KGSM_WATCHDOG_RESTART_POLICY=on-failure` for systemd-style semantics, where a clean code-0 exit
> is treated as an intentional shutdown and left stopped (only non-zero / signal exits restart) — but
> note that then a server crashing with exit 0 will *not* come back.

## Boot persistence (desired-state file)

The daemon records which instances are *enabled for boot auto-start* in a small JSON file
(`KGSM_WATCHDOG_STATE_FILE`, default `~/.local/share/kgsm-watchdog/desired-state.json`) and restores
them on startup — the in-house replacement for `systemctl enable` / `WantedBy=`. On boot each listed
instance is **re-adopted** if its cgroup is still live (a *daemon* restart) or **spawned fresh** if not
(a *host reboot*).

This is the **boot** axis, independent of the runtime start/stop axis (systemctl-style): `enable`/
`disable` control what comes back after a reboot; `start`/`stop` control what runs right now. A
started-but-not-enabled instance will **not** survive a reboot; an enabled-but-stopped one **will**.

A version-controlled reference of the exact format ships as
[`deploy/desired-state.example.json`](deploy/desired-state.example.json):

```json
{
  "version": 1,
  "desiredRunning": [
    "7dtd",
    "factorio"
  ]
}
```

| Field | Meaning |
|---|---|
| `version` | on-disk schema version (currently `1`), carried for forward-compatible migration |
| `desiredRunning` | the instance names to auto-start on boot — **intent only**; each one's spawn config is re-read fresh from kgsm-lib on restore, so there is no stale-spec drift |

**The daemon owns this file** — it adds a name on `enable` and removes it on `disable`, so you normally
never edit it by hand; `disable` an instance through the watchdog to drop it from auto-start (`stop`
leaves it enabled). Writes are atomic (temp + rename), and a missing or corrupt file degrades to
"nothing to restore" rather than wedging boot. To pre-seed auto-start before the daemon's first run, drop a file in this shape at the
configured path (keys are case-sensitive `camelCase`, and JSON comments are **not** permitted — a file
the parser rejects is treated as empty).
