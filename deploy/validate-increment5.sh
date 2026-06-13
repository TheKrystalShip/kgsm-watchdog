#!/usr/bin/env bash
# kgsm-watchdog — Increment 5 root validation: enable/disable decoupled from start/stop.
#
# Proves the systemctl-style split — boot-autostart (enable/disable) is an axis INDEPENDENT of runtime
# (start/stop). Each claim is an observable side effect across a REAL daemon, driving the new control
# endpoints directly with curl (so this is independent of the `kgsm autostart` bash front-end):
#   A. ENABLE persists boot intent WITHOUT starting the game (enable ≠ start);
#   B. START does NOT add to the boot set (start ≠ enable) — a bare start won't survive a reboot;
#   C. STOP does NOT remove from the boot set (stop ≠ disable) — an enabled instance stays enabled;
#   D. DISABLE does NOT stop a running game (disable ≠ stop);
#   E. boot restore follows the ENABLED set: an enabled-but-stopped instance is respawned on restart,
#      a started-but-not-enabled instance is not.
#
# Usage:  sudo bash deploy/validate-increment5.sh [instance]   # default: 7dtd
# Run via sudo (not a root login): the daemon drops to $SUDO_UID/$SUDO_GID and writes the state file
# into a path that user owns; kgsm is invoked as that user so it shares the instance store + socket.
# Stop the deployed unit first (this script binds the same control socket): sudo systemctl stop kgsm-watchdog
set -u

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="${REPO}/src/Watchdog/bin/Release/net10.0/linux-x64/publish/kgsm-watchdog"
KGSM="${KGSM_WATCHDOG_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
SOCK="/run/kgsm-watchdog/control.sock"
CGROOT="/sys/fs/cgroup/kgsm.slice"
STATE="/tmp/wd-inc5-state.json"
INST="${1:-7dtd}"

export KGSM_WATCHDOG_POLL_INTERVAL_MS=500
export KGSM_WATCHDOG_RESTART_GRACE_SEC=3
export KGSM_WATCHDOG_STATE_FILE="${STATE}"

[ "$(id -u)" -eq 0 ] || { echo "FATAL: run with sudo"; exit 1; }
[ -n "${SUDO_UID:-}" ] || { echo "FATAL: no SUDO_UID — run via 'sudo', not as a root login"; exit 1; }
[ -x "${BIN}" ] || { echo "FATAL: binary missing: ${BIN} (run: dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64)"; exit 1; }
if systemctl is-active --quiet kgsm-watchdog 2> /dev/null; then
  echo "FATAL: the deployed daemon owns ${SOCK} — stop it first: sudo systemctl stop kgsm-watchdog"; exit 1
fi

USER_HOME="$(getent passwd "${SUDO_UID}" | cut -d: -f6)"
rm -f "${STATE}" "${STATE}.tmp"

pass=0; fail=0
ok() { echo "  PASS: $1"; pass=$((pass + 1)); }
no() { echo "  FAIL: $1"; fail=$((fail + 1)); }
chk() { if [ "$1" -eq 0 ]; then ok "$2"; else no "$2"; fi; }

kgsm_user() {
  sudo -u "#${SUDO_UID}" -- env HOME="${USER_HOME}" KGSM_WATCHDOG_SOCKET="${SOCK}" \
    KGSM_WATCHDOG_STATE_FILE="${STATE}" "${KGSM}" "$@"
}

populated()  { grep -q '^populated 1' "${CGROOT}/${1}/cgroup.events" 2> /dev/null; }
in_list()    { curl -s --unix-socket "${SOCK}" "http://x/list" 2> /dev/null | grep -q "\"name\":\"${1}\""; }
state_has()  { grep -q "\"${1}\"" "${STATE}" 2> /dev/null; }
crash()      { echo 1 > "${CGROOT}/${1}/cgroup.kill" 2> /dev/null; }
wait_pop()   { local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" && return 0; sleep 0.1; done; return 1; }
wait_unpop() { local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" || return 0; sleep 0.1; done; return 1; }

# New control endpoints, exercised directly (curl), echoing the HTTP status so failures are visible.
wd_enable()   { curl -s -o /dev/null -w '%{http_code}' -X POST --unix-socket "${SOCK}" "http://x/enable/$1" 2> /dev/null; }
wd_disable()  { curl -s -o /dev/null -w '%{http_code}' -X POST --unix-socket "${SOCK}" "http://x/disable/$1" 2> /dev/null; }
enabled_has() { curl -s --unix-socket "${SOCK}" "http://x/enabled" 2> /dev/null | grep -q "\"${1}\""; }

DPID=""
launch() {   # launch <logfile-tag>
  KGSM_WATCHDOG_KGSM_PATH="${KGSM}" "${BIN}" > "/tmp/wd-inc5-$1.log" 2>&1 &
  DPID=$!
  local i; for i in $(seq 1 60); do [ -S "${SOCK}" ] && break; sleep 0.1; done
  curl -s --unix-socket "${SOCK}" http://x/ready 2> /dev/null | grep -q '"ready":true'
}
stop_daemon() {  # SIGTERM the daemon WITHOUT touching the game's cgroup
  [ -n "${DPID}" ] && { kill "${DPID}" 2> /dev/null; wait "${DPID}" 2> /dev/null; }
  DPID=""
  local i; for i in $(seq 1 30); do [ -S "${SOCK}" ] || break; sleep 0.1; done
}

# shellcheck disable=SC2329  # invoked indirectly via the EXIT trap
cleanup() {
  [ -d "${CGROOT}/${INST}" ] && crash "${INST}"
  [ -n "${DPID}" ] && { kill "${DPID}" 2> /dev/null; wait "${DPID}" 2> /dev/null; }
  rm -f "${STATE}" "${STATE}.tmp"
  return 0
}
trap cleanup EXIT

echo "== launching daemon #1 (root -> drops to uid ${SUDO_UID}); state file: ${STATE} =="
launch d1 || { no "supervisor ready"; echo "RESULT: ${pass} passed, ${fail} failed"; exit 1; }
ok "supervisor ready"

echo "== A. ENABLE persists boot intent WITHOUT starting (enable ≠ start) =="
CODE="$(wd_enable "${INST}")"; echo "  POST /enable/${INST} -> ${CODE}"
if [ "${CODE}" = "200" ]; then ok "enable returned 200"; else no "enable returned ${CODE}"; fi
enabled_has "${INST}"; chk $? "GET /enabled lists ${INST} after enable"
state_has "${INST}"; chk $? "desired-state file lists ${INST} after enable"
sleep 1
if populated "${INST}"; then no "enable started the game (it must not)"; else ok "enable did NOT start the game"; fi

echo "== B. START does NOT enable (start ≠ enable) =="
CODE="$(wd_disable "${INST}")"; echo "  reset: POST /disable/${INST} -> ${CODE}"
if enabled_has "${INST}"; then no "disable failed to clear the enabled set"; else ok "reset: ${INST} not enabled"; fi
OUT="$(kgsm_user start "${INST}" 2>&1)"; echo "  kgsm start ${INST} -> ${OUT}"
wait_pop "${INST}" 150; chk $? "instance entered kgsm.slice/${INST} after start"
if enabled_has "${INST}"; then no "start added ${INST} to the boot set (it must not)"; else ok "start did NOT enable ${INST}"; fi
if state_has "${INST}"; then no "start wrote ${INST} to the state file"; else ok "state file unchanged by start"; fi

echo "== C. STOP does NOT disable (stop ≠ disable) =="
CODE="$(wd_enable "${INST}")"; echo "  POST /enable/${INST} -> ${CODE} (now running AND enabled)"
enabled_has "${INST}"; chk $? "${INST} enabled while running"
OUT="$(kgsm_user stop "${INST}" 2>&1)"; echo "  kgsm stop ${INST} -> ${OUT}"
wait_unpop "${INST}" 200; chk $? "instance drained after stop"
enabled_has "${INST}"; chk $? "${INST} STILL enabled after stop (stop ≠ disable)"

echo "== D. DISABLE does NOT stop a running game (disable ≠ stop) =="
OUT="$(kgsm_user start "${INST}" 2>&1)"; echo "  kgsm start ${INST} -> ${OUT}"
wait_pop "${INST}" 150; chk $? "instance running again"
CODE="$(wd_disable "${INST}")"; echo "  POST /disable/${INST} -> ${CODE}"
if [ "${CODE}" = "200" ]; then ok "disable returned 200"; else no "disable returned ${CODE}"; fi
if enabled_has "${INST}"; then no "disable failed to clear the boot set"; else ok "${INST} no longer enabled"; fi
sleep 1
if populated "${INST}"; then ok "game STILL running after disable (disable ≠ stop)"; else no "disable stopped the game (it must not)"; fi

echo "== E. boot restore follows the ENABLED set =="
# E1: enabled-but-stopped -> respawned on restart.
CODE="$(wd_enable "${INST}")"; echo "  POST /enable/${INST} -> ${CODE}"
OUT="$(kgsm_user stop "${INST}" 2>&1)"; echo "  kgsm stop ${INST} -> ${OUT} (enabled, not running)"
wait_unpop "${INST}" 200; chk $? "enabled-but-stopped before restart (cgroup empty)"
stop_daemon
launch d2 || no "daemon #2 ready"
wait_pop "${INST}" 200; chk $? "restore SPAWNED the enabled-but-stopped ${INST}"
# E2: started-but-not-enabled -> NOT restored after a host-reboot sim.
CODE="$(wd_disable "${INST}")"; echo "  POST /disable/${INST} -> ${CODE} (running but not enabled)"
if enabled_has "${INST}"; then no "disable failed"; else ok "${INST} running but not enabled"; fi
stop_daemon
crash "${INST}"; wait_unpop "${INST}" 100; chk $? "game gone before restart (host-reboot sim)"
launch d3 || no "daemon #3 ready"
sleep 1   # let restore run (nothing to restore)
if in_list "${INST}"; then no "started-but-not-enabled instance was auto-restored"; else ok "started-but-not-enabled NOT restored"; fi
if populated "${INST}"; then no "non-enabled instance cgroup repopulated"; else ok "non-enabled instance stays down across restart"; fi

echo
echo "RESULT: ${pass} passed, ${fail} failed   (daemon logs: /tmp/wd-inc5-d{1,2,3}.log)"
exit "${fail}"
