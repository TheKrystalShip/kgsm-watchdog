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

> **Status: Increment 1 — daemon skeleton.** Spawn/stop one native instance into its cgroup over
> the control socket. Crash detection + restart is Increment 2; CLI/lib/bot client wiring and boot
> integration is Increment 3.

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

## Run (Increment 1)

Boot as root once; the daemon delegates the slice, enters it, and drops to the KGSM user:

```bash
sudo KGSM_WATCHDOG_KGSM_PATH=/usr/local/bin/kgsm \
     KGSM_WATCHDOG_UID=$(id -u) KGSM_WATCHDOG_GID=$(id -g) \
     ./kgsm-watchdog
```

Control plane (HTTP/1.1 over the unix socket — `curl --unix-socket`):

```bash
S=/run/kgsm-watchdog/control.sock
curl --unix-socket $S http://x/ready
curl --unix-socket $S -X POST http://x/start/my-server
curl --unix-socket $S http://x/status/my-server
curl --unix-socket $S http://x/list
curl --unix-socket $S -X POST http://x/stop/my-server
```

The watchdog supervises **native standalone** instances only; it no-ops on systemd/container
instances (those are owned by systemd / Docker).
