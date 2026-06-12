#!/usr/bin/env bash
# kgsm-watchdog — Increment 4 root validation: desired-state persistence + restore-on-boot.
#
# Proves the in-house replacement for systemd's `enable`/`WantedBy=` boot auto-start — the thing the
# unit suite can only test in pieces (DesiredStateStore round-trips, RestorePlan's fork). Each claim
# is checked by an observable side effect across a REAL daemon restart, with the state file surviving
# in between (that persistence is the whole point):
#   A. `kgsm start` -> the instance name lands in the on-disk desired-state file (intent persisted);
#   B. ADOPT: kill the daemon while the game survives (FIFO held open here so it can't hit stdin EOF),
#      restart the daemon -> it RE-ADOPTS the live cgroup: same PID, no kill, no respawn;
#   C. RESPAWN: with the daemon down, cgroup.kill the game (a host-reboot leaves nothing running),
#      restart the daemon -> restore SPAWNS it fresh (new PID, cgroup repopulated);
#   D. PRUNE: `kgsm stop` drops the name from the desired-state file, so a later restart does NOT
#      auto-start it.
#
# Usage:  sudo bash deploy/validate-increment4.sh [instance]   # default: 7dtd
# Run via sudo (not a root login): the daemon drops to $SUDO_UID/$SUDO_GID and writes the state file
# into a path that user owns; kgsm is invoked as that user so it shares the instance store + socket.
set -u

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="${REPO}/src/Watchdog/bin/Release/net10.0/linux-x64/publish/kgsm-watchdog"
KGSM="${KGSM_WATCHDOG_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
SOCK="/run/kgsm-watchdog/control.sock"
CGROOT="/sys/fs/cgroup/kgsm.slice"
STATE="/tmp/wd-inc4-state.json"      # explicit so we can inspect/clean it deterministically
INST="${1:-7dtd}"

export KGSM_WATCHDOG_POLL_INTERVAL_MS=500
export KGSM_WATCHDOG_RESTART_GRACE_SEC=3
export KGSM_WATCHDOG_STATE_FILE="${STATE}"

[ "$(id -u)" -eq 0 ] || { echo "FATAL: run with sudo"; exit 1; }
[ -n "${SUDO_UID:-}" ] || { echo "FATAL: no SUDO_UID — run via 'sudo', not as a root login"; exit 1; }
[ -x "${BIN}" ] || { echo "FATAL: binary missing: ${BIN} (run: dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64)"; exit 1; }

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
leader_pid() { head -1 "${CGROOT}/${1}/cgroup.procs" 2> /dev/null; }
state_has()  { grep -q "\"${1}\"" "${STATE}" 2> /dev/null; }
crash()      { echo 1 > "${CGROOT}/${1}/cgroup.kill" 2> /dev/null; }
wait_pop()   { local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" && return 0; sleep 0.1; done; return 1; }
wait_unpop() { local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" || return 0; sleep 0.1; done; return 1; }
wait_list()  { local i; for ((i = 0; i < ${2:-100}; i++)); do in_list "$1" && return 0; sleep 0.1; done; return 1; }

# The instance's stdin FIFO, so the harness can hold it open across a daemon kill (Phase B) and the
# game never sees EOF — isolating the adopt-code path from game-specific stdin-EOF behavior.
fifo_path() {
  kgsm_user instances info "${INST}" --json 2> /dev/null \
    | grep -o '"socket_file"[: ]*"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/'
}

DPID=""
launch() {   # launch <logfile-tag>
  KGSM_WATCHDOG_KGSM_PATH="${KGSM}" "${BIN}" > "/tmp/wd-inc4-$1.log" 2>&1 &
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
  [ -n "${HOLD_FD:-}" ] && eval "exec ${HOLD_FD}>&-" 2> /dev/null
  [ -n "${DPID}" ] && { kill "${DPID}" 2> /dev/null; wait "${DPID}" 2> /dev/null; }
  rm -f "${STATE}" "${STATE}.tmp"
  return 0
}
trap cleanup EXIT

echo "== launching daemon #1 (root -> drops to uid ${SUDO_UID}); state file: ${STATE} =="
launch d1 || { no "supervisor ready"; echo "RESULT: ${pass} passed, ${fail} failed"; exit 1; }
ok "supervisor ready"

echo "== A. 'kgsm start' persists intent to the desired-state file =="
OUT="$(kgsm_user start "${INST}" 2>&1)"; echo "  kgsm start ${INST} -> ${OUT}"
wait_pop "${INST}" 150; chk $? "instance entered kgsm.slice/${INST}"
state_has "${INST}"; chk $? "desired-state file lists ${INST} after start"
PID1="$(leader_pid "${INST}")"; echo "  leader pid = ${PID1:-<none>}"

echo "== B. ADOPT: daemon restart with the game alive -> re-adopt, no respawn =="
FIFO="$(fifo_path)"; echo "  stdin FIFO = ${FIFO:-<unresolved>}"
HOLD_FD=""
if [ -n "${FIFO}" ] && [ -p "${FIFO}" ]; then
  exec {HOLD_FD}<>"${FIFO}"   # hold a writer open so killing the daemon can't EOF the game's stdin
fi
stop_daemon
populated "${INST}"; chk $? "game survived the daemon kill (cgroup still populated)"
launch d2 || no "daemon #2 ready"
wait_list "${INST}" 100; chk $? "restore re-listed ${INST} after restart"
PID2="$(leader_pid "${INST}")"; echo "  leader pid after restart = ${PID2:-<none>}"
if [ -n "${PID1}" ] && [ "${PID1}" = "${PID2}" ]; then
  ok "ADOPTED live cgroup — same pid ${PID1} (no kill+respawn)"
else
  no "expected adopt (pid unchanged ${PID1}); got ${PID2:-<none>}"
fi
[ -n "${HOLD_FD}" ] && { eval "exec ${HOLD_FD}>&-"; HOLD_FD=""; }

echo "== C. RESPAWN: daemon down + game killed (host-reboot sim) -> spawn fresh =="
stop_daemon
crash "${INST}"; wait_unpop "${INST}" 100; chk $? "game gone before restart (cgroup empty)"
launch d3 || no "daemon #3 ready"
wait_pop "${INST}" 200; chk $? "restore SPAWNED ${INST} fresh (cgroup repopulated)"
wait_list "${INST}" 100 > /dev/null
PID3="$(leader_pid "${INST}")"; echo "  leader pid after respawn = ${PID3:-<none>}"
if [ -n "${PID3}" ] && [ "${PID3}" != "${PID1}" ]; then
  ok "RESPAWNED — new pid ${PID3} (distinct from the original ${PID1})"
else
  no "expected a fresh spawn with a new pid; got ${PID3:-<none>}"
fi

echo "== D. PRUNE: 'kgsm stop' removes intent; a later restart does NOT auto-start =="
OUT="$(kgsm_user stop "${INST}" 2>&1)"; echo "  kgsm stop ${INST} -> ${OUT}"
# Assert the drain finished BEFORE we kill the daemon — otherwise a slow 7dtd graceful stop could
# orphan the game and make the final 'stays down' check fail spuriously (a script-timing artifact,
# not a feature bug). If this FAILs, raise the timeout; don't read the later checks as real.
wait_unpop "${INST}" 200; chk $? "instance drained after kgsm stop (before daemon kill)"
if state_has "${INST}"; then no "desired-state still lists ${INST} after stop"; else ok "desired-state pruned ${INST} after stop"; fi
stop_daemon
launch d4 || no "daemon #4 ready"
sleep 1   # let restore run (nothing to restore)
if in_list "${INST}"; then no "stopped instance was auto-restored"; else ok "stopped instance not auto-restored"; fi
if populated "${INST}"; then no "stopped instance cgroup repopulated"; else ok "stopped instance stays down across restart"; fi

echo
echo "RESULT: ${pass} passed, ${fail} failed   (daemon logs: /tmp/wd-inc4-d{1,2,3,4}.log)"
exit "${fail}"
