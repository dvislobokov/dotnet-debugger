#!/usr/bin/env bash
# Appends a snapshot of the machine to a log every few seconds: load, memory, how many processes of ours exist and who
# burns the CPU. For CI machines that die without leaving anything else behind (see macos-diagnostics.yml).
#   usage: resource-monitor.sh <log file> [seconds]
log="$1"
interval="${2:-5}"
while true; do
  {
    date "+%H:%M:%S"
    uptime | sed 's/.*load/load/'
    if command -v vm_stat > /dev/null; then
      vm_stat | grep -E "free|active|wired" | tr '\n' ' '
      echo
    else
      free -m | sed -n 2p
    fi
    echo "processes: dotnet=$(pgrep -f dotnet | wc -l) TestApp=$(pgrep -f TestApp | wc -l) sh=$(pgrep -x sh | wc -l) all=$(ps -A | wc -l)"
    if [ "$(uname -s)" = "Darwin" ]; then
      ps -A -o pid,ppid,%cpu,rss,etime,command -r | head -8 | cut -c1-200
    else
      ps -A -o pid,ppid,%cpu,rss,etime,args --sort=-%cpu | head -8 | cut -c1-200
    fi
    echo
  } >> "$log" 2>&1
  sleep "$interval"
done
