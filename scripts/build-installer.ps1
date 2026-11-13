# Builds the ClipBar installer from an already-published single-file exe.
# Usage:
#   powershell -File scripts\build-installer.ps1 -SourceExe <ClipBar.exe> -OutputDirectory <new-dir> [-Version 1.1.1]
# The exe must be the standard self-contained build: the installer carries one binary, not the lite one.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceExe,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.1',
    [string]$IsccPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NativePath([string]$Value) {
    if ($Value -notmatch '^[A-Za-z]:[\\/]' -or $Value -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'Use an absolute native Windows path without parent traversal.'
    }
    return [IO.Path]::GetFullPath($Value)
}

$root = Resolve-NativePath (Split-Path -Parent $PSScriptRoot)
$source = Resolve-NativePath $SourceExe
$output = Resolve-NativePath $OutputDirectory
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Source exe not found: $source" }
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory already exists; choose a fresh directory.' }
$icon = Join-Path $root 'src\ClipBar.App\Assets\app.ico'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw "Icon not found: $icon" }

if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $IsccPath) {
        $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $IsccPath = $command.Source
    }
    if (-not $IsccPath) { throw 'ISCC.exe (Inno Setup 6) not found. Install it or pass -IsccPath.' }
}

# Fresh output only: ISCC refuses to overwrite an existing installer otherwise.
New-Item -ItemType Directory -Path $output | Out-Null
$arguments = @(
    ('/DSourceExe=' + $source),
    ('/DSourceIcon=' + $icon),
    ('/DMyVersion=' + $Version),
    ('/O' + $output),
    ('/FClipBar-Setup-' + $Version),
    (Join-Path $root 'scripts\installer.iss')
)
Write-Host ('Compiling installer with ' + $IsccPath)
& $IsccPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }

$installer = Join-Path $output ('ClipBar-Setup-' + $Version + '.exe')
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer was not produced.' }
$size = (Get-Item -LiteralPath $installer).Length
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("Installer: $installer ($size bytes, sha256 $hash)")
