<#
.SYNOPSIS
    Publishes the debug adapter for one or more runtime identifiers into artifacts/publish/<rid>.
.EXAMPLE
    ./build/publish.ps1                       # current platform, framework-dependent (needs .NET 8 or newer to run)
    ./build/publish.ps1 -Rid win-x64,win-arm64 -SelfContained   # what is released: one executable, no .NET needed
                                                                 # (Windows: plus the 32-bit adapter in x86/)
#>
param(
    [string[]] $Rid = @(if ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'win-x64' }),
    [switch] $SelfContained,
    [switch] $NoX86,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/DotnetDebugger.Adapter/DotnetDebugger.Adapter.csproj'

foreach ($r in $Rid) {
    $output = Join-Path $root "artifacts/publish/$r"
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    # Self-contained means a single compressed executable (plus dbgshim): about 40 MB instead of 90 MB of loose files.
    # No trimming: the expression interpreter lives on reflection and "dynamic".
    $flavor = if ($SelfContained) {
        @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true')
    } else {
        @('--self-contained', 'false')
    }
    dotnet publish $project -c $Configuration -r $r @flavor -o $output -nologo -v q `
        -p:DebugType=embedded -p:SatelliteResourceLanguages=en
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $r" }

    # ICorDebug debugs processes of its own bitness only: the 64-bit adapter hands 32-bit debuggees to a 32-bit build
    # of itself in "x86" (self-contained, so no x86 .NET runtime is needed; about 38 MB). -NoX86 leaves it out.
    if ($r -in 'win-x64', 'win-arm64' -and -not $NoX86) {
        dotnet publish $project -c $Configuration -r win-x86 --self-contained true -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true -o (Join-Path $output 'x86') -nologo -v q `
            -p:DebugType=embedded -p:SatelliteResourceLanguages=en
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the x86 adapter of $r" }
    }
    Write-Host "Published $r -> $output"
}
