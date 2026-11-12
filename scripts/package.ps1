# Build trusted ClipBar source only. No tests, data copy, process termination or release upload.
# All generated state stays under a NEW OutputDirectory inside WorkspaceRoot.
# Optional PackageSource selects an existing LOCAL feed/cache for offline builds.
# Otherwise dotnet uses the caller's normal NuGet configuration.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$WorkspaceRoot,
    [string]$PackageSource,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.0'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NativePath([string]$Value) {
    if ($Value -notmatch '^[A-Za-z]:[\\/]' -or $Value -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'Use an absolute native Windows path without parent traversal.'
    }
    $full = [IO.Path]::GetFullPath($Value)
    if ($full -eq [IO.Path]::GetPathRoot($full) -or $full -match '^[A-Za-z]:[\\/](tmp|temp|e)([\\/]|$)') {
        throw 'A drive root or reserved temporary root is not a legal workspace path.'
    }
    return $full.TrimEnd([char[]]@('\', '/'))
}

function Assert-NoLinks([string]$Path) {
    $current = $Path
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Symlink/reparse point is not allowed: $current"
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Test-Within([string]$Path, [string]$Parent) {
    return ($Path -eq $Parent -or $Path.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase))
}

$ProjectRoot = Get-NativePath $ProjectRoot
$OutputDirectory = Get-NativePath $OutputDirectory
$WorkspaceRoot = Get-NativePath $WorkspaceRoot
if ($PackageSource) {
    $PackageSource = Get-NativePath $PackageSource
    Assert-NoLinks $PackageSource
}
foreach ($path in @($WorkspaceRoot, $ProjectRoot, $OutputDirectory)) {
    Assert-NoLinks $path
}
if (-not (Test-Path -LiteralPath $WorkspaceRoot -PathType Container) -or
    -not (Test-Within $ProjectRoot $WorkspaceRoot) -or
    -not (Test-Within $OutputDirectory $WorkspaceRoot)) {
    throw 'ProjectRoot and OutputDirectory must be inside the existing explicit workspace.'
}
if (Test-Within $OutputDirectory $ProjectRoot) { throw 'Output and project must not overlap.' }
if (Test-Within $ProjectRoot $OutputDirectory) { throw 'Output and project must not overlap.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory already exists; choose a fresh directory.' }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $OutputDirectory) -PathType Container)) {
    throw 'The output parent must already exist.'
}
if ($PackageSource -and -not (Test-Path -LiteralPath $PackageSource -PathType Container)) {
    throw 'PackageSource must be an existing local NuGet package directory.'
}
$project = Join-Path $ProjectRoot 'src\ClipBar.App\ClipBar.App.csproj'
Assert-NoLinks $project
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'ClipBar.App.csproj is missing.' }
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

# Never clear a pre-existing dist or reuse a previous output/staging tree.
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$staging = Join-Path $OutputDirectory '.staging'
$releaseAssets = Join-Path $OutputDirectory 'release-assets'
New-Item -ItemType Directory -Path $staging, $releaseAssets | Out-Null
$environment = @{
    PYTHONUTF8 = '1'; PYTHONIOENCODING = 'utf-8'; PYTHONDONTWRITEBYTECODE = '1'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; DOTNET_NOLOGO = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'; DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
    MSBUILDDISABLENODEREUSE = '1'
}
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
                    'NUGET_SCRATCH', 'NUGET_PLUGINS_CACHE_PATH', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR')) {
    $directory = Join-Path $staging $name.ToLowerInvariant()
    New-Item -ItemType Directory -Path $directory | Out-Null
    $environment[$name] = $directory
}
$previous = @{}
$pushed = $false
try {
    foreach ($name in $environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    Push-Location -LiteralPath $staging
    $pushed = $true
    $sdkVersion = (& $dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to identify the selected .NET SDK.' }
    $assets = [ordered]@{}
    $checksums = New-Object 'System.Collections.Generic.List[string]'
    $modes = @(
        @{ Name = 'standard'; SelfContained = 'true'; Compression = 'true'; Asset = 'ClipBar-win-x64.exe' },
        @{ Name = 'lite'; SelfContained = 'false'; Compression = 'false'; Asset = 'ClipBar-lite-win-x64.exe' }
    )
    foreach ($mode in $modes) {
        $publish = Join-Path $OutputDirectory $mode.Name
        $artifacts = Join-Path $staging ('artifacts-' + $mode.Name)
        $arguments = @(
            'publish', $project, '-c', 'Release', '-r', 'win-x64',
            '--self-contained', $mode.SelfContained, '--artifacts-path', $artifacts,
            '--output', $publish, '--disable-build-servers',
            '-p:UseSharedCompilation=false',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            ('-p:EnableCompressionInSingleFile=' + $mode.Compression),
            '-p:DebugType=None', '-p:DebugSymbols=false', '-p:CopyOutputSymbolsToPublishDirectory=false',
            '-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true',
            '-p:EnableSourceControlManagerQueries=false', '-p:EnableSourceLink=false',
            '-p:IncludeSourceRevisionInInformationalVersion=false',
            ('-p:PathMap=' + $ProjectRoot + '=/_/src'),
            ('-p:Version=' + $Version), ('-p:AssemblyVersion=' + $Version + '.0'),
            ('-p:FileVersion=' + $Version + '.0')
        )
        if ($PackageSource) { $arguments += @('--source', $PackageSource, '-p:NuGetAudit=false') }
        Write-Host ('Publishing ' + $mode.Name + ' with SDK ' + $sdkVersion)
        & $dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw ('dotnet publish failed: ' + $mode.Name) }
        $published = @(Get-ChildItem -LiteralPath $publish -Recurse -Force)
        if ($published.Count -ne 1 -or $published[0].Name -ne 'ClipBar.exe' -or $published[0].PSIsContainer) {
            throw ('Expected only ClipBar.exe (no PDB/data/extra content): ' + $mode.Name)
        }
        Assert-NoLinks $published[0].FullName
        $assetPath = Join-Path $releaseAssets $mode.Asset
        Copy-Item -LiteralPath $published[0].FullName -Destination $assetPath
        $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $assets[$mode.Name] = [ordered]@{
            name = $mode.Asset; bytes = [long](Get-Item -LiteralPath $assetPath).Length; sha256 = $hash
            selfContained = ($mode.SelfContained -eq 'true'); compressed = ($mode.Compression -eq 'true')
        }
        $checksums.Add($hash + '  ' + $mode.Asset)
    }
    $metadata = [ordered]@{
        schemaVersion = 1; version = $Version; runtime = 'win-x64'; sdkVersion = $sdkVersion; assets = $assets
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'),
        ($metadata | ConvertTo-Json -Depth 5) + "`n", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $releaseAssets 'SHA256SUMS.txt'),
        ($checksums -join "`n") + "`n", [Text.Encoding]::ASCII)
    Write-Host ('Release assets: ' + $releaseAssets)
    Write-Host ('Metadata: ' + (Join-Path $OutputDirectory 'manifest.json'))
    Write-Host 'Local .staging is intentionally retained, never a release upload input.'
}
finally {
    if ($pushed) { Pop-Location }
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
