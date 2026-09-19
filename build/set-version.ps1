<#
.SYNOPSIS
    Sets the one version shared by the adapter, the .NET tool and the VS Code extension.
.EXAMPLE
    ./build/set-version.ps1 -Version 0.2.0
#>
param([Parameter(Mandatory)] [string] $Version)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$Version' is not a semantic version." }
$root = Split-Path $PSScriptRoot -Parent

# .NET file APIs instead of Get/Set-Content: Windows PowerShell 5 would re-encode the files
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Update-File([string] $path, [string] $pattern, [string] $replacement) {
    $text = [System.IO.File]::ReadAllText($path)
    [System.IO.File]::WriteAllText($path, [regex]::Replace($text, $pattern, $replacement), $utf8)
}

Update-File (Join-Path $root 'Directory.Build.props') '<Version>[^<]*</Version>' "<Version>$Version</Version>"

# The Marketplace only accepts major.minor.patch; pre-releases are marked when publishing instead.
$extensionVersion = ($Version -split '-')[0]
Update-File (Join-Path $root 'vscode/package.json') '("version":\s*")[^"]*(")' "`${1}$extensionVersion`${2}"

Write-Host "Version set to $Version (extension: $extensionVersion)"
