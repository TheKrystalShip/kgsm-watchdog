#!/usr/bin/env bash
# kgsm-watchdog — Increment 3 root validation: kgsm CLI <-> watchdog routing.
#
# Proves the integration the unit suite can only mock: that `kgsm start|stop|is-active`
# for a NATIVE STANDALONE instance actually drive the RUNNING daemon end-to-end
# (bash -> curl --unix-socket -> Kestrel -> routing -> real instance in its cgroup),
# not the legacy direct-spawn path — and that they fall back to the direct path when
# the daemon is absent. Each claim is checked by an observable side effect:
#   A. `kgsm start` -> the instance enters kgsm.slice/<inst> AND appears in the
#      daemon's /list (a direct spawn would do neither);
#   B. `kgsm is-active` -> reports active off the daemon's /status (the management
#      script writes no PID file for a daemon-spawned process, so its own check
#      would be stale);
#   C. `kgsm stop` -> the daemon tears the cgroup down and drops it from /list;
#   D. with the daemon down, the routing gate (__watchdog_available) goes false, so
#      kgsm transparently uses the direct path.
#
# Usage:  sudo bash deploy/validate-increment3.sh [instance]   # default: 7dtd
# Run via sudo (not a root login): the daemon drops to $SUDO_UID/$SUDO_GID, and kgsm
# is invoked as that user so it reads the same instance store and hits the same socket.
set -u

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="${REPO}/src/Watchdog/bin/Release/net10.0/linux-x64/publish/kgsm-watchdog"
KGSM="${KGSM_WATCHDOG_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
KGSM_DIR="$(dirname "${KGSM}")"
SOCK="/run/kgsm-watchdog/control.sock"
CGROOT="/sys/fs/cgroup/kgsm.slice"
INST="${1:-7dtd}"

export KGSM_WATCHDOG_POLL_INTERVAL_MS=500
export KGSM_WATCHDOG_RESTART_GRACE_SEC=3

[ "$(id -u)" -eq 0 ] || { echo "FATAL: run with sudo"; exit 1; }
[ -n "${SUDO_UID:-}" ] || { echo "FATAL: no SUDO_UID — run via 'sudo', not as a root login"; exit 1; }
[ -x "${BIN}" ] || { echo "FATAL: binary missing: ${BIN} (run: dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64)"; exit 1; }

USER_HOME="$(getent passwd "${SUDO_UID}" | cut -d: -f6)"

pass=0; fail=0
ok() { echo "  PASS: $1"; pass=$((pass + 1)); }
no() { echo "  FAIL: $1"; fail=$((fail + 1)); }
chk() { if [ "$1" -eq 0 ]; then ok "$2"; else no "$2"; fi; }   # chk <exit-status> <description>

# Invoke kgsm as the original (dropped) user, so it reads that user's instance store
# and reaches the socket the daemon bound after dropping privilege.
kgsm_user() {
  sudo -u "#${SUDO_UID}" -- env HOME="${USER_HOME}" KGSM_WATCHDOG_SOCKET="${SOCK}" "${KGSM}" "$@"
}

populated()  { grep -q '^populated 1' "${CGROOT}/${1}/cgroup.events" 2> /dev/null; }
in_list()    { curl -s --unix-socket "${SOCK}" "http://x/list" 2> /dev/null | grep -q "\"name\":\"${1}\""; }
# shellcheck disable=SC2329  # invoked indirectly via the EXIT trap (cleanup)
crash()      { echo 1 > "${CGROOT}/${1}/cgroup.kill" 2> /dev/null; }
wait_pop()   { local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" && return 0; sleep 0.1; done; return 1; }
wait_unpop() { local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" || return 0; sleep 0.1; done; return 1; }

# shellcheck disable=SC2329  # invoked indirectly via the EXIT trap
cleanup() {
  [ -d "${CGROOT}/${INST}" ] && crash "${INST}"
  if [ -n "${DPID:-}" ]; then kill "${DPID}" 2> /dev/null; wait "${DPID}" 2> /dev/null; fi
  return 0
}
trap cleanup EXIT

echo "== launching daemon as root; it will drop to uid ${SUDO_UID} =="
KGSM_WATCHDOG_KGSM_PATH="${KGSM}" "${BIN}" > /tmp/wd-root-inc3.log 2>&1 &
DPID=$!
for _ in $(seq 1 50); do [ -S "${SOCK}" ] && break; sleep 0.1; done
READY="$(curl -s --unix-socket "${SOCK}" http://x/ready)"
echo "  /ready -> ${READY}"
if ! echo "${READY}" | grep -q '"ready":true'; then no "supervisor ready"; echo "RESULT: ${pass} passed, ${fail} failed"; exit 1; fi
ok "supervisor ready"

echo "== A. 'kgsm start' routes to the daemon (not the direct path) =="
OUT="$(kgsm_user start "${INST}" 2>&1)"; RC=$?; echo "  kgsm start ${INST} -> rc=${RC} :: ${OUT}"
wait_pop "${INST}" 150; chk $? "instance entered kgsm.slice/${INST} (daemon cgroup spawn)"
in_list "${INST}"; chk $? "daemon /list shows ${INST} (proves the CLI routed to the daemon)"

echo "== B. 'kgsm is-active' reflects the daemon's view =="
kgsm_user is-active "${INST}" > /dev/null 2>&1
chk $? "kgsm is-active -> active (off daemon /status, not a stale PID file)"

echo "== C. 'kgsm stop' routes to the daemon =="
OUT="$(kgsm_user stop "${INST}" 2>&1)"; RC=$?; echo "  kgsm stop ${INST} -> rc=${RC} :: ${OUT}"
wait_unpop "${INST}" 150; chk $? "instance cgroup drained after kgsm stop"
if in_list "${INST}"; then no "daemon still lists ${INST} after stop"; else ok "daemon dropped ${INST} from /list after stop"; fi
if kgsm_user is-active "${INST}" > /dev/null 2>&1; then no "kgsm is-active -> inactive after stop"; else ok "kgsm is-active -> inactive after stop"; fi

echo "== D. fallback: daemon down -> routing gate goes false =="
kill "${DPID}" 2> /dev/null; wait "${DPID}" 2> /dev/null; DPID=""
for _ in $(seq 1 30); do [ -S "${SOCK}" ] || break; sleep 0.1; done
# __watchdog_available depends only on curl + the socket check + config vars, so it
# can be sourced standalone (no full kgsm bootstrap). With the socket gone it must
# report unavailable, which is exactly what makes kgsm fall back to the direct path.
if sudo -u "#${SUDO_UID}" -- env KGSM_WATCHDOG_SOCKET="${SOCK}" \
     bash -c 'source "'"${KGSM_DIR}"'/commands/handlers/watchdog.sh"; __watchdog_available'; then
  no "watchdog reported available with the daemon down"
else
  ok "watchdog unavailable with daemon down -> kgsm uses the direct path"
fi

echo
echo "RESULT: ${pass} passed, ${fail} failed   (daemon log: /tmp/wd-root-inc3.log)"
exit "${fail}"
