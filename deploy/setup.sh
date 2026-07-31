#!/usr/bin/env bash
#
# setup.sh — one-shot host provisioning for this project. Run it ONCE per host.
#
#   ./deploy/setup.sh
#
# This is the ONLY script in the project that needs privilege. It asks for your sudo password,
# provisions everything that lives outside your ownership, and hands the host over to
# deploy/deploy.sh — which from then on deploys with NO password and NO prompt, forever.
#
# Idempotent: safe to re-run any time. A steady-state re-run changes nothing (every install is
# cmp-guarded). Re-run it after changing the systemd unit's User=/Group=, adding a unit, or if
# deploy.sh tells you to.
#
# What it provisions:
#   1. ${PREFIX}                    — the install tree, chowned to you, so deploy.sh writes it
#                                     unprivileged.
#   2. ${ENV_DIR}/…env              — operator config, seeded from the repo template if absent.
#                                     NEVER overwritten: your secrets survive every redeploy.
#   3. ${UNIT_DIR}                  — a directory YOU own that holds the real systemd unit files.
#   4. /etc/systemd/system/<unit>   — a symlink into ${UNIT_DIR}. systemd resolves and enables
#                                     units through symlinks, so this is what lets deploy.sh
#                                     update a unit by writing a file it already owns.
#   5. a scoped polkit rule         — lets you drive systemctl for THIS project's units with no
#                                     password and no interactive auth agent.
#   6. the units enabled + started.
#
# Then it VERIFIES the grant by making the same unprivileged systemctl calls deploy.sh will,
# and fails loudly if they would have prompted.
#
# Security note, stated plainly: ${UNIT_DIR} is writable by ${DEPLOY_USER}, and systemd runs
# those units as root. Anyone who can write there can make PID 1 run anything. That is a
# deliberate trade for a single-admin host where the deploying user already holds full sudo —
# it moves no boundary that was not already open. Do NOT adopt this layout on a host where the
# deploying user is meant to be less privileged than root.
#
# Non-interactive (CI / an agent): no password ever lives in this script — point sudo at an
# askpass helper instead.
#   printf '#!/bin/sh\necho YOUR_PASS\n' > /tmp/ap && chmod 700 /tmp/ap
#   SUDO='sudo -A' SUDO_ASKPASS=/tmp/ap ./deploy/setup.sh
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

trap 'err "setup failed (line ${LINENO}). The host may be partly provisioned — re-run this script."; exit 1' ERR

refuse_root

log "provisioning ${PROJECT} for headless deploys by '${DEPLOY_USER}'"

# Prove sudo works up front, so a wrong password fails here and not halfway through.
$SUDO true || { err "sudo is not usable — setup needs it once."; exit 1; }

# ── 1. The install prefix (owned by the deploying user) ───────────────────────
if [[ ! -d "$PREFIX" ]]; then
    log "creating ${PREFIX} (owned by ${DEPLOY_USER})"
    $SUDO install -d -m 0755 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" "$PREFIX"
elif [[ "$(stat -c '%U' "$PREFIX")" != "$DEPLOY_USER" ]]; then
    log "chowning ${PREFIX} → ${DEPLOY_USER}:${DEPLOY_GROUP} (so deploys need no privilege)"
    $SUDO chown -R "${DEPLOY_USER}:${DEPLOY_GROUP}" "$PREFIX"
fi

# ── 2. Operator config — seeded once, never clobbered ─────────────────────────
if [[ -n "${ENV_EXAMPLE:-}" && ! -f "$ENV_FILE" ]]; then
    log "creating ${ENV_FILE} from template — EDIT IT before relying on this service"
    $SUDO install -d -m 0755 "$ENV_DIR"
    $SUDO install -m 0640 -g "$DEPLOY_GROUP" "$ENV_EXAMPLE" "$ENV_FILE"
    ENV_SEEDED=1
else
    [[ -d "$ENV_DIR" ]] || $SUDO install -d -m 0755 "$ENV_DIR"
    ENV_SEEDED=0
fi

# ── 3. The user-owned unit directory ──────────────────────────────────────────
if [[ ! -d "$UNIT_DIR" ]]; then
    log "creating ${UNIT_DIR} (owned by ${DEPLOY_USER} — this is what makes deploys sudo-free)"
    $SUDO install -d -m 0755 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" "$UNIT_DIR"
elif [[ ! -w "$UNIT_DIR" ]]; then
    $SUDO chown -R "${DEPLOY_USER}:${DEPLOY_GROUP}" "$UNIT_DIR"
fi

# Preserve whatever is live before we take the unit over. A hand-edit on this host that never
# made it back into the repo would otherwise vanish silently on the first deploy.
for u in "${UNITS[@]}"; do
    live="${SYSTEMD_DIR}/${u}"
    if [[ -f "$live" && ! -L "$live" ]]; then
        rendered="$(mktemp)"; render_unit "$u" > "$rendered"
        if ! cmp -s "$rendered" "$live"; then
            warn "the live ${live} differs from what this repo renders."
            warn "keeping a copy at ${UNIT_DIR}/${u}.pre-setup.bak — diff it if that drift mattered."
            $SUDO install -m 0644 -o "$DEPLOY_USER" -g "$DEPLOY_GROUP" \
                 "$live" "${UNIT_DIR}/${u}.pre-setup.bak"
        fi
        rm -f "$rendered"
    fi
done

# Write the units into the directory we now own (unprivileged, same call deploy.sh makes).
install_units_unprivileged

# ── 4. Point systemd at them ──────────────────────────────────────────────────
LINKS_CHANGED=0
for u in "${UNITS[@]}"; do
    live="${SYSTEMD_DIR}/${u}"
    if [[ -L "$live" && "$(readlink -f "$live")" == "${UNIT_DIR}/${u}" ]]; then
        continue
    fi
    log "linking ${live} → ${UNIT_DIR}/${u}"
    $SUDO rm -f "$live"
    $SUDO ln -sfn "${UNIT_DIR}/${u}" "$live"
    LINKS_CHANGED=1
done

if [[ "$LINKS_CHANGED" -eq 1 || "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    $SUDO systemctl daemon-reload
fi

# ── 5. The polkit grant ───────────────────────────────────────────────────────
# Scoped to exactly this project's units and exactly the verbs deploy.sh uses. Returning
# polkit.Result.YES means no auth agent is consulted, which is what makes a headless,
# session-less deploy possible.
#
# reload-daemon is granted unscoped because the action carries no unit to match on — systemd's
# daemon-reload is global by nature. It re-reads unit files; it starts and stops nothing.
POLKIT_RENDERED="$(mktemp)"
{
    cat <<EOF
// 48-${PROJECT}-deploy.rules — headless-deploy grant for ${PROJECT}.
//
// Installed by ${PROJECT}/deploy/setup.sh. Lets '${DEPLOY_USER}' run the systemctl verbs that
// deploy/deploy.sh needs, for THIS project's units only, with no password and no interactive
// auth agent. Every other unit on the host is untouched by this rule.
//
// Managed by setup.sh — re-run it rather than hand-editing this file.
polkit.addRule(function(action, subject) {
    if (subject.user !== "${DEPLOY_USER}") {
        return;
    }

    // daemon-reload: global by nature (the action carries no unit), re-reads unit files only.
    if (action.id === "org.freedesktop.systemd1.reload-daemon") {
        return polkit.Result.YES;
    }

    if (action.id !== "org.freedesktop.systemd1.manage-units") {
        return;
    }

    var units = {
EOF
    for u in "${UNITS[@]}"; do printf '        "%s": true,\n' "$u"; done
    cat <<'EOF'
    };
    if (units[action.lookup("unit")] !== true) {
        return;
    }

    var verb = action.lookup("verb");
    if (verb === "start" || verb === "stop" || verb === "restart" ||
        verb === "reload" || verb === "try-restart" || verb === "reload-or-restart") {
        return polkit.Result.YES;
    }
});
EOF
} > "$POLKIT_RENDERED"
# Trailing comma in the units object is valid JS and keeps the generator simple.

if $SUDO cmp -s "$POLKIT_RENDERED" "$POLKIT_DST" 2>/dev/null; then
    : # already current
else
    log "installing polkit grant → ${POLKIT_DST}"
    $SUDO install -D -m 0644 "$POLKIT_RENDERED" "$POLKIT_DST"
fi
rm -f "$POLKIT_RENDERED"

# ── 6. Enable + start ─────────────────────────────────────────────────────────
for u in "${ENABLE_UNITS[@]}"; do
    log "enabling + starting ${u}"
    $SUDO systemctl enable --now "$u"
done

# ── 7. Verify the grant actually works, unprivileged ──────────────────────────
# The point of this script is that deploy.sh never needs a password. Prove it here rather than
# discovering it on the next deploy. Both calls are the real, polkit-gated ones deploy.sh makes:
# daemon-reload (reload-daemon) and start on an already-running unit (manage-units, a no-op job).
log "verifying the headless grant (no password expected) ..."
GRANT_OK=0
for _ in 1 2 3 4 5; do
    # polkitd picks up rules.d via inotify; give it a moment on the first pass.
    if systemctl --no-ask-password daemon-reload 2>/dev/null &&
       systemctl --no-ask-password start "$SERVICE" 2>/dev/null; then
        GRANT_OK=1
        break
    fi
    sleep 1
done

if [[ "$GRANT_OK" -ne 1 ]]; then
    err "the unprivileged systemctl calls were REFUSED — deploy.sh would prompt for a password."
    err "check that polkit is running and that ${POLKIT_DST} names '${DEPLOY_USER}' and ${SERVICE}:"
    err "    systemctl status polkit"
    err "    sudo cat ${POLKIT_DST}"
    exit 1
fi

# ── 8. Project-specific provisioning ──────────────────────────────────────────
# Whatever else this project needs set up once (extra state dirs, a CLI symlink on PATH, a
# feature's drop-ins). Defined in deploy-common.sh's PROJECT BLOCK; a no-op for most projects.
setup_project_extras

trap - ERR

# ── 9. Summary ────────────────────────────────────────────────────────────────
log "${PROJECT} is provisioned ✓  — deploys are now headless"
printf '   prefix : %s (owned by %s)\n' "$PREFIX" "$DEPLOY_USER"
printf '   units  : %s → %s/\n' "${UNITS[*]}" "$UNIT_DIR"
printf '   grant  : %s may start/stop/restart/reload %s with no password\n' "$DEPLOY_USER" "${UNITS[*]}"
[[ -n "${HEALTH_URL:-}" ]] && printf '   health : %s\n' "$HEALTH_URL"
if [[ -f "$ENV_FILE" ]]; then
    printf '   config : %s%s\n' "$ENV_FILE" \
        "$([[ "$ENV_SEEDED" -eq 1 ]] && printf '  ← FRESH FROM TEMPLATE, EDIT IT' || printf '  (untouched)')"
fi
printf '\n   deploy from now on with:  %s/deploy/deploy.sh   (no sudo, no prompts)\n' "$REPO_DIR"
