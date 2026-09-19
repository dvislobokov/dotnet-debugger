#!/usr/bin/env bash
# Publishes the debug adapter into artifacts/publish/<rid>.
#   ./build/publish.sh [rid ...]          (default: the current platform)
#   SELF_CONTAINED=true ./build/publish.sh linux-x64 linux-arm64
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/src/DotnetDebugger.Adapter/DotnetDebugger.Adapter.csproj"

if [ $# -eq 0 ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) set -- osx-arm64 ;;
    Darwin-*)     set -- osx-x64 ;;
    Linux-aarch64) set -- linux-arm64 ;;
    *)            set -- linux-x64 ;;
  esac
fi

for rid in "$@"; do
  output="$root/artifacts/publish/$rid"
  rm -rf "$output"
  dotnet publish "$project" -c "${CONFIGURATION:-Release}" -r "$rid" --self-contained "${SELF_CONTAINED:-false}" \
    -o "$output" -nologo -v q -p:DebugType=embedded -p:SatelliteResourceLanguages=en
  echo "Published $rid -> $output"
done
