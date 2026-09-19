<#
.SYNOPSIS
    Runs the test suite on Linux inside a Docker container (the repository is mounted read-only and copied, so the
    Windows bin/obj directories stay untouched).
.EXAMPLE
    ./build/test-in-docker.ps1                                  # the whole suite
    ./build/test-in-docker.ps1 -Matrix -Stress                  # plus the runtime matrix and the stress tests
    ./build/test-in-docker.ps1 -Filter "FullyQualifiedName~SteppingTests"
#>
param(
    [string] $Filter,
    [switch] $Matrix,
    [switch] $Stress
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$image = 'dotnet-debugger-linux-tests'

docker build -q -t $image -f (Join-Path $PSScriptRoot 'docker/linux-tests.Dockerfile') (Join-Path $PSScriptRoot 'docker') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker build failed' }

# A debugger needs ptrace, which the default container profile forbids.
$arguments = @('run', '--rm', '--cap-add=SYS_PTRACE', '--security-opt', 'seccomp=unconfined', '-v', "${root}:/src:ro")
if ($Matrix) { $arguments += @('-e', 'DOTNET_DEBUGGER_MATRIX=1') }
if ($Stress) { $arguments += @('-e', 'DOTNET_DEBUGGER_STRESS=1') }
$arguments += @($image, 'bash', '/src/build/test-in-docker.sh')
if ($Filter) { $arguments += @('--filter', $Filter) }
elseif (-not $Matrix) { $arguments += @('--filter', 'FullyQualifiedName!~RuntimeMatrixTests') }

docker @arguments
exit $LASTEXITCODE
