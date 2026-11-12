#!/usr/bin/env bash
# Runs the Android lifecycle runbook against an installed, launchable app and prints the results as a Markdown table.
# Usage: android-lifecycle.sh <adb serial> <application id> [sustained minutes, default 30]
# A tick is proven by the app's CPU time: a rendering game uses it every frame, a paused one stops.
set -u

serial=$1
package=$2
minutes=${3:-30}
adb=${ADB:-${ANDROID_HOME:?set ANDROID_HOME or ADB}/platform-tools/adb}
failures=0
rows=()

device() { "$adb" -s "$serial" "$@"; }
remote() { device shell "$@"; }

# CPU jiffies the app used over 1.5 s; -1 when it is not running.
busy() {
  remote 'p=$(pidof '"$package"') || { echo -1; exit 0; }; a=$(cut -d" " -f14,15 /proc/$p/stat | tr " " +); sleep 1.5; b=$(cut -d" " -f14,15 /proc/$p/stat | tr " " +); echo $(( ($b) - ($a) ))' | tr -d '\r'
}
pid() { remote pidof "$package" | tr -d '\r'; }
record() { # <cell> <ok 0|1> <detail>
  local status=PASS
  if [ "$2" -ne 0 ]; then status=FAIL; failures=$((failures + 1)); fi
  rows+=("| $1 | $status | $3 |")
  echo "$status $1 - $3" >&2
}
ticking() { [ "$(busy)" -ge 20 ]; }
# A backgrounded app may finish a few seconds of wind-down work, so it must go idle within three samples.
paused() { local n; for _ in 1 2 3; do n=$(busy); { [ "$n" -ge 0 ] && [ "$n" -le 5 ]; } && return 0; done; return 1; }
launch() { remote monkey -p "$package" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1; sleep 4; }
home() { remote input keyevent KEYCODE_HOME; sleep 2; }
crashes() { device logcat -d | grep -cE "ANR in $package|FATAL EXCEPTION|Fatal signal|VUID-" || true; }
unlocked() { remote dumpsys window 2>/dev/null | grep -q "isKeyguardShowing=false"; }
# Waits for a person to unlock a secure lock screen; the script never enters credentials.
wait_unlocked() {
  for _ in $(seq 1 "$1"); do unlocked && return 0; echo "Unlock the phone." >&2; sleep 5; done
  return 1
}

original_rotation=$(remote settings get system accelerometer_rotation | tr -d '\r')
restore() {
  remote settings put system accelerometer_rotation "${original_rotation:-1}" >/dev/null
  remote settings put system user_rotation 0 >/dev/null
  remote settings put global always_finish_activities 0 >/dev/null
}
trap restore EXIT

model=$(remote getprop ro.product.model | tr -d '\r')
release=$(remote getprop ro.build.version.release | tr -d '\r')
patch=$(remote getprop ro.build.version.security_patch | tr -d '\r')
remote settings put global always_finish_activities 0 >/dev/null
remote input keyevent KEYCODE_WAKEUP
wait_unlocked 120 || { echo "The phone stayed locked." >&2; exit 2; }
device logcat -c

# Cold launch from the launcher icon after a force stop; the first log line names the GPU and driver.
remote am force-stop "$package"; sleep 2
launch; sleep 3
gpu=$(device logcat -d -s Turian:I | grep -m1 "Model=" | sed 's/.*Turian *: //')
if ticking; then record "force-stop, cold launch from icon" 0 "ticking"; else record "force-stop, cold launch from icon" 1 "not ticking"; fi
record "no crash, ANR or validation error after cold launch" "$([ "$(crashes)" -eq 0 ]; echo $?)" "log scan"

# Background and foreground, twenty times; the process must survive, stop ticking away and tick again.
bad=0
base=$(pid)
for i in $(seq 1 20); do
  home
  paused || { bad=$((bad + 1)); echo "cycle $i: still busy in the background" >&2; }
  launch
  { ticking && [ "$(pid)" = "$base" ]; } || { bad=$((bad + 1)); echo "cycle $i: not ticking or process changed after return" >&2; }
done
record "background/foreground x20" "$([ "$bad" -eq 0 ]; echo $?)" "$bad failed checks, same process"

# Rotation, ten changes, with auto-rotate off so the runbook controls the orientation.
remote settings put system accelerometer_rotation 0 >/dev/null
bad=0
for i in $(seq 1 10); do
  remote settings put system user_rotation $((i % 2 == 1 ? 1 : 0)) >/dev/null; sleep 3
  { ticking && [ "$(pid)" = "$base" ]; } || bad=$((bad + 1))
done
remote settings put system user_rotation 0 >/dev/null; sleep 2
record "rotation x10" "$([ "$bad" -eq 0 ]; echo $?)" "$bad failed checks, same process"

remote cmd statusbar expand-notifications; sleep 3
remote cmd statusbar collapse; sleep 2
{ ticking && [ "$(pid)" = "$base" ]; } && ok=0 || ok=1
record "notification shade" "$ok" "same process, ticking after"

# Process death while backgrounded, then the task returns: the activity restarts from its saved state.
home
remote am kill "$package"; sleep 2
[ -z "$(pid)" ] && gone=0 || gone=1
component=$(remote cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$package" | tail -n1 | tr -d '\r')
remote am start -n "$component" >/dev/null; sleep 5
ticking && back=0 || back=1
record "process death in background, return to the task" "$((gone + back))" "process killed: $([ $gone -eq 0 ] && echo yes || echo no); ticking after: $([ $back -eq 0 ] && echo yes || echo no)"

# Activity destruction without process death.
remote settings put global always_finish_activities 1 >/dev/null
bad=0
base=$(pid)
for i in 1 2 3; do
  home
  launch
  { ticking && [ "$(pid)" = "$base" ]; } || bad=$((bad + 1))
done
remote settings put global always_finish_activities 0 >/dev/null
record "Don't keep activities x3" "$([ "$bad" -eq 0 ]; echo $?)" "$bad failed checks"

# Sustained run, one sample a minute.
base=$(pid)
bad=0
samples=()
for m in $(seq 1 "$minutes"); do
  sleep 57
  load=$(busy)
  temp=$(remote dumpsys battery | grep -m1 temperature | tr -d ' \r' | cut -d: -f2)
  thermal=$(remote dumpsys thermalservice | grep -m1 "Thermal Status" | tr -d '\r' | sed 's/.*: *//')
  memory=$(remote dumpsys meminfo "$package" | grep -m1 -E "TOTAL PSS:" | sed -E 's/.*TOTAL PSS: *([0-9]+).*/\1/')
  samples+=("$m:load=$load,temp=$((temp / 10)).$((temp % 10))C,thermal=${thermal:-n/a},pss=${memory:-?}KB")
  { [ "$load" -ge 20 ] && [ "$(pid)" = "$base" ]; } || bad=$((bad + 1))
done
record "sustained run, $minutes min" "$([ "$bad" -eq 0 ]; echo $?)" "$bad bad samples; first ${samples[0]:-none}; last ${samples[-1]:-none}"
record "no crash, ANR or validation error in the whole run" "$([ "$(crashes)" -eq 0 ]; echo $?)" "log scan"

# Screen off and on, last and only on request: a secure lock screen needs a person to unlock it.
if [ "${LOCK_TEST:-0}" = 1 ]; then
  remote input keyevent KEYCODE_SLEEP; sleep 3
  paused && off=0 || off=1
  remote input keyevent KEYCODE_WAKEUP
  wait_unlocked 24; sleep 3
  ticking && on=0 || on=1
  record "lock/unlock" "$((off + on))" "stopped while off: $([ $off -eq 0 ] && echo yes || echo no); ticking after unlock: $([ $on -eq 0 ] && echo yes || echo no)"
fi

echo "Device: $model, Android $release (security patch $patch)"
echo "GPU: ${gpu:-unknown}"
echo
echo "| Cell | Result | Detail |"
echo "|---|---|---|"
printf '%s\n' "${rows[@]}"
echo
echo "Sustained samples (minute: busy jiffies per 1.5 s, battery temperature, thermal status, PSS):"
printf '%s\n' "${samples[@]}"
exit $((failures > 0 ? 1 : 0))
