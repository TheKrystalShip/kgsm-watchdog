# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
