<#
.SYNOPSIS
    Downloads the Unity test players for the end-to-end tests to .cache\testplayers\<unity version>\<platform>.

.DESCRIPTION
    The test players are published as releases of LustralTeam/Lustral.TestPlayers, one per Unity version and build date
    (see its README); tests/testplayers.json lists the releases that Lustral tests against. For each release, downloads
    players.json and each platform's zip, verifies each zip's SHA-256 hash and extracts it. A Unity version whose folder
    already holds the listed release is not downloaded again; otherwise its folder is replaced. Without -UnityVersion,
    folders of Unity versions that are no longer listed are removed. Building the players requires LustralTeam's Unity
    project, which is not public.

.EXAMPLE
    ./tools/get-testplayers.ps1
    ./tools/get-testplayers.ps1 -UnityVersion 6000.6.5f1
    ./tools/get-testplayers.ps1 -Source ../Lustral.TestPlayerProject/artifacts    # releases packaged locally, one folder each
#>
[CmdletBinding()]
param(
    [string[]]$UnityVersion,
    [string]$Repository,
    [string]$Source,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # The Windows PowerShell progress bar slows down large downloads considerably.
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pinned = Get-Content -Raw -LiteralPath (Join-Path $repo 'tests\testplayers.json') | ConvertFrom-Json
if (-not $Repository) { $Repository = $pinned.repository }
$players = Join-Path $repo '.cache\testplayers'
New-Item -ItemType Directory -Force $players | Out-Null

# A release is named <unity version>-<build date>.
$releases = @($pinned.releases | ForEach-Object { [pscustomobject]@{ Name = $_; UnityVersion = $_.Split('-')[0] } })
if (-not $UnityVersion) {
    Get-ChildItem -LiteralPath $players -Directory | Where-Object { $_.Name -notin $releases.UnityVersion } | ForEach-Object {
        Write-Host "Removing the test players of Unity $($_.Name), which tests/testplayers.json no longer lists"
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}
$selected = @($releases | Where-Object { -not $UnityVersion -or $_.UnityVersion -in $UnityVersion })
if ($UnityVersion) {
    $missing = @($UnityVersion | Where-Object { $_ -notin $releases.UnityVersion })
    if ($missing) { throw "tests/testplayers.json lists no release for Unity $($missing -join ', ')" }
}

function Get-File([string]$release, [string]$name, [string]$destination) {
    if ($Source) { Copy-Item -LiteralPath (Join-Path $Source "$release\$name") -Destination $destination }
    else { Invoke-WebRequest -Uri "https://github.com/$Repository/releases/download/$release/$name" -OutFile $destination -UseBasicParsing }
}

$download = Join-Path $repo '.cache\testplayers-download'
foreach ($release in $selected) {
    $versionDir = Join-Path $players $release.UnityVersion
    $marker = Join-Path $versionDir 'release.txt'
    if (-not $Force -and -not $Source -and (Test-Path -LiteralPath $marker) -and (Get-Content -Raw -LiteralPath $marker).Trim() -eq "$Repository $($release.Name)") {
        Write-Host "Unity $($release.UnityVersion): release $($release.Name) is already in $versionDir"
        continue
    }
    if (Test-Path -LiteralPath $versionDir) { Remove-Item -LiteralPath $versionDir -Recurse -Force }
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Recurse -Force }
    New-Item -ItemType Directory -Force $versionDir, $download | Out-Null
    $manifestPath = Join-Path $download 'players.json'
    Get-File $release.Name 'players.json' $manifestPath
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    foreach ($player in $manifest.players) {
        $zip = Join-Path $download $player.file
        Write-Host "==> $($release.Name)/$($player.file) ($([math]::Round($player.size / 1MB, 1)) MB)" -ForegroundColor Cyan
        Get-File $release.Name $player.file $zip
        $sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha256 -ne $player.sha256) { throw "SHA-256 mismatch for $($release.Name)/$($player.file): $sha256, expected $($player.sha256)" }
        $destination = Join-Path $versionDir $player.platform
        New-Item -ItemType Directory -Force $destination | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $destination)
    }
    Remove-Item -LiteralPath $download -Recurse -Force
    if (-not $Source) { Set-Content -LiteralPath $marker -Value "$Repository $($release.Name)" -Encoding ascii }
    Write-Host "Unity $($release.UnityVersion): $(@($manifest.players).Count) test players of release $($release.Name)" -ForegroundColor Green
}
