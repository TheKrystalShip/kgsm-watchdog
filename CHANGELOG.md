# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **The daemon no longer watches the entire filesystem, and no longer starves the games it supervises
  of inotify watches.** The host builder's content root defaulted to the process working directory,
  which under this unit is `/`, and the builder's own `appsettings.json` providers watch that root
  *recursively* for reload — one inotify watch per directory, ~165k of the 524k per-user budget, held
  for the daemon's lifetime and growing as supervised games created directories. Game servers draw on
  the same per-user budget: with the budget exhausted, Project Zomboid's `DebugFileWatcher` could not
  register a watch, threw, and terminated during boot — with exit code 0, which under the default
  `always` restart policy is a restart. The daemon then restarted it forever, each attempt creating
  more directories and taking more watches. The content root is now pinned to `AppContext.BaseDirectory`,
  and the unit sets `WorkingDirectory=` so the working directory is the install prefix rather than `/`.
- **An instance installed while the daemon is running now gets its real readiness config.** The native
  presence/readiness watch is built the moment an instance's directory appears — which, for an install
  in progress, is several seconds before kgsm has written the instance's config — so the first read saw
  no `startup_success_regex` and no log file and settled on the immediate-readiness fallback,
  permanently. That instance then reported `instance-ready` the instant it spawned, for the rest of the
  daemon's life, no matter what its blueprint declared. The config is now re-read on every fresh run
  (the cgroup start edge), so a run is always judged by the config it actually started with, and an
  operator editing a blueprint's regex sees it take effect on the next start. The tail is kept across a
  re-read unless the log path itself moved: it holds the cursor and session identity, and a replacement
  would prime at the end of the current file and skip what sits between.

### Changed — the leaf config descriptor is generated, not written
- **`deploy/kgsm-watchdog.leaf.json` is now written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `WatchdogSettings`. A knob lives in two places —
  the property and the settings-file key — instead of three, and the descriptor cannot describe a
  variable this leaf does not read: the `env` name is derived from the property's position under its
  bound section, and the default from the settings file itself. **Edit the settings class, not the
  JSON.**
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **`LeafDescriptorTests` is gone.** Every check it made — settings coverage in both directions, the
  field vocabulary, group and `dependsOn` references, enum values and defaults, bounds, floor-source
  order — now runs in the generator, at the point the file is produced rather than after, and in one
  implementation shared by every leaf instead of a copy per repo.
- The package is **build-only** and declares no dependencies: the attributes arrive as source and the
  generator reads this assembly's metadata in its own process, so nothing reaches the published
  output and this leaf gains no reflection.

- **`Watchdog__CgroupControllers` ships comma-separated** (`cpu,memory,io,pids`). The parser has
  always taken either spelling, but the settings file said spaces while the descriptor published
  commas as the default — so the Control Panel showed a default and a floor that disagreed for a
  value neither side was wrong about. The file, the coded default and the panel now all say the same
  thing, which is the one the panel writes.

### Added — the env template is held to the settings file
- **A test fails the build when `deploy/kgsm-watchdog.env.example` names a key
  `kgsm-watchdog.settings.json` does not declare.** The env file overrides the settings file one
  key at a time, so a variable naming an undeclared key binds to nothing — it reads as configuration
  and is inert. The template is the one copy of that file in version control, so it is the copy that
  can be checked. Commented lines count too, since a commented key is what someone uncomments;
  systemd's own directives quoted in the prose (`EnvironmentFile=`, `Delegate=`) do not, because they
  configure the unit rather than the leaf.

### Changed
- **`pairedApiKey` names the Control Panel API's renamed setting.** kgsm-api's environment
  variables are now spelled `Api__<Property>`, and this value is what the API resolves to warn that
  a change here has moved this leaf out of its reach. Naming the old key would have made that check
  silently find nothing and report the change as clean.

### Fixed — a knob written blank no longer takes the daemon down
- **Every number in the settings type is nullable, so "written blank" means unset.** Binding a blank
  value to a non-nullable `int` throws, which made a single stray `Watchdog__PollIntervalMs=` line in
  an env file a startup crash — for this daemon, one that leaves every native game server
  unsupervised until someone notices. A null one binds to `0`, silently discarding the coded default.
  Null now means unset and the coded default applies. A value that is present but is not a number
  still fails loudly, which is the point of typing it.

### Changed — configuration is bound from `kgsm-watchdog.settings.json`, which is now the source of truth

- **Every knob is declared in the settings file and bound to `WatchdogSettings`.** The file ships all
  19 keys with their defaults under a `Watchdog` section, and an environment variable overrides one of
  them by spelling its path with `__` (`Watchdog__KgsmPath`, `Watchdog__PollIntervalMs`). The
  hand-rolled `WatchdogOptions.FromEnvironment()` and the flat `KGSM_WATCHDOG_*` names are gone;
  `WatchdogOptions.FromSettings` normalizes bound values instead of reading the environment.

  A variable naming a key the file does not declare binds to nothing, so there is no longer a way to
  configure this daemon that is invisible in the file. Binding is source-generated (the binder
  generator is on under `PublishAot`), so the ILC pass stays at zero IL warnings.

- **`KnownEnvVars` is derived from `WatchdogSettings` rather than hand-listed.** The list that feeds
  `--help` and the startup typo warning was a parallel array that had to be edited alongside every new
  knob — precisely the kind of second copy that falls behind. Adding a property is now the only step.

- **`AddWatchdogConfiguration` owns the source order, and both readers use it.** `--selfcheck` and the
  required-knob check run before the host exists and previously read the environment directly; they
  now read the same file-then-environment stack the host does, so a selfcheck can no longer pass on
  configuration the daemon will not see.

- **Environment variables are registered after the settings file so they win.** Configuration resolves
  by source order, and the explicitly-loaded file was appended after everything the slim builder
  installed — including its own environment provider. This was already true of the logging-only file
  that preceded this change: `Logging__LogLevel__Default` could not in fact override the level the
  file set, despite the comment beside it saying it could.

- **`floorSources` lists the settings file first, where it belongs.** The descriptor's list is
  lowest-precedence-first, and `appsettings` was last — outranking the unit. Harmless while the file
  held only logging, but a file that declares every knob then overwrites the deployed values in the
  Control Panel's floor computation, which showed `Watchdog__KgsmPath` as empty while the daemon ran
  with a real path. A test pins the ordering, because nothing else catches it: the wrong order builds
  and runs fine, and only shows up as a wrong value on the Control Panel after a deploy.

- **A cadence below its floor is raised to the floor** instead of reverting to the coded default; the
  floor is the nearest legal value to what was asked for.

- The hot-swap handoff variable keeps its own prefix, which is a separate namespace from the
  `Watchdog__` config one — an internal IPC channel can no longer be mistaken for a config knob, so
  the explicit exclusion it needed is gone.

> **Deploying a config-key rename needs `--cold`.** The hot-swap `execv` inherits the live process's
> environment, which predates the rename, so `--selfcheck` on the new binary fails its required-knob
> check and the swap aborts rather than starting a daemon that cannot find its configuration.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. The watchdog consumes no events and
  needed no source change; the pin keeps it on the one lib version the ecosystem runs. No behaviour
  change.

### Removed — the KGSM event socket knob

- **`KGSM_WATCHDOG_KGSM_SOCKET` is gone.** It was vestigial: the watchdog registers no event
  handler and never resolves `IEventService`, so no socket was ever created at that path and
  nothing was ever delivered to it. It described a capability the daemon does not have, which is
  worse than describing nothing — an operator could point it somewhere and reasonably expect
  events to arrive. The watchdog reads instance config through kgsm-lib and consumes no engine
  events at all.

### Added — the Control Panel can configure this daemon
- **`deploy/kgsm-watchdog.leaf.json` declares every knob the watchdog reads** — all 20
  `KGSM_WATCHDOG_*` variables plus the standard logging level, grouped for display, each with its
  type, coded default, bounds, unit and risk. `deploy.sh` installs it into `/var/lib/kgsm/leaves/`,
  where kgsm-api scans for it and renders this daemon's configuration page. Nothing in kgsm-api
  needs to know about the watchdog for that to work.
- **A coverage test fails the build if the descriptor and the daemon disagree.** It scans the
  daemon's own source, so a knob added without a descriptor entry fails here, and a descriptor entry
  naming a variable the watchdog does not read fails here too — an override written for a variable
  nothing reads would otherwise be reported as applied while changing nothing. It also holds the
  descriptor to the same surface `--help` documents.
- The cgroup layout, both socket keys, the KGSM path and the instances directory are marked
  `wiring`; the desired-state file is `destructive` (repointing it orphans the set of instances that
  come back after a reboot). The control socket names `KGSM_API_WATCHDOG_SOCKET` as the API setting
  that has to move with it.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns
  `/opt/kgsm-watchdog` to the deploying user, seeds the env file, puts the real unit in
  `/etc/kgsm-watchdog/systemd/` with `/etc/systemd/system/kgsm-watchdog.service` symlinked to it,
  installs a polkit grant scoped to this project's units, enables the unit, and verifies the grant
  with the same unprivileged `systemctl` calls `deploy.sh` makes.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned. The hot-swap path is unchanged —
  `--selfcheck` gate → atomic `rename(2)` → `systemctl reload` → verify a new `/version` on an
  **unchanged** `MainPID` — it simply no longer escalates to do it.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

### Added
- **RCON player-presence poller** (`RconPlayerPresencePoller`). A new `BackgroundService`
  that polls game servers supporting Source RCON for connected players, detecting disconnects
  when the game server does not log them. For each native instance with RCON configured
  (`rcon_port` non-null, `rcon_password` non-empty), it periodically connects via RCON,
  executes the configured `players` command, diffs the result against the previous poll, and
  emits `instance-player-joined` / `instance-player-left` wire events — the same events the
  log-based `NativePlayerPresenceIngester` produces. Coexists with the log tail via the shared
  `PlayerSessionStore`; the store's dedup logic prevents double-counting. Per-instance poll
  interval is configurable (default 10s, minimum 5s).
- **RCON client in kgsm-lib** (`IRconClient`, `RconClient`). Minimal Source RCON protocol
  implementation — TCP, length-prefixed binary packets, auth handshake, command/response.
  AOT-safe (no reflection, no external dependencies). Lives in kgsm-lib so other consumers
  (bot, API) can use it.
- **RCON properties on `Instance` model**. `RconPort`, `RconPassword`, `RconPollIntervalSeconds`,
  `RconPlayersCommand` — materialized from the blueprint into the instance config.

### Fixed
- **A stop is never mistaken for a crash.** `Reconcile` now checks `DesiredRunning` before it classifies
  an exit: a record whose intent is "stopped" has its teardown completed (handle disposed, cgroup purged,
  phase `Stopped`, entry dropped) instead of being restarted — no exit-code read, no retry slot, no
  `instance-crashed` event. The intent axis existed but nothing in the reconcile path consulted it: the
  only things keeping a stopped instance down were incidental (the gate excludes reconcile *while*
  `StopAsync` runs, and `StopAsync` removes the table entry as its last act). Any stop that unwound in
  between therefore left a `Running`-phase record over a dead cgroup, which the crash path read as a
  crash — and since a timed-out graceful stop ends in `cgroup.kill`, the "crash" it saw was the
  watchdog's own SIGKILL (exit 137). Live symptom: a Team Fortress 2 server stopped from the web restarted
  itself one second after the daemon killed it.
- **A stop that has begun runs to completion.** `POST /stop/{name}` and `POST /restart/{name}` no longer
  bind to the request's cancellation token, and every wait inside `StopAsync` past the gate uses
  `CancellationToken.None` — the same rule `DELETE /instance/{name}` already followed. A stop drains for
  the instance's full `stop_command_timeout_seconds` before hard-killing, which routinely outlives a
  caller's HTTP timeout; a client hanging up mid-drain aborted the verb and produced exactly the
  half-stopped record above. Each wait is separately bounded, so running to completion cannot hang.

### Added
- **Deregistration verb — `DELETE /instance/{name}`.** The counterpart to `kgsm uninstall`: drops an
  instance from supervision entirely — the live table entry, its cgroup, its boot-autostart intent, and
  its persisted supervision counters. Without it there was **no way to un-supervise an instance**, so
  every uninstall leaked a `desired=running` record: the supervisor restart-looped a game whose install
  directory no longer existed (until the give-up latch caught it), and every `/list` consumer — the
  kgsm-api alert engine among them — kept mirroring a crash condition for a server nobody could act on
  or resolve. Idempotent: an unknown name is a `200` no-op (the instance's kgsm spec is normally already
  deleted by the time a caller deregisters, so validating it against kgsm-lib would reject exactly the
  case this serves). `409` only when the instance is still live after the stop attempt — deregistering
  then would orphan a process nothing supervises and nothing can stop. Deliberately **not** bound to the
  request's cancellation token: deregistering stops the instance first, which can take the full graceful
  -stop timeout, and letting a client's HTTP timeout abort that mid-drain left the instance half torn
  down AND still in the table — the very leak this closes. Its internal waits are each bounded, so
  running to completion cannot hang.

- **On-demand UPnP control surface — `GET /upnp/{name}`, `POST /upnp/{name}/open`, `POST
  /upnp/{name}/close`.** UPnP was a purely internal lifecycle side effect (open-on-start /
  close-on-stop); it is now a first-class, externally-drivable AND queryable surface, capability parity
  with kgsm-firewall's `ensure-open`/`remove`/`list`. A caller (kgsm-lib `IWatchdogClient` → the
  assistant / web) can open, close, or read an instance's router port-forwards on demand.
  - **List** queries the IGD directly (`upnpc -l`) and filters to the rows whose description equals the
    instance name (the `-e <name>` ownership tag) — the router lease is the source of truth, so the
    daemon keeps **no** in-memory UPnP registry that could drift. Honest by construction: a missing
    `upnpc`, no IGD on the network, or a timeout yields `state: "unavailable"` — **never** an empty
    `"queried"` list (which would fabricate the absence of forwards). Only output carrying upnpc's
    `Found valid IGD` marker is reported as `"queried"` (exit code alone is unreliable — upnpc prints
    "No IGD found" and still exits 0).
  - **Open** takes an optional `{"ports":[{start,end,protocol}]}` body to forward an explicit set
    instead of the instance's configured ports; **close** removes the instance's mappings. Both honor
    the instance's `enable_port_forwarding` gate (config is the authority — an external caller never
    force-forwards a gated-off server) and return an honest `outcome` of `applied` / `skipped` /
    `failed`, never a fabricated open. A confirmed open/close routes through `InstanceSupervisor`
    (`OpenUpnpAsync`/`CloseUpnpAsync`) and emits `instance-upnp-opened` / `instance-upnp-closed`
    stamped with the caller's `origin` (default `control`), not `system` — the upnpc shell-out runs
    **outside** the supervisor gate so it never stalls a lifecycle verb or reconcile. New `UpnpMapping`
    / `UpnpListResult` / `UpnpActionResult` / `UpnpOpenRequest` DTOs, registered reflection-free in
    `WatchdogJsonContext`; the `upnpc -l` parser is pure + unit-tested against captured real miniupnpc
    output. 0 IL2026/IL3050/ILC warnings on AOT publish.
- **`ContainerLifecycleIngester` — the watchdog's second container role: container UPnP via
  self-reported lifecycle.** Peer of `PlayerPresenceIngester`, tailing a different channel in the same
  bind-mounted `/run/kgsm` dir: `<instances>/<blueprint>/<instance>/events/lifecycle.ndjson` (the
  kgsm-containers Phase 1 in-image `_emit_lifecycle` NDJSON channel — `instance_started`/
  `instance_stopping`). On `instance_started` it resolves the instance (`IInstanceService.GetInstanceInfo`)
  and calls the existing `UpnpService.OpenAsync`; on `instance_stopping`, `CloseAsync`. Only acts on
  **container**-runtime instances (native UPnP stays `InstanceSupervisor`-driven — never double-driven);
  never shells `docker`; UPnP-only — does **not** emit kgsm wire events (the container's own manage.sh
  already does). New `ContainerLifecycleLine` model + pure `ContainerLifecycleParser`, both registered
  reflection-free in `WatchdogJsonContext`. New knob `KGSM_WATCHDOG_CONTAINER_LIFECYCLE_POLL_MS`
  (default `1000`, same as the presence poll). 27 new tests (parser + ingester, discovery/resolve/
  end-to-end against a real never-shelling `UpnpService`). 0 IL2026/IL3050/ILC warnings on AOT publish.
  **Known limitation (see kgsm-containers CHANGELOG):** the in-container trap that emits
  `instance_stopping` is unreachable once the game's `exec` replaces the bash process — so in practice
  `instance_started` (and its UPnP open) fires reliably, but `instance_stopping` (and its UPnP close)
  only fires if a signal arrives during the container's pre-exec setup window, not the common
  steady-state `docker stop`. UPnP ports may stay forwarded after most container stops until this is
  addressed (tracked, not fixed here — would need a launch-mechanism change out of this increment's scope).

## [1.6.1] - 2026-07-04

### Fixed
- **`instance-ready` false-positive on an instance's 2nd+ start.** `SpawnEngine` appended a native
  instance's stdout to the same `Instance.LogFile` forever, never rotating it (a divergence from
  kgsm's bash reference, `_rotate_log_file` in `manage.native.d/10-logging.sh`, noted but left open in
  1.6.0). Because `NativeReadinessMatcher.MatchesExistingContent`'s one-shot whole-file late-attach
  scan re-reads the ENTIRE current log on every fresh-spawn start edge, on the second and every later
  start of an instance it re-matched the PREVIOUS run's already-logged ready line and fired
  `instance-ready` immediately — collapsing the honest "Starting" window and reporting a not-yet-booted
  server as ready.
  - Fix: `SpawnEngine.Spawn` now rotates a non-empty pre-existing log to a timestamped sibling
    (`SpawnEngine.RotateLogFile`, mirroring `_rotate_log_file`'s `mv`, not an in-place truncate) before
    every FRESH SPAWN, so the launcher's `>>` always lands on a brand-new inode. `Spawn` is called only
    from `InstanceSupervisor.TrySpawn` (manual start / boot respawn-of-dead / crash-restart) — never
    from either adopt path (`AdoptFromHandoff` hot-swap re-attach, `AdoptLiveOrphansAsync` cold-restart
    re-attach), which never call `Spawn` and so never rotate a still-writing live game's log out from
    under it. A fresh inode (rather than an in-place truncate) also lets `EventChannelTail`'s
    inode-keyed rotation detection (`LastReadResetSession`) re-arm cleanly for the readiness and
    player-presence tails alike.
  - Tests: `SpawnEngineTests` (+4, `RotateLogFile` unit coverage), `NativePlayerPresenceIngesterTests`
    (+2, end-to-end: a rotated-away stale ready line and a rotated-away stale join line are never
    resurrected on the next run), `AdoptDoesNotRotateLogTests` (new, +2: hot-swap adopt and cold-restart
    orphan re-adopt leave a live instance's log byte-for-byte unchanged at the same inode). 231/231.

## [1.6.0] - 2026-07-04

### Added
- `instance-ready` event: the "finished booting / joinable" signal the Control Panel uses to flip a
  server from "Starting" to "Running", distinct from `instance-started` (process exec) which fires
  before the game is actually joinable.
  - Owned by `NativePlayerPresenceIngester` (now doubling as the readiness ingester — pure file/cgroup
    reader, additive, no spawn-path change), keyed on the instance's cgroup transitioning
    not-populated → populated (`CgroupManager.IsPopulated`, the same signal `CrashWatcher` polls) —
    universal across every spawn path (manual start, boot autostart, crash-restart, daemon-restart
    re-adopt) without depending on log-rotation semantics. Also re-arms defensively on
    `EventChannelTail.LastReadResetSession` (a genuine log rotation/truncation).
  - Non-empty `Instance.StartupSuccessRegex` (kgsm-lib): compiled once per instance
    (`NativeReadinessMatcher`, mirrors `NativeLogMatcher`'s 100ms ReDoS-guard timeout + honesty rules),
    matched against every new log line; a one-shot whole-file scan on the start edge
    (`MatchesExistingContent`, the .NET analog of the bash reference's `watchers.logs.sh`
    `__logic_test_log_pattern` whole-file `grep -q`) catches a late attach where the ready line was
    already logged before the edge was observed (daemon hot-swap mid-boot, or attaching to an
    already-running instance) — independent of any `EventChannelTail`'s own offset/inode bookkeeping.
  - Empty `StartupSuccessRegex`: honest immediate fallback — `instance-ready` fires as soon as the
    start edge is observed, no fabricated delay. An invalid (non-empty, does not compile) pattern is
    treated as a real blueprint bug — logged once and never silently substituted with the immediate
    rule.
  - Widened the ingester's enable/skip gate: an instance with a readiness pattern but no
    `player_joined_regex`/`player_left_regex` (factorio/minecraft/terraria-shaped) is no longer skipped.
  - `instance-ready` fires exactly once per run (a `ReadyFired` latch cleared only on the next start
    edge).

## [1.5.0] - 2026-07-04

### Added
- Per-instance crash policy, overlaid on the global `BackoffPolicy` at crash-detection time
  (`InstanceSupervisor.ReconcileRunning`), sourced from kgsm instance config
  (`Instance.CrashRestart` / `Instance.CrashMaxRestarts`, kgsm-lib 1.35.0).
  - `crash_restart=false` disables auto-recovery for that instance: an unintentional exit emits
    `instance-crashed` (alert visibility) but is treated as `Stopped` — no retry slot consumed.
    Null → auto-restart on (unchanged default).
  - `crash_max_restarts` overrides the global give-up ceiling (`MaxRetries`) for that instance via a
    new pure `EffectivePolicyFor` helper. Null → keep the global default. Lowering it makes a
    crash-looping instance reach `Failed` (and emit `instance-failed`) sooner than the global would.

### Changed
- Bumped `TheKrystalShip.KGSM.Lib` 1.32.0 → 1.35.0.

## [1.4.0] - 2026-07-03

### Added
- `POST /restart/{name}?origin={origin}` — atomic restart (stop → drain cgroup → start) for the
  intentional-restart path (kgsm-scheduler's scheduled restarts). Routes through `StopAsync` +
  `StartAsync`, so it does NOT count as a crash — `StartAsync` resets the crash-recovery streak.
  Emits `instance-restarted` stamped with the caller's `origin` (optional query, default
  `scheduler`) instead of `system/system`, so the audit attributes the restart to whoever asked.
  200/409 like start/stop (`InstanceSupervisor.RestartAsync`).

## [1.3.0] - 2026-07-03

### Added
- Phase 2 (Resources): per-instance CPU priority + memory cap, sourced from kgsm instance config
  (`Instance.CpuPriority` / `Instance.MemoryCapMb`, kgsm-lib 1.32.0).
  - `CgroupManager.SetCpuWeight` / `SetMemoryMax` write the instance cgroup's `cpu.weight`
    (low=50 / normal=100 / high=400) and `memory.max` (0/null → `max`). Return false when the
    cgroup is absent (instance not running).
  - `SpawnEngine.Spawn` applies both caps right after the cgroup is created, so a memory cap bounds
    the game from its first allocation.
  - `POST /set-cpu-priority/{name}/{priority}` live-applies a cpu.weight change to a running
    instance without a respawn (200 always; `Ok=false` + message when not running). No memory-cap
    live-apply twin — shrinking `memory.max` can't reclaim already-touched pages, so the cap is
    applied only at spawn.

### Changed
- Bumped `TheKrystalShip.KGSM.Lib` 1.29.0 → 1.32.0.

## [1.2.0] - 2026-07-01

### Added
- `GET /players` endpoint: returns all instances' live player sessions as a JSON object keyed by
  instance name. Each session carries `sessionKey`, `id`, `name`, `addr`. Used by kgsm-api on
  startup to reconcile roster status without waiting for events.
- `PlayerSessionStore` — thread-safe DI singleton wrapping per-instance `PlayerSessionMap`s.
  Extracted from `NativePlayerPresenceIngester` so the control surface can read the session map
  while the ingester writes. The ingester now delegates `Join`/`Leave`/`Reset` to the store.

### Changed
- `NativePlayerPresenceIngester` no longer owns per-instance `PlayerSessionMap`s directly — they
  live in the shared `PlayerSessionStore`. The `NativeWatch` record no longer carries a `Sessions`
  field. No behavioral change to event emission.

## [1.0.0] - 2026-06-30

### Added
- Initial versioned release.
