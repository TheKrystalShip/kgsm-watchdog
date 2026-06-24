#!/usr/bin/env bash
#
# Build + deploy the kgsm-watchdog supervisor in one go.
#
#   ./deploy/deploy.sh
#
# Publishes the Native-AOT binary as YOU (the invoking user) and uses sudo ONLY for the steps that
# touch systemd / root-owned paths. The artifact is a single self-contained native binary — NO .NET
# runtime needed.
#
# Installs the ROOT-BOOT variant (the recommended, most-tested unit): the daemon boots as root,
# self-bootstraps kgsm.slice, then DROPS to the KGSM user. That drop target (KGSM_WATCHDOG_UID/GID)
# is set to the invoking user's numeric uid/gid, so a fresh host needs no dedicated 'kgsm' user.
# (The rootless variant — deploy/kgsm-watchdog.rootless.service — needs `kgsm system setup-cgroups`
# and a real kgsm user first; deploy it by hand if you specifically want a never-root daemon.)
#
#   * binary → /opt/kgsm-watchdog/kgsm-watchdog,
#   * unit   → kgsm-watchdog.service with KGSM_WATCHDOG_UID/GID rewritten to your uid/gid,
#   * verified by an actual "ok"/200 from GET /health over the control unix socket.
#
# Non-interactive: SUDO='sudo -A' SUDO_ASKPASS=/path/to/askpass ./deploy/deploy.sh
#
set -euo pipefail

# ── Paths / config ────────────────────────────────────────────────────────────
PREFIX="/opt/kgsm-watchdog"
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
# The user the daemon drops to after the root bootstrap (owner of the cgroup subtree + socket dir).
WD_UID="${KGSM_WATCHDOG_UID:-$(id -u)}"
WD_GID="${KGSM_WATCHDOG_GID:-$(id -g)}"
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

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the KGSM user (the daemon drops to YOUR uid/gid)."
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
log "installing binary → ${PREFIX}/kgsm-watchdog"
$SUDO install -d -m 0755 "$PREFIX"

# Env file (optional in the unit): create from template if absent, never clobber.
if [[ ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} from template (optional overrides)"
    $SUDO install -d -m 0755 "$ENV_DIR"
    $SUDO install -m 0644 "$ENV_EXAMPLE" "$ENV_FILE"
fi

# Unit: substitute the drop-to uid/gid, install only if changed.
TMP_UNIT="$(mktemp)"
sed "s/^Environment=KGSM_WATCHDOG_UID=.*/Environment=KGSM_WATCHDOG_UID=${WD_UID}/; \
     s/^Environment=KGSM_WATCHDOG_GID=.*/Environment=KGSM_WATCHDOG_GID=${WD_GID}/" "$UNIT_SRC" > "$TMP_UNIT"
UNIT_CHANGED=0
if ! cmp -s "$TMP_UNIT" "$UNIT_DST"; then
    log "installing systemd unit → ${UNIT_DST} (drops to uid=${WD_UID} gid=${WD_GID})"
    $SUDO install -m 0644 "$TMP_UNIT" "$UNIT_DST"
    UNIT_CHANGED=1
fi
rm -f "$TMP_UNIT"

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
$SUDO systemctl stop "$SERVICE" 2>/dev/null || true
STOPPED=1

log "installing binary → ${PREFIX}/kgsm-watchdog"
$SUDO install -m 0755 "$PUBLISH_DIR/kgsm-watchdog" "$PREFIX/kgsm-watchdog"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    $SUDO systemctl daemon-reload
fi

log "enabling + starting ${SERVICE}"
$SUDO systemctl enable --now "$SERVICE" >/dev/null 2>&1 || $SUDO systemctl start "$SERVICE"
STOPPED=0

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
