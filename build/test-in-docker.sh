#!/usr/bin/env bash
# Runs inside the container: copies the (read-only mounted) repository, builds and tests it.
#   usage: test-in-docker.sh [dotnet test arguments...]
set -uo pipefail
mkdir -p /work/repo
tar -C /src --exclude=bin --exclude=obj --exclude=node_modules --exclude=artifacts --exclude=.vscode-test --exclude=.git -cf - . | tar -C /work/repo -xf -
cd /work/repo
dotnet build -c Debug -v q 2>&1 | grep -E "error|Warn|Elapsed|Build succeeded" | tail -5
dotnet test tests/DotnetDebugger.Tests -c Debug --no-build "$@"
