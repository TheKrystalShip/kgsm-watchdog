# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
