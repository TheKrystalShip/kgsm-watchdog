#!/usr/bin/env bash
#
# Build + deploy the kgsm-watchdog supervisor in one go.
#
#   ./deploy/deploy.sh            # HOT-swap if the service is already running (default), else COLD
#   ./deploy/deploy.sh --cold     # force the legacy stop → install → start bounce
#
# Publishes the Native-AOT binary as YOU (the invoking user) and uses sudo ONLY for the steps that
# touch systemd / root-owned paths. The artifact is a single self-contained native binary — NO .NET
# runtime needed.
#
# ── Two install modes (Increment 7 Phase 5 — Option 3 hot-swap) ────────────────────────────────────
#  HOT  (default when `systemctl is-active kgsm-watchdog` is true and --cold is NOT given):
#       a zero-downtime in-place binary update. The new binary is validated with `--selfcheck`
#       BEFORE it is installed, atomically renamed onto the live path (rename(2) → the running
#       process keeps its old now-unlinked inode), then `systemctl reload` sends SIGHUP and the
#       daemon re-execs the new image IN PLACE — same PID, games keep their fds/cgroups/PIDs, no
#       supervised game restarts. Verified by /version (new build) + an UNCHANGED MainPID (proves an
#       in-place swap, not a systemd restart) + a healthy /health.
#  COLD (first install, or --cold): the legacy stop → install → start bounce (supervised games are
#       re-adopted by the fresh daemon, but an EOF-sensitive game may lose its console — see Inc 6).
#
# DAEMON-SIDE DEPENDENCY (built in parallel, Inc 7 Phases 0/3/4): the HOT path requires a binary that
# supports `--version` (prints the informational version, exit 0), `--selfcheck` (no-side-effect
# runnability probe, exit 0 ok / non-zero fail), `GET /version` over the control socket returning
# JSON {"version":...,"commit":...}, and a SIGHUP handler that re-execs in place. Against an OLDER
# binary lacking these, prefer `--cold`. The HOT path here fails CLOSED: a non-zero --selfcheck on
# the NEW binary aborts the deploy before anything is installed (the running daemon is untouched).
#
# Installs the SYSTEMD-DELEGATION unit (the one canonical unit; PLAN Inc 8): the daemon runs as the
# KGSM user from the start (no root, no privilege drop). systemd places it in kgsm.slice with
# Delegate=yes and hands it the kgsm.slice/kgsm-watchdog.service subtree; the daemon discovers that base
# at runtime and creates per-instance cgroups under it. User=/Group= are rewritten to the invoking user,
# so a fresh host needs no dedicated 'kgsm' user (override with KGSM_WATCHDOG_USER/GROUP).
#
# NOTE: the FIRST migration from the old root-boot unit to this one must be a COLD swap (--cold): the
# running daemon is root in the old kgsm.slice/<inst> layout; a hot-swap would re-exec it in place
# without re-placing it under delegation. After that, hot-swaps are fine (same delegated layout).
#
#   * binary → /opt/kgsm-watchdog/kgsm-watchdog,
#   * unit   → kgsm-watchdog.service with User=/Group= rewritten to your user,
#   * verified by an actual "ok"/200 from GET /health over the control unix socket.
#
# Non-interactive: SUDO='sudo -A' SUDO_ASKPASS=/path/to/askpass ./deploy/deploy.sh
#
set -euo pipefail

# ── Arg parsing ─────────────────────────────────────────────────────────────────
COLD=0
for arg in "$@"; do
    case "$arg" in
        --cold) COLD=1 ;;
        -h|--help)
            sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^#\{0,1\} \{0,1\}//'
            exit 0
            ;;
        *) printf '!! unknown argument: %s (try --cold or --help)\n' "$arg" >&2; exit 2 ;;
    esac
done

# ── Paths / config ────────────────────────────────────────────────────────────
PREFIX="/opt/kgsm-watchdog"
BIN="$PREFIX/kgsm-watchdog"
UNIT_DST="/etc/systemd/system/kgsm-watchdog.service"
ENV_DIR="/etc/kgsm-watchdog"
ENV_FILE="$ENV_DIR/kgsm-watchdog.env"
SERVICE="kgsm-watchdog"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_DIR/src/Watchdog/Watchdog.csproj"
UNIT_SRC="$REPO_DIR/deploy/kgsm-watchdog.service"
ENV_EXAMPLE="$REPO_DIR/deploy/kgsm-watchdog.env.example"
PUBLISH_DIR="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"
# The user the daemon runs as (systemd delegates the cgroup subtree + owns the socket dir to it).
# No privilege drop any more — systemd starts the daemon as this user directly (PLAN Inc 8).
WD_USER="${KGSM_WATCHDOG_USER:-$(id -un)}"
WD_GROUP="${KGSM_WATCHDOG_GROUP:-$(id -gn)}"
SUDO="${SUDO:-sudo}"

# Health is HTTP-over-unix-socket; path matches the unit's RuntimeDirectory.
WD_SOCK="${WD_SOCK:-/run/kgsm-watchdog/control.sock}"
HEALTH_TRIES="${HEALTH_TRIES:-30}"

# ── Helpers ─────────────────────────────────────────────────────────────────
log() { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
err() { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap — attempting to bring it back up..."
        $SUDO systemctl start "$SERVICE" \
            && err "restarted ${SERVICE} (running the PREVIOUS build)." \
            || err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

wait_health() {
    local i
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        curl -fsS -o /dev/null --max-time 2 --unix-socket "$WD_SOCK" http://localhost/health 2>/dev/null && return 0
        sleep 1
    done
    return 1
}

# The path to kgsm.sh the UNIT runs with — needed so the new binary's --selfcheck (a no-side-effect
# WatchdogOptions parse + load probe) sees the SAME KGSM_WATCHDOG_KGSM_PATH the live daemon resolves.
# Source order mirrors the unit: an EnvironmentFile assignment overrides the unit's inline default,
# so the env file (if present) wins; else fall back to the unit's documented default. This file is
# the systemd EnvironmentFile format (KEY=VALUE / # comments), so a plain grep is the safe reader
# (do NOT `source` it — it can legitimately contain values systemd parses but bash would mangle).
resolve_kgsm_path() {
    local p=""
    if [[ -r "$ENV_FILE" ]]; then
        # last uncommented assignment wins (systemd applies them top-to-bottom)
        p="$(grep -E '^[[:space:]]*KGSM_WATCHDOG_KGSM_PATH=' "$ENV_FILE" 2>/dev/null \
                | tail -n1 | cut -d= -f2- | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    fi
    # Default = the unit's inline Environment= value (kept in lockstep with kgsm-watchdog.service).
    [[ -n "$p" ]] || p="/usr/local/bin/kgsm"
    printf '%s' "$p"
}

# Read the daemon's reported version over the control socket. Echoes the raw JSON body of GET
# /version on success (caller greps it); empty + non-zero if the endpoint is unreachable/absent.
http_version() {
    curl -fsS --max-time 2 --unix-socket "$WD_SOCK" http://localhost/version 2>/dev/null
}

# COLD swap (full restart): reload unit → stop → install → start. Used for a first install or with
# --cold. daemon-reload happens BEFORE the stop so the stop honors the NEW unit's KillMode=process —
# otherwise systemd would kill the games (which live under the service cgroup) with the daemon. The
# fresh start is also what applies unit-level directives a hot-swap can't (NoNewPrivileges,
# CapabilityBoundingSet, User=, KillMode) — see hot_swap's note.
cold_swap() {
    if [[ "$UNIT_CHANGED" -eq 1 ]]; then
        log "reloading systemd (unit changed) before stop — so KillMode=process + hardening apply this cycle"
        $SUDO systemctl daemon-reload
    fi

    log "stopping ${SERVICE} (KillMode=process → games keep running, re-adopted on start)"
    $SUDO systemctl stop "$SERVICE" 2>/dev/null || true
    STOPPED=1

    log "installing binary → ${BIN}"
    $SUDO install -m 0755 "$PUBLISH_DIR/kgsm-watchdog" "$BIN"

    log "enabling + starting ${SERVICE}"
    $SUDO systemctl enable --now "$SERVICE" >/dev/null 2>&1 || $SUDO systemctl start "$SERVICE"
    STOPPED=0
}

# HOT swap (default when the service is already active): validate → atomic-rename install → reload
# (SIGHUP → in-place execve) → verify the new build is live on the SAME PID. The daemon is NEVER
# stopped; the failure path here installs nothing it hasn't already vetted.
hot_swap() {
    local new_bin="$PUBLISH_DIR/kgsm-watchdog"
    local kgsm_path expected_version old_pid

    kgsm_path="$(resolve_kgsm_path)"

    # (a) SAFETY GATE — validate the NEW binary BEFORE it touches the live path. --selfcheck is a
    #     no-side-effect runnability probe (no socket bind, no cgroup writes). Run it with the unit's
    #     KGSM_WATCHDOG_KGSM_PATH so the option-parse sees what the daemon will. Non-zero → ABORT the
    #     deploy: we refuse to install a binary that can't even self-validate. The running daemon is
    #     untouched (nothing has been installed yet), so this is a clean no-op abort.
    log "validating new binary: ${new_bin} --selfcheck (KGSM_WATCHDOG_KGSM_PATH=${kgsm_path})"
    if ! KGSM_WATCHDOG_KGSM_PATH="$kgsm_path" "$new_bin" --selfcheck; then
        err "the NEW binary FAILED --selfcheck — ABORTING the deploy."
        err "nothing was installed; the running ${SERVICE} is untouched (still the previous build)."
        err "fix the build, or if this is an OLDER binary without --selfcheck, re-run with --cold."
        exit 1
    fi

    # The version we expect to see after the swap (the new binary reports its own).
    expected_version="$("$new_bin" --version 2>/dev/null || true)"
    [[ -n "$expected_version" ]] || err "warning: new binary did not print a --version; PID-unchanged check will be the swap proof."

    old_pid="$($SUDO systemctl show -p MainPID --value "$SERVICE" 2>/dev/null || true)"
    log "current ${SERVICE} MainPID=${old_pid:-unknown}; new build version='${expected_version:-?}'"

    # (b) ATOMIC INSTALL — install to a sibling temp in the SAME dir, then rename(2) onto the live
    #     path. rename within a dir is atomic: the running process keeps its old (now-unlinked) inode
    #     while the path flips to the new inode. NEVER install/cp directly over the running file —
    #     that truncates+writes the live inode (ETXTBSY at best, a corrupted mmap'd image at worst).
    log "atomic install → ${BIN} (via ${PREFIX}/.kgsm-watchdog.new + rename)"
    $SUDO install -m 0755 "$new_bin" "$PREFIX/.kgsm-watchdog.new"
    $SUDO mv -f "$PREFIX/.kgsm-watchdog.new" "$BIN"

    if [[ "$UNIT_CHANGED" -eq 1 ]]; then
        # A reload won't pick up unit-file changes; daemon-reload makes systemd re-read the unit.
        # The ExecReload itself still just SIGHUPs the (same) MainPID → in-place swap of the new bin.
        log "reloading systemd (unit changed)"
        $SUDO systemctl daemon-reload
        # IMPORTANT: a SIGHUP execve keeps the running process's existing privilege + kill settings.
        # systemd applies unit-level directives (User=/Group=, KillMode=, NoNewPrivileges=,
        # CapabilityBoundingSet=, Slice=/Delegate=) only when it STARTS the service fresh — NOT on a
        # hot-swap. The binary is updated live below, but if you changed any of those directives, run
        # a `--cold` deploy (or `systemctl restart`) to actually apply them.
        err "note: unit changed — the binary hot-swaps live, but unit-level directives (User=, KillMode,"
        err "      NoNewPrivileges, CapabilityBoundingSet, Slice/Delegate) need '--cold' to take effect."
    fi

    # (c) TRIGGER — ExecReload=/bin/kill -HUP $MAINPID → the daemon re-execs the new binary in place.
    log "triggering in-place hot-swap: systemctl reload ${SERVICE} (SIGHUP → execve)"
    $SUDO systemctl reload "$SERVICE"

    # (d) VERIFY — poll up to ~HEALTH_TRIES seconds for ALL of:
    #       (i)   GET /version reports the NEW build,
    #       (ii)  MainPID is UNCHANGED (== old_pid) → proves an in-place execve, not a systemd restart,
    #       (iii) /health is ready.
    log "verifying the swap (new version live, same PID, healthy) ..."
    local i body new_pid ver_ok=0 pid_ok=0 health_ok=0
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        # (iii) health
        if [[ "$health_ok" -eq 0 ]]; then
            curl -fsS -o /dev/null --max-time 2 --unix-socket "$WD_SOCK" http://localhost/health 2>/dev/null && health_ok=1
        fi
        # (i) version — only meaningful once we know what to expect; otherwise treat as satisfied.
        if [[ "$ver_ok" -eq 0 ]]; then
            if [[ -z "$expected_version" ]]; then
                ver_ok=1
            else
                body="$(http_version || true)"
                # The /version body is JSON ({"version":"X","commit":"<hash>"}); `--version` prints the
                # COMBINED informational form "X+<hash>". Compare on the COMMIT HASH (the part after the
                # last '+'), which the JSON carries verbatim — the combined "X+hash" string never appears
                # in the JSON, so a naive whole-string substring match always misses. `${v##*+}` leaves
                # the whole string when the build has no '+hash' (non-SourceLink build), a safe fallback.
                local expected_token="${expected_version##*+}"
                [[ -n "$body" && "$body" == *"$expected_token"* ]] && ver_ok=1
            fi
        fi
        # (ii) PID unchanged
        new_pid="$($SUDO systemctl show -p MainPID --value "$SERVICE" 2>/dev/null || true)"
        if [[ -n "$old_pid" && "$new_pid" == "$old_pid" ]]; then
            pid_ok=1
        else
            pid_ok=0   # a changed/empty PID means systemd restarted us — keep watching, then warn below
        fi

        if [[ "$health_ok" -eq 1 && "$ver_ok" -eq 1 && "$pid_ok" -eq 1 ]]; then
            log "hot-swap verified: version='${expected_version:-?}' live on UNCHANGED PID ${new_pid}, healthy ✓"
            return 0
        fi
        sleep 1
    done

    # ── Verify failed — diagnose precisely. ──────────────────────────────────────────────────────
    err "hot-swap verification did NOT complete within ${HEALTH_TRIES}s."
    err "  /health ready:        $([[ $health_ok -eq 1 ]] && echo yes || echo NO)"
    err "  /version is new build:$([[ $ver_ok    -eq 1 ]] && echo ' yes' || echo ' NO')  (expected '${expected_version:-?}')"
    err "  MainPID unchanged:    $([[ $pid_ok    -eq 1 ]] && echo yes || echo NO)  (was ${old_pid:-?}, now ${new_pid:-?})"

    if [[ "$pid_ok" -eq 0 && -n "$old_pid" && -n "$new_pid" && "$new_pid" != "$old_pid" ]]; then
        err "the MainPID CHANGED — the daemon was RESTARTED by systemd instead of hot-swapping in place."
        err "(supervised games were re-adopted by the fresh daemon, but an EOF-sensitive game may have"
        err " lost its console — the very thing the hot-swap exists to avoid. Check the SIGHUP handler"
        err " in the daemon build and that ExecReload= is present in ${UNIT_DST}.)"
    elif [[ "$ver_ok" -eq 0 ]]; then
        err "the daemon is still serving the OLD version — the in-place swap likely did NOT commit."
        err "the daemon's own safety gate may have ABORTED the swap (its internal --selfcheck of the"
        err "new image failed) and kept running the OLD image. This is NOT a data-loss event: the"
        err "previous binary is still live and supervising games. Check the daemon log below for the"
        err "abort reason, fix the new build, and re-deploy."
    fi
    err "recent logs:"
    $SUDO journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the KGSM user (systemd runs the daemon as YOUR user)."
    err "it builds as you and sudo's only the systemd steps."
    exit 1
fi
[[ -f "$PROJECT" ]] || { err "project not found: $PROJECT"; exit 1; }
command -v clang >/dev/null 2>&1 || err "warning: 'clang' not found — Native-AOT publish needs a C toolchain (clang + zlib). Install it if publish fails."

# ── 1. Build (Native-AOT, as the invoking user) ────────────────────────────────
log "publishing Native-AOT (${RID}) → ${PUBLISH_DIR} (ILC compile — this takes a minute)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Privileged prep ─────────────────────────────────────────────────────────
log "ensuring install dir ${PREFIX}"
$SUDO install -d -m 0755 "$PREFIX"

# Settings file → beside the binary, where the daemon loads it from AppContext.BaseDirectory (Program.cs).
# WITHOUT this the daemon never sees its settings (the slim builder's content root is "/" under systemd),
# so e.g. the ASP.NET log level silently stays at the chatty Information default. Installed BEFORE the swap
# below so the (re-)exec'd daemon reads the matching settings. 0644 = world-readable, so the uid-dropped
# daemon (and a hot-swap re-exec running as that uid) can read it. Shipped app defaults → overwrite on every
# deploy to stay version-matched with the binary (operator overrides go through env vars, not this file).
SETTINGS_SRC="$PUBLISH_DIR/kgsm-watchdog.settings.json"
if [[ -f "$SETTINGS_SRC" ]]; then
    log "installing settings → ${PREFIX}/kgsm-watchdog.settings.json"
    $SUDO install -m 0644 "$SETTINGS_SRC" "$PREFIX/kgsm-watchdog.settings.json"
else
    err "warning: ${SETTINGS_SRC} not found in publish output — daemon will fall back to built-in defaults."
fi

# Env file (optional in the unit): create from template if absent, never clobber.
if [[ ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} from template (optional overrides)"
    $SUDO install -d -m 0755 "$ENV_DIR"
    $SUDO install -m 0644 "$ENV_EXAMPLE" "$ENV_FILE"
fi

# Unit: substitute the run-as User=/Group= (the delegation owner), install only if changed.
TMP_UNIT="$(mktemp)"
sed "s/^User=.*/User=${WD_USER}/; \
     s/^Group=.*/Group=${WD_GROUP}/" "$UNIT_SRC" > "$TMP_UNIT"
UNIT_CHANGED=0
if ! cmp -s "$TMP_UNIT" "$UNIT_DST"; then
    log "installing systemd unit → ${UNIT_DST} (runs as User=${WD_USER} Group=${WD_GROUP}, Slice=kgsm.slice Delegate=yes)"
    $SUDO install -m 0644 "$TMP_UNIT" "$UNIT_DST"
    UNIT_CHANGED=1
fi
rm -f "$TMP_UNIT"

# ── 3. Choose mode + install ─────────────────────────────────────────────────────
# DEFAULT = HOT (zero-downtime in-place swap) when the service is already running and --cold was not
# passed. Otherwise COLD: a first install (nothing running to swap) or an explicit --cold bounce.
if [[ "$COLD" -eq 0 ]] && $SUDO systemctl is-active --quiet "$SERVICE"; then
    log "${SERVICE} is active → HOT swap (in-place, zero downtime). Pass --cold to force a bounce."
    hot_swap
    # hot_swap already verified version/PID/health (and exits non-zero on failure) — done.
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    if [[ "$COLD" -eq 1 ]]; then
        log "--cold given → COLD swap (stop → install → start)."
    else
        log "${SERVICE} is not active → COLD swap (first install / bounce)."
    fi
    cold_swap

    # ── 4. Verify (an actual 200 from /health over the control socket) ─────────────
    log "waiting for ${SERVICE} to report healthy on ${WD_SOCK} ..."
    if wait_health; then
        log "kgsm-watchdog is up and healthy ✓"
        systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
    else
        err "service started but GET /health on ${WD_SOCK} did not return 200 within ${HEALTH_TRIES}s."
        err "(if it reports 'kgsm.slice is not a writable delegated base', the cgroup bootstrap failed —"
        err " check that this host has cgroup v2 and the daemon could boot as root.) Recent logs:"
        $SUDO journalctl -u "$SERVICE" -n 30 --no-pager || true
        exit 1
    fi
fi
