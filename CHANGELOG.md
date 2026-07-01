# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Player-presence contract §4: `NativeLogMatcher` now recognizes `addr`/`key`/`reason` named groups
  (alongside the existing `id`/`name`), with the join identity guard relaxed to
  `id || name || addr` (an opaque `key` alone is not a roster-worthy identity). New per-instance
  `PlayerSessionMap` correlates and dedups native join/leave lines (`sessionKey = key ?? addr ?? id ??
  name`; insert-if-absent on join, resolve-and-evict on leave, with an honest fallback/skip when the
  map misses) — validated against real log lines from all four survey games (stationeers, romestead,
  Valheim, Core Keeper). `EventChannelTail` gained `LastReadResetSession`, so `NativePlayerPresenceIngester`
  clears an instance's session map on a genuine log rotation (not on its first-ever attach).
  `instance-player-joined`/`instance-player-left` now emit `playerAddr` + `sessionKey` (and `reason` on
  `left`) alongside the existing `playerId`/`playerName`.

## [1.0.0] - 2026-06-30

### Added
- Initial versioned release.
