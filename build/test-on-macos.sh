#!/usr/bin/env bash
# Runs the test suite on a Mac and packs everything needed to analyse the result into one archive:
# what the machine is, the build output, the console output and a .trx per test class, the adapter trace of every
# debug session (DAP messages both ways plus the engine's own log) and a resource monitor.
#
#   usage: build/test-on-macos.sh [options]
#     --filter EXPR       one run with this "dotnet test" filter instead of the class-by-class runs
#                         (e.g. --filter "FullyQualifiedName~FindingsTests.NonAscii")
#     --matrix            also the runtime matrix (.NET 8/9/10, single-file, ReadyToRun); slow
#     --stress            also the stress tests
#     --enable-devtools   run "sudo DevToolsSecurity -enable" first (asks for the password)
#     --chunk-timeout N   seconds one test class may take before it is killed (default 600)
#     --keep              keep the log directory after packing
#
# The archive ends up in artifacts/ and its path is the last line printed. It contains paths of this machine and the
# user name; nothing else personal (the debuggees are the programs under tests/).
#
# Works with the bash 3.2 that ships with macOS.
set -u

filter=""
matrix=0
stress=0
enable_devtools=0
chunk_timeout=600
keep=0
while [ $# -gt 0 ]; do
  case "$1" in
    --filter) filter="$2"; shift 2 ;;
    --matrix) matrix=1; shift ;;
    --stress) stress=1; shift ;;
    --enable-devtools) enable_devtools=1; shift ;;
    --chunk-timeout) chunk_timeout="$2"; shift 2 ;;
    --keep) keep=1; shift ;;
    -h|--help) sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root" || exit 1
stamp="$(date +%Y%m%d-%H%M%S)"
out="$root/artifacts/macos-run-$stamp"
mkdir -p "$out/sessions" "$out/chunks"
summary="$out/SUMMARY.txt"

say() { echo "$@" | tee -a "$summary"; }

# only what belongs to this checkout: other dotnet processes of the user are none of our business
kill_ours() {
  pkill -9 -f "$root/tests/" 2>/dev/null
  pkill -9 -f "$root/src/DotnetDebugger.Adapter/bin/" 2>/dev/null
  true
}

monitor_pid=""
cleanup() {
  [ -n "$monitor_pid" ] && kill "$monitor_pid" 2>/dev/null
  kill_ours
}
trap cleanup EXIT
trap 'say "interrupted"; exit 130' INT TERM

# ---------------------------------------------------------------- the machine

say "dotnet-debugger on macOS, $stamp"
say "commit: $(git rev-parse --short HEAD 2>/dev/null || echo '?')$(git diff --quiet 2>/dev/null || echo ' (with local changes)')"
{
  echo "=== sw_vers";            sw_vers
  echo "=== uname -a";           uname -a
  echo "=== hardware";           sysctl -n machdep.cpu.brand_string hw.ncpu hw.memsize 2>&1
  echo "=== translated (Rosetta)? 1 = yes"; sysctl -n sysctl.proc_translated 2>&1
  echo "=== csrutil status";     csrutil status 2>&1
  echo "=== DevToolsSecurity";   DevToolsSecurity -status 2>&1
  echo "=== _developer group";   dseditgroup -o checkmember -m "$(whoami)" _developer 2>&1
  echo "=== shell";              echo "$BASH_VERSION"; echo "LANG=${LANG:-} LC_ALL=${LC_ALL:-}"
  echo "=== which dotnet";       which dotnet; ls -l "$(which dotnet)" 2>&1
  echo "=== dotnet --info";      dotnet --info 2>&1
  echo "=== DOTNET_* environment"; env | grep -E '^(DOTNET_|COMPlus_|NUGET_)' | sort
  echo "=== git status";         git status --short 2>&1 | head -50
} > "$out/environment.txt" 2>&1

if ! command -v dotnet > /dev/null; then
  say "FATAL: dotnet is not on the PATH"
  exit 1
fi

if [ "$enable_devtools" = 1 ]; then
  say "enabling developer mode (sudo)..."
  sudo /usr/sbin/DevToolsSecurity -enable 2>&1 | tee -a "$summary"
  sudo dseditgroup -o edit -a "$(whoami)" -t user _developer 2>&1 | tee -a "$summary"
fi
devtools="$(DevToolsSecurity -status 2>&1)"
say "DevToolsSecurity: $devtools"
case "$devtools" in
  *enabled*) ;;
  *) say "WARNING: developer mode is off. macOS then asks for a password whenever a process is debugged, and every"
     say "         session of an apphost program hangs behind that prompt. Re-run with --enable-devtools." ;;
esac

# ---------------------------------------------------------------- build

say "building (Debug)..."
if ! dotnet build -c Debug > "$out/build.log" 2>&1; then
  say "FATAL: the build failed, see build.log"
  tail -30 "$out/build.log"
else
  grep -E "warning|error|Warn|Elapsed" "$out/build.log" | tail -5 | tee -a "$summary"

  # who may be debugged: the entitlements of the host and of the test program's apphost
  {
    for f in "$(which dotnet)" "$root/tests/TestApp/bin/Debug/net8.0/TestApp" "$root/src/DotnetDebugger.Adapter/bin/Debug/net8.0/dotnet-debugger"; do
      echo "=== $f"
      codesign -d -vv --entitlements - --xml "$f" 2>&1 | head -30
      echo
    done
    echo "=== dbgshim next to the adapter"
    find "$root/src/DotnetDebugger.Adapter/bin/Debug" -name "libdbgshim*" -exec ls -l {} \; -exec file {} \; 2>&1
  } > "$out/codesign.txt" 2>&1

  # ---------------------------------------------------------------- tests

  export DOTNET_DEBUGGER_TEST_LOG="$out/sessions/"
  export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_UI_LANGUAGE=en
  [ "$matrix" = 1 ] && export DOTNET_DEBUGGER_MATRIX=1
  [ "$stress" = 1 ] && export DOTNET_DEBUGGER_STRESS=1

  nohup bash "$root/build/resource-monitor.sh" "$out/monitor.log" 5 > /dev/null 2>&1 &
  monitor_pid=$!

  # one chunk: dotnet test with a hard limit, because a hung debug session can outlive every timeout inside the suite
  run_chunk() {
    name="$1"; expr="$2"
    log="$out/chunks/$name.txt"
    started=$(date +%s)
    dotnet test tests/DotnetDebugger.Tests -c Debug --no-build --filter "$expr" \
      --logger "console;verbosity=normal" --logger "trx;LogFileName=$name.trx" --results-directory "$out/chunks" \
      --blame-hang-timeout 180s > "$log" 2>&1 &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
      if [ $(( $(date +%s) - started )) -gt "$chunk_timeout" ]; then
        echo "### killed after ${chunk_timeout}s" >> "$log"
        # what was still alive says where it hung
        ps -A -o pid,ppid,%cpu,rss,etime,command | grep -E "dotnet|TestApp|/bin/sh" | grep -v grep >> "$log"
        kill -9 "$pid" 2>/dev/null
        pkill -9 -f "testhost" 2>/dev/null
        break
      fi
      sleep 2
    done
    wait "$pid" 2>/dev/null
    code=$?
    took=$(( $(date +%s) - started ))
    result="$(grep -E "^(Passed!|Failed!)|Total tests:|^ +(Passed|Failed|Skipped):" "$log" | tr -s ' ' | tr '\n' ' ')"
    [ -z "$result" ] && result="no result line (crashed or killed?)"
    say "$(printf '%-24s exit=%-3s %4ss  %s' "$name" "$code" "$took" "$result")"
    grep -E "^[[:space:]]+Failed " "$log" | sed 's/^ */      /' | tee -a "$summary"
    kill_ours
  }

  if [ -n "$filter" ]; then
    run_chunk "filtered" "$filter"
  else
    # class by class: a class that takes the machine down still leaves the ones before it documented
    for class in DapConnectionTests SessionTests DebuggingTests BreakpointTests SteppingTests InspectionTests \
                 EvaluationTests LaunchTests Wave1Tests Wave2Tests Wave3Tests FindingsTests; do
      run_chunk "$class" "FullyQualifiedName~DotnetDebugger.Tests.$class"
    done
    if [ "$matrix" = 1 ]; then
      run_chunk "RuntimeMatrixTests" "FullyQualifiedName~RuntimeMatrixTests"
    fi
  fi

  kill "$monitor_pid" 2>/dev/null
  monitor_pid=""
fi

# ---------------------------------------------------------------- what else may explain a failure

{
  echo "=== processes of ours still alive at the end"
  ps -A -o pid,ppid,etime,command | grep -E "$root" | grep -v grep
  echo "=== recent crash reports (names only)"
  ls -lt "$HOME/Library/Logs/DiagnosticReports" 2>/dev/null | grep -iE "dotnet|TestApp|testhost" | head -20
} > "$out/leftovers.txt" 2>&1
# crash reports of this run: they say which native frame died
find "$HOME/Library/Logs/DiagnosticReports" -newer "$out/environment.txt" \( -iname "*dotnet*" -o -iname "*TestApp*" -o -iname "*testhost*" \) \
  -exec cp {} "$out/" \; 2>/dev/null

archive="$root/artifacts/macos-run-$stamp.tar.gz"
tar -czf "$archive" -C "$root/artifacts" "macos-run-$stamp"
[ "$keep" = 1 ] || rm -rf "$out"
echo
echo "sessions traced: $(tar -tzf "$archive" | grep -c '/sessions/.*\.log$')"
echo "send this file: $archive ($(du -h "$archive" | cut -f1))"
