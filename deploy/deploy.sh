#!/usr/bin/env bash
#
# deploy.sh — build + deploy the kgsm-watchdog supervisor. Fully headless: no sudo, no prompts.
#
#   ./deploy/deploy.sh            # HOT-swap if the service is already running (default), else COLD
#   ./deploy/deploy.sh --cold     # force the stop → install → start bounce
#
# Assumes deploy/setup.sh has provisioned this host (prefix owned by you, the unit symlinked out
# of a directory you own, polkit grant in place). If it has not, this script says so and stops
# before building. Publishes the Native-AOT binary as YOU — a single self-contained native
# binary, NO .NET runtime needed on the host.
#
# ── Two install modes ─────────────────────────────────────────────────────────────────────────
#  HOT  (default when `systemctl is-active kgsm-watchdog` is true and --cold is NOT given):
#       a zero-downtime in-place binary update. The new binary is validated with `--selfcheck`
#       BEFORE it is installed, atomically renamed onto the live path (rename(2) → the running
#       process keeps its old now-unlinked inode), then `systemctl reload` sends SIGHUP and the
#       daemon re-execs the new image IN PLACE — same PID, games keep their fds/cgroups/PIDs, no
#       supervised game restarts. Verified by /version (new build) + an UNCHANGED MainPID (proves an
#       in-place swap, not a systemd restart) + a healthy /health.
#  COLD (--cold, or the service is not running): the stop → install → start bounce (supervised
#       games are re-adopted by the fresh daemon, but an EOF-sensitive game may lose its console).
#
# DAEMON-SIDE DEPENDENCY: the HOT path requires a binary that supports `--version`, `--selfcheck`,
# `GET /version` over the control socket, and a SIGHUP handler that re-execs in place. Against an
# OLDER binary lacking these, prefer `--cold`. The HOT path fails CLOSED: a non-zero --selfcheck on
# the NEW binary aborts the deploy before anything is installed (the running daemon is untouched).
#
# A unit-level change (User=, KillMode, NoNewPrivileges, CapabilityBoundingSet, Slice/Delegate)
# needs `--cold` to take effect — a SIGHUP execve keeps the running process's privilege and kill
# settings. This script warns when it detects that case.
#
# Knobs: RID, WD_SOCK, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

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

PROJECT_CSPROJ="$REPO_DIR/src/Watchdog/Watchdog.csproj"
BIN="$PREFIX/kgsm-watchdog"
RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap — attempting to bring it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

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

# COLD swap (full restart): reload unit → stop → install → start. daemon-reload happens BEFORE the
# stop so the stop honors the NEW unit's KillMode=process — otherwise systemd would kill the games
# (which live under the service cgroup) with the daemon. The fresh start is also what applies
# unit-level directives a hot-swap can't.
cold_swap() {
    if [[ "$UNIT_CHANGED" -eq 1 ]]; then
        log "reloading systemd (unit changed) before stop — so KillMode=process + hardening apply this cycle"
        sysctl_do daemon-reload
    fi

    log "stopping ${SERVICE} (KillMode=process → games keep running, re-adopted on start)"
    sysctl_do stop "$SERVICE" || true
    STOPPED=1

    log "installing binary → ${BIN}"
    install -m 0755 "$PUBLISH_DIR/kgsm-watchdog" "$BIN"

    log "starting ${SERVICE}"
    sysctl_do start "$SERVICE"
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
    [[ -n "$expected_version" ]] || warn "new binary did not print a --version; PID-unchanged check will be the swap proof."

    old_pid="$(systemctl show -p MainPID --value "$SERVICE" 2>/dev/null || true)"
    log "current ${SERVICE} MainPID=${old_pid:-unknown}; new build version='${expected_version:-?}'"

    # (b) ATOMIC INSTALL — install to a sibling temp in the SAME dir, then rename(2) onto the live
    #     path. rename within a dir is atomic: the running process keeps its old (now-unlinked) inode
    #     while the path flips to the new inode. NEVER install/cp directly over the running file —
    #     that truncates+writes the live inode (ETXTBSY at best, a corrupted mmap'd image at worst).
    log "atomic install → ${BIN} (via ${PREFIX}/.kgsm-watchdog.new + rename)"
    install -m 0755 "$new_bin" "$PREFIX/.kgsm-watchdog.new"
    mv -f "$PREFIX/.kgsm-watchdog.new" "$BIN"

    if [[ "$UNIT_CHANGED" -eq 1 ]]; then
        # A reload won't pick up unit-file changes; daemon-reload makes systemd re-read the unit.
        # The ExecReload itself still just SIGHUPs the (same) MainPID → in-place swap of the new bin.
        log "reloading systemd (unit changed)"
        sysctl_do daemon-reload
        # IMPORTANT: a SIGHUP execve keeps the running process's existing privilege + kill settings.
        # systemd applies unit-level directives (User=/Group=, KillMode=, NoNewPrivileges=,
        # CapabilityBoundingSet=, Slice=/Delegate=) only when it STARTS the service fresh — NOT on a
        # hot-swap. The binary is updated live below, but if you changed any of those directives, run
        # a `--cold` deploy (or `systemctl restart`) to actually apply them.
        warn "unit changed — the binary hot-swaps live, but unit-level directives (User=, KillMode,"
        warn "     NoNewPrivileges, CapabilityBoundingSet, Slice/Delegate) need '--cold' to take effect."
    fi

    # (c) TRIGGER — ExecReload=/bin/kill -HUP $MAINPID → the daemon re-execs the new binary in place.
    log "triggering in-place hot-swap: systemctl reload ${SERVICE} (SIGHUP → execve)"
    sysctl_do reload "$SERVICE"

    # (d) VERIFY — poll up to ~HEALTH_TRIES seconds for ALL of:
    #       (i)   GET /version reports the NEW build,
    #       (ii)  MainPID is UNCHANGED (== old_pid) → proves an in-place execve, not a systemd restart,
    #       (iii) /health is ready.
    log "verifying the swap (new version live, same PID, healthy) ..."
    local i body new_pid ver_ok=0 pid_ok=0 health_ok=0
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        # (iii) health
        if [[ "$health_ok" -eq 0 ]]; then
            health_probe && health_ok=1
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
        new_pid="$(systemctl show -p MainPID --value "$SERVICE" 2>/dev/null || true)"
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
        err " in the daemon build and that ExecReload= is present in the unit.)"
    elif [[ "$ver_ok" -eq 0 ]]; then
        err "the daemon is still serving the OLD version — the in-place swap likely did NOT commit."
        err "the daemon's own safety gate may have ABORTED the swap (its internal --selfcheck of the"
        err "new image failed) and kept running the OLD image. This is NOT a data-loss event: the"
        err "previous binary is still live and supervising games. Check the daemon log below for the"
        err "abort reason, fix the new build, and re-deploy."
    fi
    err "recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
}

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -f "$PROJECT_CSPROJ" ]] || { err "project not found: $PROJECT_CSPROJ"; exit 1; }
command -v clang >/dev/null 2>&1 || warn "'clang' not found — Native-AOT publish needs a C toolchain (clang + zlib). Install it if publish fails."

# ── 1. Build (Native-AOT, as the invoking user) ────────────────────────────────
log "publishing Native-AOT (${RID}) → ${PUBLISH_DIR} (ILC compile — this takes a minute)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT_CSPROJ" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Settings + unit (both land in paths we own — no privilege) ──────────────
# Settings file → beside the binary, where the daemon loads it from AppContext.BaseDirectory
# (Program.cs). WITHOUT this the daemon never sees its settings (the slim builder's content root is
# "/" under systemd), so e.g. the ASP.NET log level silently stays at the chatty Information default.
# Installed BEFORE the swap so the (re-)exec'd daemon reads the matching settings. Shipped app
# defaults → overwritten every deploy to stay version-matched with the binary (operator overrides go
# through env vars, not this file).
SETTINGS_SRC="$PUBLISH_DIR/kgsm-watchdog.settings.json"
if [[ -f "$SETTINGS_SRC" ]]; then
    log "installing settings → ${PREFIX}/kgsm-watchdog.settings.json"
    install -m 0644 "$SETTINGS_SRC" "$PREFIX/kgsm-watchdog.settings.json"
else
    warn "${SETTINGS_SRC} not found in publish output — daemon falls back to built-in defaults."
fi

install_units_unprivileged

# ── 2b. Publish the leaf config descriptor ────────────────────────────────────
# Before the swap, so the surface kgsm-api reads never lags the binary that implements it.
install_leaf_descriptor

# ── 3. Choose mode + install ─────────────────────────────────────────────────────
# DEFAULT = HOT (zero-downtime in-place swap) when the service is already running and --cold was not
# passed. Otherwise COLD: a first install (nothing running to swap) or an explicit --cold bounce.
if [[ "$COLD" -eq 0 ]] && systemctl is-active --quiet "$SERVICE"; then
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
        err " check that this host has cgroup v2 and the daemon could boot.) Recent logs:"
        journalctl -u "$SERVICE" -n 30 --no-pager || true
        exit 1
    fi
fi
