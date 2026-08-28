# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed — a crash is stamped danger (`1.45.0`)

`server.crashed` carries the weight the engine's catalog declares for it, which is now `danger` — the
same as the give-up it may lead to. A server going down unasked is the fleet losing a service, and
this daemon being on its way to bring it back does not make that routine.

### Changed — events speak the one dotted vocabulary (`1.44.0`)

Events are named the way a reader groups them — domain first, past tense, an outcome as its own
segment. One vocabulary, so nothing translates between an engine name and a display one.

The container channel is a different protocol and does not move with it. A line the in-container
shim writes is spelled however that shim spells it, and the event this daemon records from it is a
separate name.

### Changed — an event carries the weight the catalog gives it (`1.43.0`)

An event this daemon records carries how much it matters and how it went, taken from the engine's own
catalog rather than tagged at each call site — so the weight of an event lives in the same place as
its payload type, and this daemon states one it was told rather than one it invented.

Only for a type the catalog recognises. Stamping the defaults onto an event nobody has classified
would assert a weight nothing established, and absent is how unknown is spelled.

### Changed — an event is named by a type (`1.42.0`)

An event's name is a type rather than a string, so a name that is not a name cannot reach the journal.
Where this producer chooses a name at run time, the name is read at that boundary and a value that is
not one is reported and dropped — a line no consumer matches fails silently everywhere downstream,
which is the failure this refuses to write.

### Fixed — readiness belongs to a run, and an uptime is the run's own (`1.41.1`)

`instance-ready` fires once per RUN, on every daemon that supervises it, rather than once per daemon.

The ingester reads a start from its own first sight of a populated cgroup, and for a server that is
already up that sight arrives on the first tick after each daemon start. An adopt does not rotate the
game's log, so the running server's own ready line is still in it for the whole-file late-attach scan
to find. The latch that stops a second announcement now lives in `readiness-state.json`, keyed to the
run itself — the leader's pid and the kernel tick it started on, read via `ProcessStartClock` — so it
outlives the daemon that set it. A run whose leader cannot be read has no key, and is announced rather
than silently swallowed.

An instance's reported start is the run's, measured from the kernel, and no longer the moment this
daemon took charge of it. `SupervisedInstance.SpawnedAt` stays the supervision clock the grace window
and the stability reset are measured from, and it restarts at every adoption because supervision does;
`RunStartedAt` carries the game's own age across a hot-swap handoff, the persisted supervision state,
and `/status` + `/runtimes`. It is null — an honest unknown — when the leader cannot be read, never
the adoption stamped in its place. The run ledger dates a run the same way.

### Added — a park a leaf runs disruptive work behind (`1.41.0`)

`POST /maintenance/begin/{instance}` drains an instance the way a stop does and leaves desired-state
`running`. `POST /maintenance/end/{instance}` respawns it. Both take `?origin=`, and the events they
emit are attributed `system:<origin>` — the leaf that asked, never a person on the local host.

What makes it a park rather than a stop is what it leaves alone. Desired-state stays `running`, so a
leaf that dies holding one leaves an instance something still wants up; the failure streak and the
give-up latch come out of the park as they went in, because the release does not route through the
operator-override path a start takes; and the router lease and host firewall rule stay open, on the
same rule a crash-restart follows.

Crash detection is gated on the phase, not on intent alone. A parked instance is desired-running with
an empty cgroup, which is the exact shape of a crash — `SupervisionPhase.Maintenance` is what tells the
two apart, and crash classification lives in the running phase and nowhere else.

Nothing waits inside the supervisor gate. The park returns as soon as the cgroup is empty and the work
happens outside it; one semaphore held across a 20-minute download would freeze crash-restart for every
instance on the host.

Two deadlines stop a leaf from stranding a server. `Watchdog__MaintenanceMaxMinutes` (default 60)
releases a park nothing else releases, and a park that predates this daemon's own start is released at
boot — the daemon cannot know whether the leaf holding it is still working, and up is the safe answer.

`?origin=` also reaches `POST /start` and `POST /stop`, where it names the caller in this daemon's own
log and attributes nothing: a start is announced by the kgsm command layer with the asker's provenance,
and a second event from here would put one bring-up in the trail twice.

### Added — a signal rung between the console and the kill (`1.40.0`)

Stopping an instance asks its console, then sends SIGTERM, then takes the group down with
`cgroup.kill`. Each rung runs only because the one above it left the cgroup populated.

A game that never reads its console still handles signals, and the Source engine's dedicated server
is the case that shows it: `srcds_run` runs a binary whose stdin nothing reads, so a command written
into the FIFO reaches nobody. With only a console above `cgroup.kill`, such a game is killed outright
along with whatever it had not yet written to disk.

The budget belongs to whichever mechanism is doing the graceful work. An instance that declares no
`stop_command` skips the console rung outright and spends its whole `stop_command_timeout_seconds` on
the signal. One whose console was asked and answered nothing has had that patience already, so the
signal gets a ten-second window before the group is forced.

cgroup v2 carries an atomic whole-subtree kill for SIGKILL alone, so SIGTERM is delivered per-PID over
`cgroup.procs`. The group is frozen around the sweep, and the freeze is waited for because writing
`cgroup.freeze` only requests one: a launcher that supervises its own child — `srcds_run` respawns on
a non-zero exit — can otherwise start a replacement after its child is signalled and before the
launcher itself is, leaving a process the sweep never saw.

Two paths with no console to ask reach the signal rung as well: an adopted instance, re-attached after
a daemon restart with no FIFO recovered, and an untracked cgroup a previous daemon left live.

`CgroupManager.PathFor` rejects any name that is not a single path segment. The daemon lives in the
base cgroup, and the control files written through that path are teardown writes.

### Changed — a packaged install enables and starts the supervisor (`1.39.0`)

`packaging/kgsm-watchdog.install` applies kgsm-base's `50-kgsm.preset` to this project's units in
`post_install`, so a node comes up with them enabled instead of needing a person to enable each one.
The node's post-transaction hook starts what is enabled, stopped and configured. `post_upgrade` does
not preset: an administrator's `disable` survives every later version.

`depends=('kgsm-base')`, which carries the `kgsm` account this unit runs as and the `/var/lib/kgsm`
tree — so this package no longer ships `/usr/lib/sysusers.d/kgsm-watchdog.conf`, and
`deploy/sysusers.d/` is gone. One declaration serves the fleet instead of ten identical copies.

The hook's verb on an upgrade is `restart`. `KillMode=process` means supervised games live through
it and the next daemon re-adopts them; the cold-restart caveat applies, so a surviving game's
`memory.current` re-charges from zero until its own next restart.

## [1.38.1] - 2026-08-23

### Fixed — the env template no longer points the engine at /usr/local

`Watchdog__KgsmPath` is a commented override in `deploy/kgsm-watchdog.env.example`. The unit sets
`/usr/bin/kgsm`, and systemd reads `EnvironmentFile=` after `Environment=`, so a live line in the
template overrides the working default — with `/usr/local/bin/kgsm`, a path no package may create
and which a node installed purely from packages does not have.

## [1.38.0] - 2026-08-22

### Added — an explicit start can carry a person's override

`POST /start/{name}?force=true` starts an instance the capacity check refuses. A query flag rather
than a header or a body field, because the caller that needs it is a curl invocation: the kgsm CLI's
transport builds a URL and reads a status back, and sends no body in either direction. A spelling the
flag does not recognise is a start *without* the override rather than a `400` — the protection is what
a caller gets by not asking, and that has to include asking badly.

**A forced start still takes its reservation.** Force goes past the verdict, not past the ledger:
what this instance is about to claim is what the next one has to be judged against, whichever way its
own verdict went. A forced start that reserved nothing would put back exactly the staleness the
ledger exists to remove, at the moment the node is under the most pressure. The gate answers
`Verdict.Forced` — allowed, and distinct from a start that simply fit — and the daemon logs the
refusal it went past at Warning, with the figures, so a forced start is visible in the journal.

**Only an explicit start forces.** The boot autostart, the crash-restart and `POST /restart/{name}`
take the verdict as final. The judgement that a blueprint's declared figure overstates what a game
really uses is a human's, made at a terminal; nobody is there for the other three, and a crash loop
that forced its way past the floor is the failure the gate exists to prevent.

This is what makes the override real for a native instance. This daemon's check subtracts the
outstanding reservations on top of the same arithmetic the kgsm CLI's gate runs, so it refuses
everything that gate would and more — skipping only the CLI's own check overrode nothing.

## [1.37.0] - 2026-08-22

### Added — a capacity refusal is answered as a refusal, not as a failure

`POST /start/{name}` answers **`507 Insufficient Storage`** when the node has no room for the
instance, and `409` for everything else that went wrong. `POST /restart/{name}` does the same: its
start half runs the same check, and a refusal there leaves the instance **down** with the stop
already done — which a caller reading a generic `409` would retry into the identical answer.

The distinction is on the status line because that is what the caller that most needs it can read:
the kgsm CLI's transport keeps the HTTP code and discards the body, so a discriminator living only
in the JSON would never reach it. kgsm maps the `507` to `EC_INSUFFICIENT_MEMORY` — the same exit
code its own gate returns — so a capacity refusal has one meaning across both halves of the engine.

`ActionResult` also carries the machine-readable `refusal` field (`no_room`, absent when the result
is not a refusal) for callers that read JSON. Neither of those is a sentence: `message` is prose for
a person and stays free to be reworded, and nothing downstream has to match on it.

Nothing is wrong with an instance the gate turns away and nothing was attempted, which is why the
two are worth telling apart at all — a failure invites a retry and reads as a fault in the server.

## [1.36.0] - 2026-08-22

### Added — the capacity check composes over a batch of starts

`MemAvailable` lags a server that has just spawned: a process two seconds old has allocated almost
nothing, and a JVM grows into its heap over minutes. Judged on that reading alone, six 8GB instances
starting together each measure a node that still looks nearly empty, each pass the check honestly, and
collectively commit far past the headroom floor. The gate never fires and the node fills anyway. The
paths where a set of starts arrives at once are exactly the ones only this daemon runs — the boot
autostart brings every enabled instance up together, a crash storm restarts several, and kgsm-api
dispatches a batch command up to four at a time.

So an allowed spawn now takes a **reservation** for the figure it was judged on, and the check
subtracts what is outstanding: the next instance is judged against what the node will have once the
ones already starting have taken what they asked for. A refusal names what is committed and to how
many instances, alongside the three figures it already named.

**Readiness releases it.** `instance-ready` is the point at which the declared memory has materialised,
so from there `MemAvailable` accounts for it and continuing to subtract would double-count. Both
readiness rules discharge a reservation: the `startup_success_regex` match, and — for a blueprint that
declares no such pattern — the immediate "ready == observed started" fallback, which is the only ready
signal such an instance has. Every path on which an instance will never report ready releases it too:
the spawn threw, the process never entered its cgroup, the run ended, the instance was stopped or
deregistered.

A ten-minute backstop stands behind that as a leak guard, not as the mechanism. It exists for the
start that reports no readiness at all — a `startup_success_regex` that does not compile disables
detection rather than being downgraded to the immediate rule, so no ready event is ever coming — where
a reservation left standing would hold memory back for the life of the daemon. It is generous by
design (the slowest honest boots run into minutes) and logs at Warning when it fires, because a
reservation released by timeout means a start that never reported ready.

The ledger changes nothing about what the gate does when it cannot answer: gate off, nothing declared,
`/proc/meminfo` unreadable all still allow, and reserve nothing — there is no measured figure to
reserve and none is invented. It is in-memory and dies with the process, which is correct: a restarted
daemon has no in-flight spawns of its own, and adopted instances are already running, so
`MemAvailable` reflects them.

## [1.35.0] - 2026-08-22

### Added — the daemon refuses to spawn into a node with no room

Every spawn the daemon performs — the dispatched start, the boot autostart, and the crash-restart —
now passes a node-capacity check first. The kgsm CLI gates the starts a person types; the other two
never pass through it, so a gate that lived only there would be missing from exactly the case that
motivates it: an instance restarting into a node that has filled up since it last ran.

The two knobs are read from **kgsm's** config (`enable_memory_gate`, `memory_gate_headroom_mb`)
through `IConfigService`, not from this daemon's own settings, so the host has one answer rather than
two that can disagree. Both are cached for a minute — each read costs a kgsm invocation and a crash
loop can ask repeatedly. Absent keys fall back to the same coded defaults kgsm uses (on, 1024MB), so
a host whose config predates the gate is protected rather than unprotected.

What an instance is expected to need is its own `memory_cap_mb` when set — the ceiling this daemon
itself writes to `memory.max`, so it bounds what the node stands to lose — otherwise the blueprint's
advisory `metadata.min_ram_mb`. With neither declared the check cannot run and the spawn proceeds; no
figure is invented. The reading is `MemAvailable`, never `MemFree`.

**A refusal is not a crash.** The crash-restart path defers instead of spawning, does **not** spend a
retry, and never latches `Failed`: nothing is wrong with the server, the node is simply full, and it
fits again the moment something else stops. The phase stays `restart-pending` and the node is
re-checked every 30s. Spending the give-up budget here would report a capacity problem as a crash
loop and leave the instance down after the memory came back. The boot autostart holds the same way,
which matters most there — every enabled instance comes up at once, so the ones at the back can find
the node briefly full and fit perfectly well a moment later.

There is deliberately **no `--force` equivalent**. The CLI has one because a person at a terminal can
judge that a blueprint's declared figure overstates what a game really uses; the daemon has nobody to
make that judgement, so it never overrules itself.

## [1.34.0] - 2026-08-21

### Added — the reported instance state dates the run

`GET /instances` and `GET /instances/{name}` now carry `spawnedAt` and `lastExitedAt`, so a consumer
can say how long a server has been up, or how long it has been down, without asking a second source.

Both come from state the daemon already keeps. `spawnedAt` is the supervised instance's own spawn
time, which is persisted alongside the phase — so it survives a daemon restart rather than resetting
to the moment the daemon came back, which would report every instance as freshly started after a
redeploy. `lastExitedAt` is the newest row in the run ledger, and is therefore the run's last output
rather than the moment the supervisor noticed the cgroup had emptied.

`spawnedAt` is reported only while the cgroup is populated. A stopped instance keeps its last spawn
time in the table, and reporting it would read as an uptime for a process that is gone. An instance
the daemon adopted rather than spawned reports null: nothing here started it, so there is no spawn
time to state. An instance with no recorded runs reports a null `lastExitedAt` — an honest unknown,
never a fabricated date.

The list endpoint reads the ledger once for the whole fleet rather than once per instance.

## [1.33.0] - 2026-08-19

### Added — a join carries the player's name on a game that prints one only on disconnect

A game's connect and disconnect lines need not carry the same fields, and some name the player on
only one of them. Necesse is the case: `Client "76561198800558749" … is connecting` on the way in,
`Player 76561198800558749 ("gingur") disconnected` on the way out — the account id on both, the
character name on the second alone. The per-field merge already gives the leave event everything the
server said about that session, but a join is emitted the moment the connect line appears, long
before its disconnect line exists. Every arrival therefore announced a bare SteamID64, while the
roster beside it showed the person's name.

`player-names.json` in the state directory now holds, per instance, the display name each account id
was last reported under. A join whose line carries no name is emitted with it. Every surface reading
these events gets the completed identity — the Control Panel's audit log, the roster, Discord, push —
because the fix is where the event is made rather than in any one reader.

It is an association, not a guess, and three things keep it that way: it is keyed on the game's own
account id and never on an address (a port is reassigned per connection and an ip is ISP-mutable, so
that would eventually name a stranger); it fills a field the line left blank and never replaces one it
carried; and it is scoped per instance, claiming only what that server reported. Bounded at 200
accounts per instance, evicting the least recently seen. Losing the file costs a display name on one
join — the next disconnect re-learns it.

Authority: `player-presence-contract.md` §4.

## [1.32.0] - 2026-08-18

### Added — every journal line now carries its own id

Every event this supervisor writes carries an `Id`: a UUIDv7 the shared writer mints per line, inherited by pinning
kgsm-lib 4.41.0. Nothing in this repo changed but the pin.

Why it exists: every durable reference to an event on this host is a byte offset into a named segment,
which holds only while a segment is appended to and deleted whole (conformance §2·l). An id makes a
rewrite **detectable** — a reference carrying both finds the line by position and proves it is the
right one by id, where before a shifted offset resolved to a real, parseable event of the wrong kind
with nothing to notice.

Optional and optional forever: lines written before this are on disk for as long as retention holds
them, and **absent means unknown, never a mismatch**. Authority: `journal-entry-id-plan.md`.

### Fixed — a first setup on a host where nothing is installed yet completes

`deploy/setup.sh` enables its unit at boot and starts it only when something exists at the unit's
`ExecStart`. A host that has never deployed this project has an empty prefix, so the unit is enabled
and left stopped, and the summary names the unit that is enabled but not running and says
`deploy/deploy.sh` is what starts it. The fresh-host path is `setup.sh` → `deploy.sh` with nothing
in between.

The grant verification adapts with it, and still makes two real polkit-gated calls: `daemon-reload`,
plus one `manage-units` call on this project's own service — `start` when the service is running
(systemd queues a no-op job), `try-restart` when it is not (documented to do nothing for a unit that
is not running). Both are dispatched as the same `manage-units` action, so a host without the grant is
refused either way and the probe measures the grant rather than the unit.

Measured in the positive direction only. The deploying user on the development host is in
`wheel`, and two pre-existing polkit rules there grant that group every
`org.freedesktop.systemd1.*` action outright, so no systemctl call by that user can be refused
and the negative path cannot be exercised on it. That `try-restart` consults polkit before it
decides there is nothing to do is systemd's own dispatch order, not something this host can
demonstrate.

### Added — the daemon reports its own state, not just its instances'

`leaf_ready`, `leaf_degraded` and `leaf_stopping` on this daemon's own journal, through
`TheKrystalShip.KGSM.Lifecycle`. Readiness is reported once the control socket is listening and reads
`SupervisorState` — the same answer `/health` serves — so the journal and the probe cannot disagree
about whether this daemon came up.

Three parts of the job can now break without the daemon dying, and each says so instead of only
logging:

- `cgroup-delegation` — no writable delegated subtree, so `/start` refuses. The daemon being unable to
  do its whole job rather than part of it.
- `cgroup-controllers` — controllers could not be enabled on the delegated base. The daemon still
  spawns; what stops is accounting, so a game runs and its memory reads as zero. That looks like a
  monitoring fault from every surface and is a supervision one.
- `cgroup-kill` — checked once at boot against the delegated base rather than discovered on the first
  stop, because the file is per-cgroup but the capability is per-kernel. Without it a stop cannot
  atomically kill the whole process tree, so a game that forked may leave survivors holding its port.

**`StartupMs` is anchored to the top of `Program.cs`, not to the process start.** A hot-swap
`execve`s a new image into the same process id, so the OS goes on reporting the original start — the
first swap of this daemon reported a startup time of four hours. Measured, and still the wrong clock
for an image that replaced itself.

**A hot-swap says goodbye as `reload`, before the `execve` that never returns.** It is the only
chance: the image is replaced in place, so `ApplicationStopping` never runs and the successor cannot
know what happened to its predecessor. A consumer reading `reload` knows the process id is unchanged
and that not one supervised game restarted — indistinguishable from an outage without it, and it would
otherwise page somebody on a successful deploy.

### Fixed — a leave keeps the name the join never saw

`PlayerSessionMap.Leave` merges the identity captured at join with the leave line's own, per field,
join-wins — rather than returning the stored session verbatim. For nearly every game the leave line is
the poorer of the two and the merge changes nothing. Necesse runs the other way: its connect line
carries the SteamID64 and the endpoint, its disconnect line carries the character name, so the name
was measured and then discarded, and the roster showed a bare account id for a player the server had
named. A field is taken from the leave line only where the join captured nothing, so a measured value
is never overwritten by a later one.

Contract authority `player-presence-contract.md` §4 updated to match.


### Added — this producer reports a journal no other account can reach

`TheKrystalShip.KGSM.Journal` 1.5.0 checks at startup whether this producer's state directory grants
its group access, and warns when it does not. A directory cannot be entered without execute on every
directory above it, so a state directory closed to the group hides the journal inside it however
permissive the journal's own mode is.

**That failure is silent.** A reader that cannot traverse in gets `Directory.Exists == false`, not a
permission error — so discovery concludes this producer has recorded nothing, which is exactly what a
genuinely idle leaf looks like. This unit declares `0750` and names the shared `kgsm` group, so the
check stays quiet here; it exists for the leaf that ships `0700` and disappears.


### Added — this producer prunes its own journal

Segments older than **90 days** are removed, matching the engine's own retention window
(`TheKrystalShip.KGSM.Journal` 1.4.0). **Before this, only the engine pruned anything** — its daily
timer covers its own directory alone, and every leaf journal grew without bound.

Pruning runs at startup and again when the segment date rolls over, so a resident daemon prunes daily
and a short-lived one prunes every time it wakes — no timer, and therefore no hosting dependency in
the writer package. Segments are unlinked **whole**, never truncated: a consumer's position is a byte
offset into a named segment, so a rewritten file misplaces every event after the cut, where a removed
one makes the consumer report an honest gap. Age is read from the segment's **name**, not its mtime,
which a restore or a backup tool moves without any event moving.


### Fixed — federation cannot be registered in the wrong order

kgsm-lib 4.30.0 makes `AddKgsmServices` and `AddKgsmJournalFederation` register the same resolution
rule, so either call order yields a federated reader. **The bug it removes had no symptom**: a
consumer that federated too early kept reading the engine's journal *successfully* — healthy journal,
quiet host, nothing to catch — while every other producer's events sat in files it never opened.
`JournalDiscovery` also scans once per process now, instead of once for the history reader and again
for the live tail.

### Changed — journal identity is derived from the producer id

`AddKgsmJournal(WatchdogJournal.ProducerId, …)` replaces the hand-built writer registration and
`WatchdogJournal` derives from `JournalRecorder` (kgsm-lib 4.29.0 / Journal 1.3.0). This daemon keeps
what is its own — the nine event types it authors and the payload each carries — and stops answering
for the parts every producer answers the same way.

- **`ActorWatchdog` is derived from `ProducerId`**, not spelled out. Still `system:watchdog`,
  byte-for-byte, and still exposed: the supervisor hands it to the firewall authority as the
  provenance of a change it asked for.
- **The journal no longer follows `StatePathResolver`.** The other three state files go wherever
  that resolver lands, which can be a home-directory layout — and a journal there is one **no reader
  on this host would ever find**, because discovery scans state directories under `/var/lib`. Under
  systemd both answers are `/var/lib/kgsm-watchdog`, so nothing moves; a daemon run from a home
  layout now records where its events can actually be read. `KGSM_JOURNAL_STATE_ROOT` relocates it
  for a run that must not touch this host's record.
- **`ProducerVersion` is unchanged in shape** — this daemon already stamped its informational
  version, which is what the ecosystem standardised on. It is the other three producers that moved.

The debug line naming a recorded event now reports the normalised type, so it says what the journal
says: these call sites name events the engine's command-line way (`instance-crashed`), and the wire
carries `instance_crashed`.

### Added — a console can be read past its tail, and had whole

`GET /console/{name}` reports the byte range it served in `X-Console-Start` / `X-Console-End`, and
takes `?end=OFFSET` to read the window ending there. A caller pages back by passing the `Start` it was
given, so the windows meet exactly while the game keeps appending — a line count from the end names a
different line on every request, and consecutive pages overlap or skip with nothing saying so. A
`Start` of 0 is the beginning of the run. `?tail=` is still clamped to 5000, but that now bounds ONE
response rather than how far back a caller can read.

The reader behind it walks the file backwards a block at a time instead of streaming it forward into
a ring buffer, so a window costs its own size rather than the size of the log — which is what makes
paging back through a large run affordable at all. Splitting on `'\n'` before decoding is safe
because a newline byte cannot occur inside a multi-byte UTF-8 sequence.

`GET /console/{name}/download` streams the whole of one run's log with a `Content-Length`, copying it
block by block: a multi-gigabyte log costs the daemon a buffer, not its RSS. The length is
snapshotted when the file is opened and exactly that many bytes are sent, because the game is still
appending and a body longer than its own Content-Length is a broken response, not a fresher one.

Both remain one run. The log rotates on every fresh spawn, so a crash and the restart behind it stay
two answers — this surface still refuses to splice them into one stream.

## [1.27.2] - 2026-08-14

### Added — GPL-3.0-or-later

This project now carries a `LICENSE`. Its package declares `GPL-3.0-or-later` and installs the text
to `/usr/share/licenses/`, so a distributed binary travels with the terms it is under.

### Added — an Arch package, built from the tested binaries

`packaging/PKGBUILD` builds this project into a pacman package. It compiles nothing: CI publishes
first and the recipe places that output, so the packaged bytes are the tested bytes. `pkgver()`
reads `deploy/version.sh`, so the package never restates a version.

The install prefix stays `/opt/<project>` — the same path `deploy.sh` uses — which is what lets the
committed systemd unit ship verbatim instead of being rewritten at packaging time.

Config files are listed in `backup=()`, so an upgrade writes `.pacnew` beside a file you edited
rather than over it. The unit, the sysusers fragment and the leaf descriptor are packaged files, so
the descriptor can never lag the binary it describes. Nothing is enabled by a scriptlet: pacman's
own hooks handle the service account, the state directories and the daemon reload, and enabling a
unit is the administrator's decision.

### Added — one machine-readable version, read rather than restated

`deploy/version.sh` prints this project's version from the single file that declares it, and
`--pkgver` prints the form pacman accepts (a `pkgver` may not contain a hyphen; ordering survives it,
since `vercmp` puts `3.16.0rc3` before `3.16.0`). Packaging asks for a version instead of carrying a
copy that can fall behind the binary.

### Added — the deploy contract is files, not install-time script output

`deploy/polkit/48-kgsm-watchdog-deploy.rules.in` carries the headless-deploy grant as reviewable content, and
`setup.sh` renders the deploying user and unit list into it instead of embedding the rule in a
heredoc — what a host is granted can now be read without running anything.

`deploy/sysusers.d/kgsm-watchdog.conf` declares the `kgsm` service account so a packaged install provisions it
declaratively rather than relying on an account that happens to exist.

`deploy/kgsm-watchdog.requires.json` states every host command, peer service and kernel feature this project
needs — each with its Arch package name, a probe that proves it works, and, for anything optional,
what is lost without it.

### Changed — the committed unit names the service account, not a developer

`User=`/`Group=` read `kgsm`, the account `sysusers.d` declares. `render_unit()` still substitutes
the deploying user at install time, so a dev-host deploy is unchanged.

### Changed — kgsm-lib 4.25.0

Picks up the events an audit of the engine's whole vocabulary added: the update's failure fact and the
brackets around the two backup verbs. Nothing here emits them; the daemon tracks the library its
consumers share.

### Added — a scheduled restart reports its middle

`RestartAsync` stops the instance, waits for its cgroup to drain, then starts it — and said nothing
between the two, so for the whole shutdown a consumer had only `instance-restarted` at the very end
and read the instance as running while its process did not exist. It now emits
`instance-restart-stopped` once the drain completes, attributed to the requester like the restart
itself. A step inside one operation, never `instance-stopped`: that one is the fact that somebody
stopped a server (kgsm-lib 4.24.0 classifies the new event `Phase`).

### Changed — this daemon no longer records firewall edges

`OpenFirewallPorts`/`CloseFirewallPorts` ask kgsm-firewall and record nothing. The authority performs the
change and writes `instance_ports_opened`/`instance_ports_closed` to its own journal — this daemon asked,
which is not the same as having done it, and journaling here as well put one firewall edge in the trail
twice under two different authors.

The new `AskAndForget` is the counterpart to `FireAndForget`, and the distinction is the whole of
"emitter = author": one is for an edge this daemon both performs and records (UPnP, which it genuinely
drives), the other for an edge it merely asks another authority to perform. The firewall call's outcome is
deliberately unused now — whether the firewall changed is the firewall's to report.

`FirewallPortsService` forwards `actor`/`origin` (kgsm-lib 4.23.0) so the authority's record can say who
asked; this daemon's own supervisory bring-up passes `system:watchdog`.

### Changed — this daemon records what this daemon did

The watchdog writes its own event journal instead of spawning `kgsm.sh` to write each event down.
Everything it emits is something it established itself — it spawned the process, it opened the port, it
saw the readiness line — so the line naming kgsm as the author named the wrong one, and the write cost a
bash bootstrap, a sourced library and a `jq` call **per event**, three times over on a single server
start. Authority: `../event-journal-federation-plan.md` (Phase 4).

- **`WatchdogJournal`** holds every payload shape in one place, so the four emitting components cannot
  drift into describing the same event two ways. Its journal is `<state directory>/events`, beside the
  other state files, created by the writer inside a directory systemd's `StateDirectory=` already gives
  this daemon — no provisioning and no privilege. The producer id is that directory's own name, which is
  what a reader scans for, so the writer and every reader agree on the location without either being
  told.
- **Writes are inline, not off-thread.** The old path hopped to the thread pool because an engine spawn
  is slow enough to stall the reconcile tick — and that hop meant two events emitted back to back could
  land out of order. An append is one write to a file this daemon owns, so doing it in place is both
  fast enough and strictly better: the journal's order becomes the order things happened.
- **The ports events stop round-tripping through a UFW string.** They carried
  `IReadOnlyList<PortMapping>` rendered down to a UFW spec so kgsm could parse it back into structured
  JSON; they are now written structured from the mappings the daemon already holds, removing the only
  place the two spellings could disagree.
- **Absent player fields are real JSON nulls.** A positional string emit cannot carry a null
  mid-arguments, so an unknown id or address travelled as an empty string and kgsm mapped empty back to
  null at the far end. The honest-null rule no longer depends on a conversion happening two components
  away. The container path still records no address and no session key — passed through as they were,
  never invented.
- `IEventManagementService` is gone from this daemon. The tests record through the **real**
  `WatchdogJournal` over an in-memory writer, so they assert the JSON a consumer will actually
  deserialize rather than a list of call arguments.

kgsm's own `instance-ready` watchers (`watcher.logs.sh`, `watcher.ports.sh`) emit the same event from
the engine's journal when `enable_watcher=true`. It defaults to **false**, so the watchdog's readiness
matcher is normally the only emitter — but turning it on now yields two rows for one fact, in two
journals.

### Added — the run ledger: how each run ended

A console's runs could be listed and read one at a time, but nothing said which of them held a crash.
A consumer could only compare a run's end time against a crash event and hope the nearest one was it
— a guess that cannot tell a crash from a deliberate stop, and that picks the wrong file whenever a
server was restarted near the moment another one died.

The supervisor is the only thing on the host that watches a process exit and classifies it against
operator intent, so it now records that verdict. `run-history.json` holds a bounded ring of runs per
instance: how each ended (`crashed` / `gave-up` / `exited` / `stopped`), the exit code where one could
be read, when the run started, and the supervisor's own reason line. `GET /console/{name}/runs` gains
`outcome` and `exitCode` on every run.

A row joins to its console file on **the file's own mtime**, read at the moment the run is concluded.
That is the last line the process printed, not the moment the supervisor noticed an empty cgroup, and
`rename(2)` leaves it untouched — so the row still identifies its file after rotation has moved it
into the instance's logs directory. Nothing is written into the game's own directory: a log stays a
byte-faithful capture of stdout, and anything scanning it (readiness, presence, a blueprint's own
regexes) still sees only what the game wrote.

A run with no row reports `unknown`, which is an absence of knowledge and never a clean ending — what
every run rotated before the ledger existed reports.

### Changed — state lives in `/var/lib/kgsm-watchdog`

The unit declares `StateDirectory=kgsm-watchdog` + `StateDirectoryMode=0750`, and the three state
files resolve from `$STATE_DIRECTORY`. systemd creates the directory before `ExecStart` and chowns it
to `User=`, so it costs no privilege — the same declaration `kgsm-api` and `kgsm-monitor` use. It is
also independent of `User=`, which `setup.sh` templates per host, and of whether that account has a
home directory at all. `Watchdog__StateFile` still outranks it; a daemon run outside systemd still
falls back to the XDG data home.

**This needs a `--cold` deploy** — `StateDirectory=` is a unit-level directive and a hot-swap
cannot apply it. State written under the home directory is carried over on first use of the systemd
directory (copied, then removed), because `desired-state.json` is the only record of which instances
return after a reboot and leaving it behind would start nothing at the next boot without erroring.

### Added — a console has runs, and they are addressable

The log rotates on every fresh spawn, so a crash and the restart that followed it leave the cause in
one file and a clean boot in another. `GET /console/{name}?tail=N` could only ever read the live one,
which made it blind to a crash precisely when the daemon had healed it — the case where nothing else
is left to look at.

`GET /console/{name}/runs` lists which stretches of console exist, newest first, and when each ended.
`?run=I` reads one of them; it defaults to 0, so the existing call is unchanged. A caller correlating
an event against output asks which run ended when and reads that one, never having to know rotation
exists or guess a path — the file stays inside the daemon and the index is the only handle.

Each response is **one run**. Splicing two into a single body would read as continuous text and invite
a caller to narrate a stack trace and the boot after it as one timeline, from two different processes.

Ordering and `endedAt` come from each file's last-write time, never its name. The last write is the
last line a run printed, which makes it the measured end; a name is only a label, and a log rotated by
an older build is labelled with the rotation moment — days off for an instance that sat stopped.

`current` means a process is alive in the instance's cgroup writing that file right now, not merely
that the file is the live path. A stopped instance — or one that crashed with nothing restarting it —
keeps its last log there until the next spawn rotates it, and that run is over: it reports
`current: false` with a real `endedAt`, which is what lets the crashed run be found by its end time
even when no restart followed.

### Fixed — a rotated log is named for when the run ended

`SpawnEngine.RotateLogFile` stamps the rotated filename from the log's last write — the last line the
server printed — instead of the clock at the moment of rotation. The two are the same quantity only
when a restart is immediate; every stopped interval separates them. An instance stopped on the 1st
and started on the 5th produced `romestead.2026-07-05T11:47:49.log` for a run whose final line was
written on the 1st at 21:26:10, misplacing that run by three and a half days for anything correlating
a run against a timestamp.

Rotation still happens at spawn, which is what guarantees `NativeReadinessMatcher`'s whole-file
late-attach scan can only ever see the current run's content — an exit has no hook on a crash, a
SIGKILL or a host power loss, so the guarantee is asserted where it has to hold.

### Fixed — a port two instances share belongs to both of them

A router forward is one row per (port, protocol) however many instances declare it, and `upnpc`
addresses it by port with no owner attached. Two servers sharing an external port — Steam's
`27015/udp` across a Palworld and a Stationeers instance, say — therefore share one row, and both
halves of the UPnP lifetime were reading that row as belonging to exactly one of them.

**A stop released ports a sibling was still running on.** `upnpc -f <port> <proto>` deletes by port,
so the first instance to stop deleted the shared row and took the other off the air from outside,
until the sweep noticed and put it back. Retention now decides what a close may delete: the ports
another desired-running instance still wants stay mapped, and the last instance standing is the one
that releases them. The predicate is desired-running rather than "cgroup is populated", so a sibling
mid-crash-restart keeps its forward — a dead process does not drop a router lease and the respawn
still needs it. Every close site asks (`InstanceSupervisor`'s stop, the sweep's mid-re-assert undo,
and the container lifecycle ingester), and `CloseAsync` has no overload that omits the question, so a
new one cannot inherit the bug by forgetting to ask. The `instance-upnp-closed` event reports what was
actually released rather than the instance's declared set, and a close with nothing left to release is
`Skipped`, not a removal that never happened.

**The sweep read a sibling's tag as its own forward being dropped.** `upnpc -r` on a port the IGD
already holds overwrites the description, so whichever instance started last owned the tag — and
matching on the tag alone made the sweep report a forward that was present and correct as *"the router
dropped 1 forward(s) … while it was running"*, re-open it, hand the tag back, and warn again on the
next sweep for as long as both instances ran. Each pass emitted a fabricated `instance-upnp-reasserted`
event. A row now satisfies a configured port when it *is* the mapping the daemon would open — same
port and protocol, pointing at this host on the port it forwards — with this host's LAN address read
off the same `upnpc -l` listing the sweep already fetches. The tag still counts on its own, a row
pointing at another host or translating to a different internal port still reads as missing, and a
listing that reported no local address falls back to matching on the tag.

### Changed — `GET /players` answers whether a roster is knowable, not just who is on it

The endpoint listed only instances with tracked sessions, so an absent instance was ambiguous between
"nobody is online" and "this game cannot report players" — and every consumer that read the first
meaning of the second stated something this host does not know.

Now **every** instance appears, each carrying its `detection` beside its `players`:

```json
{"minecraft": {"detection":"log","players":[…]}, "starbound": {"detection":"none","players":[]}}
```

`log` is matched from the game's output (real transitions), `rcon` is polled and diffed (cannot see
churn between polls), `none` is not observable, and `unknown` means the instance inventory could not
be read so the capability could not be established either way. The two travel in one object because a
consumer must not be able to take the roster without the qualifier.

**`PlayerDetection` is now the single predicate**, and the RCON poller gates on it rather than
carrying its own copy — an instance polled here while the endpoint called it undetectable would tell a
consumer "nobody can tell" about a roster being actively read. It is not a rule a consumer could
re-derive in any case: it accounts for log matching, RCON (port + password + command), and whether
each pattern actually *compiles*.

Consumers move with kgsm-lib 4.6.0 (`IWatchdogClient.GetAllPlayersAsync` → `GetPlayerPresenceAsync`).

### Fixed — RCON player polling reaches the game

The poller has been registered and ticking all along, and never once produced an event. `RconClient`
sent its authentication as packet type 0; Source RCON's `SERVERDATA_AUTH` is type 3. Servers answer
with an id of -1, which the client reports as a rejected password — so the one instance on the host
with RCON configured logged 28,796 authentication failures against a password that was correct on both
sides. The fix and its siblings (response termination, read deadlines, split-response reassembly) are
in kgsm-lib 4.5.1, now covered by tests that drive a real socket, because a protocol defect is only
observable on the wire.

A failing poll now stamps its own timestamp. Left unstamped, it never satisfied the interval check and
was retried on every 1 Hz tick instead of the configured cadence, so the instance least likely to
answer was polled the hardest — the source of those 28,796 lines, each with a stack trace. A repeated
identical failure is logged once at warning and at debug thereafter, and the recovery is logged too.

It does not report players for a stopped server and never did: the poller skips any instance the
supervisor does not hold as running, so draining a roster at shutdown remains the supervisor's job.

### Changed — the roster's shape is blueprint data, not daemon knowledge

`RconPlayerResponseParser` applies the pattern an instance's blueprint supplies
(`rcon_players_regex`, kgsm-lib 4.5.2) and holds no format of its own. Rosters are worded per game —
Project Zomboid prints a header and one `-Name` line per player and states no id anywhere, while a
columnar roster gives an id and a name — and both are the same operation against a different pattern.
Adding an RCON game is a blueprint edit; it needs no build and no deploy of this daemon.

An entry carries only what the server stated: `PlayerEntry.Id` is absent for a game whose roster is
names-only, rather than filled in from the name so every entry has the same shape. An instance whose
blueprint supplies no usable pattern is skipped with one warning naming it, because polling it could
only yield a roster that is empty for want of parsing — indistinguishable from nobody being connected.

### Added — the player session map survives a hot-swap

`HotSwapHandoff` carries every instance's player sessions across the `execv`, alongside the FIFO fds,
and for the same reason: the map cannot be re-derived. The successor's log tail primes at EOF, so the
join lines that established those sessions are behind it permanently.

That matters because a join line carries the player's name and a leave line usually does not — the map
is what pairs them. Of the games with presence configured, romestead's leave carries only the peer
address, valheim's and Core Keeper's only an opaque key. A leave landing on an empty map has nothing to
resolve and, per the presence contract, is skipped rather than guessed, so the player stayed reported as
connected until the instance next stopped. Games whose leave line carries a name or id (minecraft,
palworld) were unaffected.

The sessions are a top-level map on the handoff, not a field on `HotSwapEntry`: entries exist only for
instances carrying a live FIFO fd, while sessions are tracked for every instance the ingester discovers.
Hanging them off the entries would have dropped the map for an adopted, cgroup-only instance — the one
that has already lost its console. They are restored before any instance adoption and independently of
it, so a successor that comes up not-ready still keeps presence correlation. A session carrying no key is
dropped rather than restored: the key is what a leave resolves against, so an entry without one could
only ever inflate what `GET /players` reports.

The RCON poller seeds its first poll from the session map instead of starting empty. That dictionary is
not carried across the swap, and an empty one makes the first poll blind to leaves — a player who
disconnects before it runs is absent from both sides of the diff, so nothing retires the session the map
still holds.

**Not persisted to disk.** `desired-state.json` and `supervision-state.json` hold intent and counters,
which stay true while the daemon is dead. A session map is an observation, and its truth expires the
moment nobody is watching; a file would assert after a reboot that players were connected to a server
that has not run in a week. A hot-swap is the one restart that preserves the map's truth across the gap,
because the games never stopped and the gap is microseconds.

### Fixed — a player session map does not outlive the process it describes

`GET /players` answered with players connected to instances that were not running, for as long as they
stayed down: the per-instance session map was only ever cleared when the log rolled to a fresh inode,
which is the *next* start. The consequence was not confined to this daemon — kgsm-api reconciles its
permanent roster from that snapshot on every startup, so an API restart during a stopped instance's
down-window copied the phantom sessions into a durable record and reported them, in the Control Panel,
as players connected to a stopped server.

The map is now dropped at the edge where every session in it genuinely ends — the instance's process
ending — in `InstanceSupervisor`, the one place every teardown passes through: the `stop` verb (in a
`finally`, so a stop that throws mid-drain still clears, and after the drain, so a disconnect the game
logs on its way down still resolves against the session it names), the reconcile pass that concludes a
process exited (ahead of the crash / gave-up / stay-down branches, so none of them can leave a map
over a dead process), and the completion of a stop whose caller abandoned it. The log-roll reset stays
as the second trigger it always was.

Clearing emits nothing. A stop is one fact, already carried by the lifecycle event every consumer
resets a roster on; an `instance-player-left` per tracked session would be a disconnect record no game
ever reported.

The RCON poller drops its own last-poll player set for an instance that is not running, too — carried
into the next run it turned the first poll after a restart into a burst of leaves for players who
disconnected with the previous session.

### Added — the router drops forwards, so the daemon puts them back

A UPnP mapping is not durable the way a host firewall rule is. A router can accept one, report its
lease as infinite, and discard it anyway — on a WAN reconnect, a reboot, table pressure, or nothing
visible at all. When that happens a game server keeps running and quietly stops being reachable from
outside, and nothing on this host is told.

`UpnpReconciler` sweeps for exactly that: every `Watchdog__UpnpReconcileSeconds` (default 300, `0`
disables) it compares what the IGD actually holds against what the running forwarding-enabled
instances need, and re-opens what is missing. It reconciles rather than renews because there is no
expiry to schedule against — the advertised lease is the number that turns out not to bind the
router, and `upnpc -r` takes no duration to negotiate one. Measuring instead covers every cause
uniformly, including a router reboot and a sibling instance whose stop deleted a shared external port
(`upnpc -f` deletes by port, with no owner check).

It is cheap and timid by construction. No forwarding instance running means the router is never
contacted at all; one `upnpc -l` answers for every instance in the sweep however many there are; and
a router it cannot reach leaves it doing nothing, because an unreadable table is not evidence of an
empty one — treating it as such would turn a brief outage into a storm of redundant re-opens. A
mapping owned by someone else on the same external port is not counted as ours, so a re-assert never
silently steals a port from its owner. If the instance stops while `upnpc` is mid-call, the sweep
releases what it just restored and reports nothing.

- **`instance-upnp-reasserted`** (kgsm 3.11.0-rc1) is emitted only on a confirmed re-open, carrying
  the subset that was missing rather than the instance's whole set. Its own event rather than a second
  `instance-upnp-opened` because the two are different facts: an open accompanies a bring-up, this one
  says the mapping went missing with nothing on this host asking for it. It is the only evidence an
  operator gets that their router discards mappings it accepted, and how often.
- `UpnpService` gains `ListAllAsync` (the whole redirection table, owner-tagged) and a subset-scoped
  `CloseAsync`; `InstanceSupervisor` exposes `ForwardingCandidates`, `IsRunning`, and
  `NoteUpnpReasserted`, all lock-free over the instance table the way `/status` and `/list` already are.

### Removed

- **`POST /upnp/{name}/open` and `/close`**, and the supervisor methods behind them. Opening and closing
  are pure lifecycle side effects — the daemon opens an instance's router forwards as it spawns and
  releases them on a deliberate stop, so a forward lasts exactly as long as the run and there is nothing
  for a caller to drive by hand. `GET /upnp/{name}` stays: what the router actually holds is a question a
  diagnosing operator has.


### Fixed — actor and origin are two axes, and a caller-driven event stamps each on its own

Three emitters passed one caller-supplied string as **both** the actor and the origin, which cannot be
right for either: the actor became a bare name — read downstream as a person on the local host, so the
audit log grew people called `scheduler`, `assistant`, `harness` and `verify-test` — while the origin
became a word outside the five surfaces the audit vocabulary knows (`ui|assistant|discord|system|api`)
and was normalized away to nothing.

- **The atomic restart** (`/restart/{name}`) is attributed to the leaf that asked, as
  `system:<requester>` — the form a consumer reads as an autonomous leaf — with origin `system`, since
  no human surface drove it. A scheduled restart now reads in the audit trail exactly like the
  scheduler's backups do. The daemon's own crash-recovery respawn is unchanged (`system:watchdog`).
- **On-demand UPnP open/close** keeps the caller's origin, which there genuinely IS the surface the
  request came through (`assistant`, `ui`, or the endpoint's `control` default), and stamps the actor
  as `system:watchdog` — the daemon performs the mapping, and the identity of whoever drove the surface
  is not forwarded to it, so claiming one would be an invention.

`ProvenanceActor` is the one place a requester becomes an actor: a requester that already names its
identity source passes through, a bare leaf name gains the `system:` provider, and a blank one is the
daemon itself. The `/restart` query key stays `origin` — that is what kgsm-lib's `IWatchdogClient`
sends — and names the requesting leaf.

### Added — an instance's ports are open while it runs

The supervisor now asks the kgsm-firewall authority to open an instance's ports as it spawns it, and
to release them on a deliberate stop — the host-side peer of the UPnP mapping it already opens and
closes on those same edges. UPnP opens the router; this opens the host.

The trigger belongs here because the supervisor is the only thing that sees every bring-up. A boot
auto-start and a crash-respawn never pass through the kgsm CLI, so an instance that only ever comes
up that way could run behind a host ruleset that was reset or rebuilt underneath it, with nothing to
say why it was unreachable. It fires on exactly the two edges UPnP does — a fresh start and a boot
respawn of a dead instance, never a crash-restart — because a dead process does not drop a host
rule and the restart still needs it.

It owns the trigger, not the firewall: every write goes through kgsm-lib's `IFirewallService` to the
authority, which stays the single owner of host firewall state. `Watchdog__FirewallSocketPath`
points at its control socket.

Best-effort, and deliberately louder than UPnP. A failure never fails a start — an authority that is
down must not keep a game server off the air — but a failed *open* leaves the server unreachable to
everyone rather than only to off-LAN players, so it is logged at warning, and the absent
`instance-ports-opened` event is what says the ports were never confirmed open. Only a confirmed rule
change emits: a rule staged against an inactive backend, a no-op, and an unsupported backend all
report honestly as nothing having happened.

### Changed — one place owns the bring-up side-effect mechanics

`FireAndForget` carries the rules every side effect on these edges has to get right — run it off the
supervisor thread so a slow router or an unreachable authority never delays a start or holds the
gate, emit the audit event only on a confirmed change, and let nothing escape to fault supervision.
UPnP and the firewall are two call sites over it rather than two restatements of it.

### Changed — kgsm-lib 3.1.0

Up from 2.0.0. The engine event journal is now queried directly through the library
(`IEventJournalHistory`), which retires kgsm-monitor's event index — nothing here read that index, so
this repo only follows the pin.

Two breaking changes in the library reach this code. `IEventService.RegisterRawHandler` and
`IEventSource.EventReceived` carry an `EventPosition` alongside the envelope, because an event's
journal position is now its identity: it is unique by construction, so two identical events emitted
within one second are no longer collapsed the way a content hash collapsed them.
`IInstanceService` gained the player-moderation verbs (`Kick`/`Ban`/`Unban`) back in 2.1.0, which
this repo skipped over. Every `IInstanceService` test fake implements them now.

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
