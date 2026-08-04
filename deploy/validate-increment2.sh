#!/usr/bin/env bash
# kgsm-watchdog — Increment 2 root validation: crash detection + restart with backoff.
#
# Proves the supervision behaviour the unit suite CANNOT reach (it needs real cgroups + a real
# game process), each by an observable side effect rather than "it didn't error":
#   A. a crashed instance is restarted (new PID lands back in the cgroup);
#   B. a crash-loop gives up and reports phase=failed (no endless restarts);
#   C. a manual start clears the give-up latch, and a deliberate stop is NOT restarted.
#
# A "crash" is simulated by writing the instance's own cgroup.kill (atomic whole-tree SIGKILL) — the
# same teardown an OOM-kill or segfault produces, so the daemon sees populated->0 with a non-zero
# leader exit. Kills are spaced by waiting for the NEW pid each time, so nothing races.
#
# Usage:
#   sudo bash deploy/validate-increment2.sh [instance]      # default instance: 7dtd
# Must be run via sudo (not a root login): the daemon drops to $SUDO_UID/$SUDO_GID.
set -u

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="${REPO}/src/Watchdog/bin/Release/net10.0/linux-x64/publish/kgsm-watchdog"
KGSM="${KGSM_WATCHDOG_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
SOCK="/run/kgsm-watchdog/control.sock"
CGROOT="/sys/fs/cgroup/kgsm.slice"
INST="${1:-7dtd}"

# Tuned so the give-up path runs in seconds, not minutes (defaults are 60s/5min-scale).
GRACE=3
export Watchdog__RestartGraceSeconds="${GRACE}"
export Watchdog__RestartBaseDelayMs=500
export Watchdog__RestartMaxDelayMs=2000
export Watchdog__RestartMaxRetries=2          # 2 restarts, then give up on the 3rd crash
export Watchdog__RestartStabilitySeconds=3600     # never auto-resets the streak mid-test
export Watchdog__PollIntervalMs=500

[ "$(id -u)" -eq 0 ] || { echo "FATAL: run with sudo"; exit 1; }
[ -n "${SUDO_UID:-}" ] || { echo "FATAL: no SUDO_UID — run via 'sudo', not as a root login"; exit 1; }
[ -x "${BIN}" ] || { echo "FATAL: binary missing: ${BIN} (run: dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64)"; exit 1; }

pass=0; fail=0
ok() { echo "  PASS: $1"; pass=$((pass + 1)); }
no() { echo "  FAIL: $1"; fail=$((fail + 1)); }
chk() { if [ "$1" -eq 0 ]; then ok "$2"; else no "$2"; fi; }   # chk <exit-status> <description>

leader_pid() { head -n1 "${CGROOT}/${1}/cgroup.procs" 2> /dev/null; }
populated()  { grep -q '^populated 1' "${CGROOT}/${1}/cgroup.events" 2> /dev/null; }
crash()      { echo 1 > "${CGROOT}/${1}/cgroup.kill" 2> /dev/null; }
status()     { curl -s --unix-socket "${SOCK}" "http://x/status/${1}"; }

wait_pop() { # inst deciseconds
  local i; for ((i = 0; i < ${2:-100}; i++)); do populated "$1" && return 0; sleep 0.1; done; return 1
}
wait_new_pid() { # inst oldpid deciseconds -> prints new pid
  local i p; for ((i = 0; i < ${3:-200}; i++)); do
    p="$(leader_pid "$1")"
    if [ -n "${p}" ] && [ "${p}" != "$2" ]; then echo "${p}"; return 0; fi
    sleep 0.1
  done; return 1
}

# shellcheck disable=SC2329  # invoked indirectly via the EXIT trap
cleanup() {
  if [ -n "${DPID:-}" ]; then kill "${DPID}" 2> /dev/null; wait "${DPID}" 2> /dev/null; fi
  # belt-and-suspenders: never leave the test instance running if a phase aborted early
  [ -d "${CGROOT}/${INST}" ] && crash "${INST}"
  return 0
}
trap cleanup EXIT

echo "== launching daemon as root; it will drop to uid ${SUDO_UID} =="
Watchdog__KgsmPath="${KGSM}" "${BIN}" > /tmp/wd-root-inc2.log 2>&1 &
DPID=$!
for _ in $(seq 1 50); do [ -S "${SOCK}" ] && break; sleep 0.1; done
READY="$(curl -s --unix-socket "${SOCK}" http://x/ready)"
echo "  /ready -> ${READY}"
if ! echo "${READY}" | grep -q '"ready":true'; then no "supervisor ready"; echo "RESULT: ${pass} passed, ${fail} failed"; exit 1; fi
ok "supervisor ready"

echo "== A. crash -> restart =="
R="$(curl -s --unix-socket "${SOCK}" -X POST "http://x/start/${INST}")"
echo "  /start/${INST} -> ${R}"
wait_pop "${INST}" 100; chk $? "instance populated after start"
P1="$(leader_pid "${INST}")"; echo "  leader pid: ${P1}"
sleep "$((GRACE + 1))"                 # clear the post-spawn grace window so the crash is detected
crash "${INST}"; echo "  simulated crash (cgroup.kill) of ${P1}"
P2="$(wait_new_pid "${INST}" "${P1}" 200)"
if [ -n "${P2}" ]; then ok "crashed instance restarted (new pid ${P2})"; else no "crashed instance restarted"; fi
SA="$(status "${INST}")"; echo "  /status -> ${SA}"
echo "${SA}" | grep -q '"phase":"running"'; chk $? "phase=running after restart"

echo "== B. crash-loop -> failed (give up at maxRetries=2) =="
# Phase A already recorded failure #1 (P1 -> P2). Two more crashes reach the give-up threshold.
sleep "$((GRACE + 1))"; crash "${INST}"; echo "  crash #2 of ${P2}"
P3="$(wait_new_pid "${INST}" "${P2}" 200)"
if [ -n "${P3}" ]; then ok "second crash restarted (new pid ${P3})"; else no "second crash restarted"; fi
sleep "$((GRACE + 1))"; crash "${INST}"; echo "  crash #3 of ${P3} (exceeds maxRetries=2 -> should give up)"
sleep "$((GRACE + 4))"                 # past grace + max backoff: a restart would have happened by now
if populated "${INST}"; then no "gave up: no restart after crash-loop"; else ok "gave up: no restart after crash-loop"; fi
SB="$(status "${INST}")"; echo "  /status -> ${SB}"
echo "${SB}" | grep -q '"phase":"failed"'; chk $? "phase=failed reported"
echo "${SB}" | grep -q '"restarts":3';     chk $? "restarts=3 surfaced on /status"

echo "== C. manual start clears give-up; deliberate stop -> no restart =="
R="$(curl -s --unix-socket "${SOCK}" -X POST "http://x/start/${INST}")"
echo "  /start/${INST} (after failed) -> ${R}"
echo "${R}" | grep -q '"ok":true'; chk $? "manual start cleared the give-up latch"
wait_pop "${INST}" 100; chk $? "running again after manual start"
R="$(curl -s --unix-socket "${SOCK}" -X POST "http://x/stop/${INST}")"
echo "  /stop/${INST} -> ${R}"
sleep "$((GRACE + 4))"                  # a deliberate stop must NOT come back
if populated "${INST}"; then no "deliberately-stopped instance stays down"; else ok "deliberately-stopped instance stays down"; fi
SC="$(curl -s -o /dev/null -w '%{http_code}' --unix-socket "${SOCK}" "http://x/status/${INST}")"
if [ "${SC}" = "404" ]; then ok "stopped instance dropped from the table (404)"; else no "stopped instance dropped from the table (got ${SC})"; fi

echo
echo "RESULT: ${pass} passed, ${fail} failed   (daemon log: /tmp/wd-root-inc2.log)"
exit "${fail}"
