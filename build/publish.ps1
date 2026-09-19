<#
.SYNOPSIS
    Publishes the debug adapter for one or more runtime identifiers into artifacts/publish/<rid>.
.EXAMPLE
    ./build/publish.ps1                       # current platform, framework-dependent
    ./build/publish.ps1 -Rid win-x64,win-arm64 -SelfContained
#>
param(
    [string[]] $Rid = @(if ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'win-x64' }),
    [switch] $SelfContained,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/DotnetDebugger.Adapter/DotnetDebugger.Adapter.csproj'

foreach ($r in $Rid) {
    $output = Join-Path $root "artifacts/publish/$r"
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
    dotnet publish $project -c $Configuration -r $r --self-contained $selfContainedValue -o $output -nologo -v q `
        -p:DebugType=embedded -p:SatelliteResourceLanguages=en
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $r" }
    Write-Host "Published $r -> $output"
}
