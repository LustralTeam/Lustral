<#
.SYNOPSIS
    Builds a Lustral release: one zip per backend, laid out as a game folder, and release.json describing them.

.DESCRIPTION
    Writes artifacts\release\<version>\:
      lustral-win-x64-mono.zip     winhttp.dll (stage 0) and Lustral\core (Lustral.Host.Mono and its dependencies)
      lustral-win-x64-il2cpp.zip   winhttp.dll, Lustral\core (Lustral.Host), Lustral\dotnet (the .NET runtime) and
                                   Lustral\generator (Lustral.Generator)
      release.json                 the version, the commit, and each zip's backend, size and SHA-256 hash

    Each zip contains Lustral\install.json, which describes the installation for installers such as Lustral Hub:
      files     paths outside Lustral\ installed by the zip, which are removed on uninstall
      replace   folders replaced entirely on install or update, so that no stale files remain
      keep      user and game data that install, update and uninstall leave in place unless requested: settings, logs,
                crash reports, the generated interop assemblies (regenerated when outdated), mods, mod update state
                (Lustral/updates: pending downloads and previous versions for rollback), mod data (Lustral/data) and
                Lustral/cache
    The zip to install depends on the game's backend: IL2CPP if GameAssembly.dll is next to the executable, otherwise Mono.

.EXAMPLE
    ./tools/pack.ps1
#>
[CmdletBinding()]
param(
    [string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
. (Join-Path $PSScriptRoot 'common.ps1')

try {

$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$commit = (git rev-parse HEAD).Trim()
$dirty = [bool](git status --porcelain --untracked-files=no)
if (-not $Output) { $Output = Join-Path $repo "artifacts\release\$version" }
$staging = Join-Path $repo 'artifacts\release\staging'
Remove-IfPresent $staging
Remove-IfPresent $Output
New-Item -ItemType Directory -Force $Output | Out-Null

Write-Host '==> Building stage 0' -ForegroundColor Cyan
& (Join-Path $repo 'stage0\build.ps1')
$proxy = Join-Path $repo 'stage0\build\x64-release\winhttp.dll'

$keep = @('Lustral/loader.toml', 'Lustral/config', 'Lustral/Logs', 'Lustral/Reports', 'Lustral/interop', 'Lustral/updates', 'Lustral/cache', 'Lustral/data', 'Mods')
$packages = foreach ($backend in 'mono', 'il2cpp') {
    $root = Join-Path $staging $backend
    $lustral = Join-Path $root 'Lustral'
    New-Item -ItemType Directory -Force $lustral | Out-Null
    Copy-Item -LiteralPath $proxy -Destination (Join-Path $root 'winhttp.dll')
    # Lustral's license (the LGPL and the GPL it supplements) and the third-party notices for the components it ships.
    foreach ($notice in 'COPYING.LESSER', 'COPYING', 'THIRD-PARTY-NOTICES.md') {
        $path = Join-Path $repo $notice
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $lustral }
        elseif ($notice -ne 'THIRD-PARTY-NOTICES.md') { Write-Warning "No $notice file found; the release does not include Lustral's complete license" }
    }

    $hostProject = if ($backend -eq 'il2cpp') { 'src\Lustral.Host' } else { 'src\Lustral.Host.Mono' }
    Invoke-Checked "Publishing $hostProject" {
        dotnet publish (Join-Path $repo $hostProject) -c Release -o (Join-Path $lustral 'core') -nologo -v quiet
    }
    $replace = @('Lustral/core')
    if ($backend -eq 'il2cpp') {
        Install-DotNetRuntime (Join-Path $lustral 'dotnet')
        Invoke-Checked 'Publishing src\Lustral.Generator' {
            dotnet publish (Join-Path $repo 'src\Lustral.Generator') -c Release -o (Join-Path $lustral 'generator') -nologo -v quiet
        }
        $replace += 'Lustral/dotnet', 'Lustral/generator'
    }

    [ordered]@{
        format   = 1
        product  = 'Lustral'
        version  = $version
        commit   = $commit
        backend  = $backend
        platform = 'win-x64'
        proxy    = 'winhttp.dll'
        files    = @('winhttp.dll')
        replace  = $replace
        keep     = $keep
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $lustral 'install.json') -Encoding utf8

    # Named without the version, so that https://github.com/LustralTeam/Lustral/releases/latest/download/<name> always
    # downloads the latest release (the download buttons in the README and the documentation).
    $zip = Join-Path $Output "lustral-win-x64-$backend.zip"
    Write-Host "==> Zipping $([IO.Path]::GetFileName($zip))" -ForegroundColor Cyan
    # Written entry by entry rather than with Compress-Archive, which in Windows PowerShell 5.1 stores backslashes as path
    # separators. The ZIP format requires forward slashes, and other tools and platforms extract backslashes literally.
    Remove-IfPresent $zip
    $rootPath = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File) {
            $entryName = $file.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
    [ordered]@{
        backend  = $backend
        platform = 'win-x64'
        file     = [IO.Path]::GetFileName($zip)
        size     = (Get-Item -LiteralPath $zip).Length
        sha256   = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

[ordered]@{
    format   = 1
    product  = 'Lustral'
    version  = $version
    commit   = $commit
    dirty    = $dirty
    packages = @($packages)
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'release.json') -Encoding utf8
Remove-IfPresent $staging

Write-Host ''
Write-Host "Lustral $version released to $Output$(if ($dirty) { ' (from uncommitted changes)' })" -ForegroundColor Green
Get-ChildItem -LiteralPath $Output | ForEach-Object { Write-Host ("  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

} finally {
    Pop-Location
}
