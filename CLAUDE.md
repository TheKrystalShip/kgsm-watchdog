# CLAUDE.md

The resident KGSM supervisor daemon, the engine's stateful half and peer of `kgsm`: it owns the
`kgsm.slice` cgroup, spawns native standalone game servers into per-instance cgroups, holds desired-state
and does crash-restart and boot-autostart. Clients issue lifecycle verbs over a unix socket. .NET 10,
Native AOT. The daemon acts where `kgsm-monitor` only measures. Role, contracts and peers:
`../kgsm-docs/components/kgsm-watchdog.md`; model: `../kgsm-docs/systems/engine/watchdog.md`.

Repo docs: `PLAN.md` (design), `README.md` (operator summary), `docs/internals.md` (control plane,
supervisor, boot sequence, spawn, hosted services, state files).

## Build / test / publish

```bash
dotnet build  kgsm-watchdog.slnx                                    # .slnx, not .sln
dotnet test   kgsm-watchdog.slnx
dotnet test   kgsm-watchdog.slnx --filter "FullyQualifiedName~CgroupManagerTests"   # one class/test
dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64 # AOT: expect 0 IL2026/IL3050/ILC warnings
```

The AOT publish (or a clean `dotnet build`) stays warning-free: an `IL2026`/`IL3050`/`ILC` warning means
something reaches for reflection and fails at runtime. The project and its engine-core dependency are
`IsAotCompatible` with no reflection fallback.

The test project fakes the daemon's seam to the engine with `FakeInstanceCatalog` (an `IInstanceCatalog`
answering from a map a test fills in), so no test reads an engine file.

## Deploy and live validation

Bounce the daemon freely; prefer Factorio or Terraria for spawn tests. One canonical unit,
`deploy/kgsm-watchdog.service` (`Slice=kgsm.slice`, `Delegate=yes`).

```bash
./deploy/setup.sh            # once per host; asks for sudo, provisions and verifies the headless grant
./deploy/deploy.sh           # hot-swap if running (zero-downtime SIGHUP re-exec), else cold install
./deploy/deploy.sh --cold    # force stop, install, start
```

- Hot-swap: the new binary is `--selfcheck`ed, atomically `rename(2)`d onto the live path, and
  `systemctl reload` makes the daemon `execv` it in place with the same PID, carrying each game's
  stdin-FIFO fd across. Verify with the new `/version` and an unchanged `MainPID`.
- `deploy.sh` needs no privilege: `/opt/kgsm-watchdog` and the unit directory are yours and the
  `systemctl` verbs go through a scoped polkit rule. If another operation needs root, stop and ask;
  never reintroduce `sudo` here. The mechanism: the `deploying` skill.
- `deploy/validate-increment*.sh` are root-run end-to-end harnesses (persist, adopt, respawn, prune
  across real daemon restarts against a live game server): the regression net for boot and cgroup
  behaviour that unit tests cannot reach.

## Invariants for editing

- **The engine core is the only path to KGSM, and read-only here.** The watchdog reads each instance's
  spawn config, derived layout and resolved launch arguments through `IInstanceCatalog`
  (`Catalog/`, over `EngineReader`); it never starts or stops through the engine (that path spawns
  detached, outside this daemon's cgroups) and never runs the `kgsm` binary. The launcher receives the
  engine's resolved arguments one parameter each and expands nothing.
- **The memory gate's two settings are the daemon's own** (`Watchdog__EnableMemoryGate`,
  `Watchdog__MemoryGateHeadroomMb`); the engine holds no memory check. `GET /memory-gate` reports them.
- **Reflection-free JSON.** Every (de)serialized type is registered in `Model/WatchdogJsonContext.cs`; an
  unregistered type throws `NotSupportedException` at runtime.
- **Never fabricate state.** A status is measured (cgroup, `/proc`) or "unknown". A failed cgroup read is
  `CgroupPopulation.Unknown`, never empty. A run's age is `RunStartedAt` from `/proc`, never the
  supervision clock `SpawnedAt`. Details: `docs/internals.md`.
- **`InstanceSupervisor` is the single decision point.** Every state transition runs under its gate;
  `CrashWatcher` is only the clock. New transitions go there.
- **`KillMode=process` is load-bearing**: games live under the service cgroup and survive a daemon stop.
- **GC is tuned for an idle supervisor.** `Watchdog.csproj` forces Workstation, non-concurrent GC and
  `MemoryTrimmer` returns free pages after bursts. Do not flip these to defaults.
- **Naming:** namespace `TheKrystalShip.KGSM.Watchdog`, config section `Watchdog` (env overrides are
  `Watchdog__*`; only `Watchdog__KgsmPath` is required).
- **`kgsm-watchdog.settings.json` is the source of truth for every knob**, loaded explicitly from
  `AppContext.BaseDirectory`. A variable naming an undeclared key binds to nothing and is logged at
  startup as a probable typo.
- **`AddEnvironmentVariables()` runs after the settings file, and the order is load-bearing**: a file
  registered after the environment provider outranks it. `AddWatchdogConfiguration` owns the order for
  both the pre-host reads (`--selfcheck`, the required-knob check) and the host.
- **`KnownEnvVars` is derived from `WatchdogSettings`.** Adding a property is the only step that makes a
  knob real, documented in `--help` and exempt from the typo warning.
- **A knob lives in two places**: a `WatchdogSettings` property carrying `[ConfigField]` and a `<panel>`
  doc tag, and a key in `kgsm-watchdog.settings.json`. `deploy/kgsm-watchdog.leaf.json` is generated from
  them on every build, so edit the settings class, never the JSON, and commit what the build produces.
  The same build writes `deploy/kgsm-watchdog.leaf.actions.json` from `src/Watchdog/ActionDeclarations.cs`.
- **A config-key rename cannot be hot-swapped** (the `execv` inherits the old environment, so
  `--selfcheck` fails the required-knob check). Deploy those with `--cold`.

## Version tracking

- The version is `<Version>` in `src/Watchdog/Watchdog.csproj`; packaging reads it with
  `./deploy/version.sh` (`--pkgver` for the pacman form).
- Bump it for every user-facing change: patch for fixes, minor for features, major for breaking changes.
- Add a `CHANGELOG.md` entry under `## [Unreleased]` for every meaningful change.
- Tag the bump commit `git tag -a v<version> -m "kgsm-watchdog v<version>"`.

## Read before changing

- `../kgsm-docs/systems/engine/watchdog.md`: the daemon's role and the control socket contract.
- `../kgsm-docs/systems/operations/maintenance-windows.md` and `reference/maintenance-windows.md`: the park.
- `../kgsm-docs/systems/operations/firewall-and-ports.md`: router forwards and the firewall authority.
- `../kgsm-docs/reference/leaf-config-descriptor.md` and `systems/components/`: the descriptor and action
  declarations; mechanism in `../kgsm-componentconfig/README.md`.
- `../kgsm-docs/reference/logging-convention.md`.
- `../kgsm-docs/decisions/0022-watchdog-is-the-resident-exception.md`.

Docs and comments describe the system as it is now: present tense, no history, no emoji, no volatile
numbers (workspace `../CLAUDE.md`).
