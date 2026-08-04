#!/usr/bin/env bash
# kgsm-watchdog — Increment 1 root validation.
#
# Exercises the privileged paths the unit suite CANNOT reach (they only run under root):
# the root self-bootstrap (slice create + controller enable + chown), supervisor-cgroup
# entry, and the setgroups -> setresgid -> setresuid privilege drop. Each is proven by an
# observable side effect, not by "it didn't error".
#
# Usage:
#   sudo bash deploy/validate-increment1.sh                 # privilege machinery only (cheap)
#   sudo bash deploy/validate-increment1.sh --spawn 7dtd    # ALSO fork a real native instance
#
# Must be run via sudo (not a root login): the daemon drops to $SUDO_UID/$SUDO_GID.
set -u

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="$REPO/src/Watchdog/bin/Release/net10.0/linux-x64/publish/kgsm-watchdog"
KGSM="${KGSM_WATCHDOG_KGSM_PATH:-/home/heisen/tks/kgsm/kgsm.sh}"
SOCK="/run/kgsm-watchdog/control.sock"

SPAWN=""
[ "${1:-}" = "--spawn" ] && SPAWN="${2:-}"

[ "$(id -u)" -eq 0 ] || { echo "FATAL: run with sudo"; exit 1; }
[ -n "${SUDO_UID:-}" ] || { echo "FATAL: no SUDO_UID — run via 'sudo', not as a root login"; exit 1; }
uid="$SUDO_UID"
gid="${SUDO_GID:-$SUDO_UID}"
[ -x "$BIN" ] || { echo "FATAL: binary missing: $BIN (run: dotnet publish src/Watchdog/Watchdog.csproj -c Release -r linux-x64)"; exit 1; }

pass=0; fail=0
ok() { echo "  PASS: $1"; pass=$((pass + 1)); }
no() { echo "  FAIL: $1"; fail=$((fail + 1)); }

echo "== launching daemon as root; it will drop to uid ${uid} gid ${gid} =="
Watchdog__KgsmPath="$KGSM" "$BIN" > /tmp/wd-root.log 2>&1 &
DPID=$!
for _ in $(seq 1 50); do [ -S "$SOCK" ] && break; sleep 0.1; done

echo "== A. supervisor readiness (proves slice create + controllers + chown + entry) =="
READY="$(curl -s --unix-socket "$SOCK" http://x/ready)"
echo "  /ready -> $READY"
if echo "$READY" | grep -q '"ready":true'; then ok "ready=true"; else no "ready=true"; fi

echo "== B. daemon lives in kgsm.slice/supervisor (proves in-slice after the drop) =="
CG="$(cat "/proc/${DPID}/cgroup" 2> /dev/null)"
echo "  /proc/${DPID}/cgroup -> $CG"
if echo "$CG" | grep -q "kgsm.slice/supervisor"; then ok "in kgsm.slice/supervisor"; else no "in kgsm.slice/supervisor"; fi

echo "== C. privilege dropped (proves setgroups + setresgid + setresuid) =="
grep -E '^(Uid|Gid|Groups):' "/proc/${DPID}/status" | sed 's/^/  /'
if grep -qE "^Uid:[[:space:]]+${uid}[[:space:]]" "/proc/${DPID}/status"; then ok "uid -> ${uid}"; else no "uid -> ${uid}"; fi
if grep -qE "^Gid:[[:space:]]+${gid}[[:space:]]" "/proc/${DPID}/status"; then ok "gid -> ${gid}"; else no "gid -> ${gid}"; fi
groups_line="$(awk '/^Groups:/{$1=""; print}' "/proc/${DPID}/status")"
has_root=0
for g in $groups_line; do [ "$g" = "0" ] && has_root=1; done
if [ "$has_root" -eq 0 ]; then ok "root group (0) discarded"; else no "root group (0) discarded"; fi

if [ -n "$SPAWN" ]; then
  echo "== D. spawn '$SPAWN' into its own cgroup (proves the self-move launcher + mkfifo) =="
  R="$(curl -s --unix-socket "$SOCK" -X POST "http://x/start/${SPAWN}")"
  echo "  /start/${SPAWN} -> $R"
  if echo "$R" | grep -q '"ok":true'; then ok "start ok"; else no "start ok"; fi
  sleep 2
  PROCS="$(cat "/sys/fs/cgroup/kgsm.slice/${SPAWN}/cgroup.procs" 2> /dev/null | tr '\n' ' ')"
  echo "  kgsm.slice/${SPAWN}/cgroup.procs -> [${PROCS}]"
  if [ -n "$(echo "$PROCS" | tr -d '[:space:]')" ]; then ok "game process is inside the instance cgroup"; else no "game process is inside the instance cgroup"; fi

  echo "== E. stop '$SPAWN' and confirm teardown (graceful -> cgroup.kill -> remove) =="
  R="$(curl -s --unix-socket "$SOCK" -X POST "http://x/stop/${SPAWN}")"
  echo "  /stop/${SPAWN} -> $R"
  sleep 1
  if [ ! -d "/sys/fs/cgroup/kgsm.slice/${SPAWN}" ]; then ok "instance cgroup removed"; else no "instance cgroup removed"; fi
fi

echo "== cleanup: stopping daemon =="
kill "$DPID" 2> /dev/null
wait "$DPID" 2> /dev/null

echo
echo "RESULT: ${pass} passed, ${fail} failed   (daemon log: /tmp/wd-root.log)"
exit "$fail"
