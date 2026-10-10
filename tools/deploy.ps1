<#
.SYNOPSIS
    Development deployment: installs Lustral and the game's sample mod into a Unity game.

.DESCRIPTION
    Detects the backend from the game folder (IL2CPP if GameAssembly.dll exists, otherwise Mono) and writes:
      winhttp.dll                        the loader proxy: Lustral's stage 0 (-Loader lustral) or UnityDoorstop 4.5
      doorstop_config.ini                Doorstop only
      Lustral\core\                      IL2CPP: Lustral.Host and its dependencies (framework-dependent)
                                         Mono:   Lustral.Host.Mono and its dependencies
      Lustral\dotnet\                    IL2CPP only: the .NET runtime, laid out as a .NET installation
      Lustral\generator\                 IL2CPP only: Lustral.Generator, which runs at game startup when the interop
                                         assemblies are missing or outdated
      Lustral\interop\                   IL2CPP only: Il2CppInterop V2 assemblies
      Lustral\Logs\                      Lustral's logs
      Mods\<sample id>\                  the game's sample mod, if it has one
    All paths outside Lustral\ are recorded in Lustral\installed.txt; -Uninstall removes exactly those paths and Lustral\.
    Doorstop's verbose build also writes doorstop_*.log files to the game folder, which -Uninstall removes as well.

.EXAMPLE
    ./tools/deploy.ps1                      # BOMBANANA! (IL2CPP)
    ./tools/deploy.ps1 -Game plagueinc      # Plague Inc (Mono)
    ./tools/deploy.ps1 -Game bendy          # Bendy and the Ink Machine (IL2CPP, Unity 2022.3)
    ./tools/deploy.ps1 -Game btd6           # Bloons TD 6 (IL2CPP, Unity 6000.3)
    ./tools/deploy.ps1 -Game trds           # Totally Reliable Delivery Service (Mono, Epic Games Launcher)
    ./tools/deploy.ps1 -Loader doorstop     # UnityDoorstop 4.5 instead of Lustral's stage 0
    ./tools/deploy.ps1 -NoInterop           # defers interop generation to the next game start
    ./tools/deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\SomeGame" -NoSample
    ./tools/deploy.ps1 -Game plagueinc -Uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('bombanana', 'plagueinc', 'lethalcompany', 'peak', 'bendy', 'btd6', 'trds')]
    [string]$Game = 'bombanana',
    [string]$GameDir,
    [switch]$NoSample,
    # IL2CPP: skips running the Generator; the game runs it at startup, with its progress window, when the cache is outdated.
    [switch]$NoInterop,
    # Lustral's stage 0 (the default). Doorstop remains available as a fallback.
    [ValidateSet('doorstop', 'lustral')]
    [string]$Loader = 'lustral',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# dotnet selects the SDK from global.json in the working directory rather than the project's, so commands run from the
# repository root.
Push-Location $repo
. (Join-Path $PSScriptRoot 'common.ps1')

# Test games, from Steam or the Epic Games Launcher. Games with anti-cheat are excluded, and samples must not affect
# online play (Bloons TD 6 and Totally Reliable Delivery Service: menus and single player only).
$games = @{
    bombanana     = @{ Folder = 'BOMBANANA!';     Sample = 'samples\Lustral.Samples.Bombanana' }
    plagueinc     = @{ Folder = 'PlagueInc';      Sample = 'samples\Lustral.Samples.PlagueInc' }
    lethalcompany = @{ Folder = 'Lethal Company'; Sample = 'samples\Lustral.Samples.LethalCompany' }
    peak          = @{ Folder = 'PEAK';           Sample = $null }
    bendy         = @{ Folder = 'Bendy and the Ink Machine'; Sample = 'samples\Lustral.Samples.Bendy' }
    btd6          = @{ Folder = 'BloonsTD6';      Sample = 'samples\Lustral.Samples.Btd6' }
    trds          = @{ Folder = 'TRDeliveryService'; Sample = $null; Store = 'Epic' }
}
$steamCommon = if ($env:LUSTRAL_STEAM_COMMON) { $env:LUSTRAL_STEAM_COMMON } else { 'C:\Program Files (x86)\Steam\steamapps\common' }
$epicGames = if ($env:LUSTRAL_EPIC_GAMES) { $env:LUSTRAL_EPIC_GAMES } else { 'C:\Program Files\Epic Games' }
if (-not $GameDir) { $GameDir = Join-Path $(if ($games[$Game].Store -eq 'Epic') { $epicGames } else { $steamCommon }) $games[$Game].Folder }
$sample = if ($NoSample -or $PSBoundParameters.ContainsKey('GameDir')) { $null } else { $games[$Game].Sample }

# Doorstop's verbose build logs its startup, which helps diagnose runtime initialization.
$doorstopVersion = '4.5.0'
$doorstopAsset = "doorstop_win_verbose_$doorstopVersion.zip"
$doorstopSha256 = '309ab07c1bcbf172424edec56aa3133a07746a78bf48aef57471f1023df4e4de'
$doorstopUrl = "https://github.com/NeighTools/UnityDoorstop/releases/download/v$doorstopVersion/$doorstopAsset"


# Identifies a winhttp.dll that this script may replace or remove: Lustral's stage 0 (ProductName "Lustral") or
# UnityDoorstop 4.5 (CompanyName "Doorstop", ProductName "NeighTools"). Any other file is left untouched.
function Test-KnownProxy([string]$path) {
    $info = (Get-Item -LiteralPath $path).VersionInfo
    return "$($info.CompanyName) $($info.ProductName)" -match 'Lustral|Doorstop|NeighTools'
}

function Get-ModId([string]$project) {
    $csproj = Get-ChildItem -LiteralPath (Join-Path $repo $project) -Filter '*.csproj' | Select-Object -First 1
    $match = Select-String -LiteralPath $csproj.FullName -Pattern '<ModId>([^<]+)</ModId>'
    if (-not $match) { throw "$($csproj.Name) has no <ModId>" }
    return $match.Matches[0].Groups[1].Value
}

function Install-Doorstop([string]$proxy, [bool]$il2cpp) {
    $cache = Join-Path $repo ".cache\doorstop\$doorstopVersion"
    $zip = Join-Path $cache $doorstopAsset
    if (-not (Test-Path -LiteralPath $zip)) {
        Write-Host "==> Downloading $doorstopAsset" -ForegroundColor Cyan
        New-Item -ItemType Directory -Force $cache | Out-Null
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $doorstopUrl -OutFile $zip -UseBasicParsing
    }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $doorstopSha256) {
        Remove-Item -LiteralPath $zip
        throw "SHA-256 mismatch for $doorstopAsset ($hash, expected $doorstopSha256). The file was deleted; run the script again to download it."
    }
    $extracted = Join-Path $cache 'extracted'
    if (-not (Test-Path -LiteralPath $extracted)) { Expand-Archive -LiteralPath $zip -DestinationPath $extracted }
    Copy-Item -LiteralPath (Join-Path $extracted 'x64\winhttp.dll') -Destination $proxy -Force

    # Doorstop resolves these paths against the working directory, which Steam sets to the game folder.
    $hostAssembly = if ($il2cpp) { 'Lustral.Host.dll' } else { 'Lustral.Host.Mono.dll' }
    $framework = "Lustral\dotnet\shared\Microsoft.NETCore.App\$dotnetVersion"
    $coreclrPath = if ($il2cpp) { "$framework\coreclr.dll" } else { '' }
    $corlibDir = if ($il2cpp) { $framework } else { '' }
    $config = @"
# Written by Lustral's tools/deploy.ps1 (-Loader doorstop).
[General]
enabled=true
target_assembly=Lustral\core\$hostAssembly
redirect_output_log=false
boot_config_override=
ignore_disable_switch=false

[UnityMono]
dll_search_path_override=
debug_enabled=false
debug_address=127.0.0.1:10000
debug_suspend=false

[Il2Cpp]
coreclr_path=$coreclrPath
corlib_dir=$corlibDir
"@
    [IO.File]::WriteAllText((Join-Path (Split-Path $proxy) 'doorstop_config.ini'), $config.Replace("`r`n", "`n"))
    Write-Host "==> Installed UnityDoorstop $doorstopVersion as winhttp.dll" -ForegroundColor Cyan
}

function Install-Stage0([string]$proxy) {
    Write-Host '==> Building stage 0' -ForegroundColor Cyan
    & (Join-Path $repo 'stage0\build.ps1')
    Copy-Item -LiteralPath (Join-Path $repo 'stage0\build\x64-release\winhttp.dll') -Destination $proxy -Force
    Remove-IfPresent (Join-Path (Split-Path $proxy) 'doorstop_config.ini') # Left over from a Doorstop installation.
    Write-Host '==> Installed Lustral stage 0 as winhttp.dll' -ForegroundColor Cyan
}

try {

if (-not (Test-Path -LiteralPath $GameDir)) { throw "Game folder not found: $GameDir" }
$il2cpp = Test-Path -LiteralPath (Join-Path $GameDir 'GameAssembly.dll')
$backend = if ($il2cpp) { 'IL2CPP' } else { 'Mono' }
if (-not $il2cpp -and -not (Get-ChildItem -LiteralPath $GameDir -Directory -Filter '*_Data' | Where-Object { Test-Path (Join-Path $_.FullName 'Managed\Assembly-CSharp.dll') })) {
    throw "$GameDir is not a recognized Unity game: it contains neither GameAssembly.dll nor *_Data\Managed\Assembly-CSharp.dll"
}

$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($GameDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "Close the game first. Running from ${GameDir}: $(($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ')"
}

$proxy = Join-Path $GameDir 'winhttp.dll'
$lustral = Join-Path $GameDir 'Lustral'
$record = Join-Path $lustral 'installed.txt'
if ((Test-Path -LiteralPath $proxy) -and -not (Test-KnownProxy $proxy)) {
    throw "$proxy is neither Lustral's stage 0 nor UnityDoorstop; another loader may be installed. No changes were made."
}

if ($Uninstall) {
    $paths = if (Test-Path -LiteralPath $record) { Get-Content -LiteralPath $record } else { @('winhttp.dll', 'doorstop_config.ini') }
    foreach ($relative in $paths) { if ($relative) { Remove-IfPresent (Join-Path $GameDir $relative) } }
    Remove-IfPresent $lustral
    $mods = Join-Path $GameDir 'Mods'
    if ((Test-Path -LiteralPath $mods) -and -not (Get-ChildItem -LiteralPath $mods -Force)) { Remove-IfPresent $mods }
    Get-ChildItem -LiteralPath $GameDir -Filter 'doorstop_*.log' | ForEach-Object { Remove-IfPresent $_.FullName }
    Write-Host "Lustral uninstalled from $GameDir." -ForegroundColor Green
    return
}

Write-Host "Deploying Lustral ($backend, loader: $Loader) to $GameDir" -ForegroundColor Green
$installed = [System.Collections.Generic.List[string]]::new()

# The BOMBANANA! sample's former ID, from before installations were recorded.
Remove-IfPresent (Join-Path $GameDir 'Mods\lustral.samples.hello')

# 1. The loader proxy, installed as winhttp.dll.
if ($Loader -eq 'lustral') {
    Install-Stage0 $proxy
    $installed.Add('winhttp.dll')
} else {
    Install-Doorstop $proxy $il2cpp
    $installed.Add('winhttp.dll')
    $installed.Add('doorstop_config.ini')
}

# 2. Lustral's assemblies. The folder is cleared first so that no stale assemblies remain in the runtime's probing path.
$core = Join-Path $lustral 'core'
Remove-IfPresent $core
$hostProject = if ($il2cpp) { 'src\Lustral.Host' } else { 'src\Lustral.Host.Mono' }
Invoke-Checked "Publishing $hostProject" {
    dotnet publish (Join-Path $repo $hostProject) -c $Configuration -o $core -nologo -v quiet
}

if ($il2cpp) {
    # 3. The .NET runtime and the Generator, which runs on it.
    Install-DotNetRuntime (Join-Path $lustral 'dotnet')
    $generator = Join-Path $lustral 'generator'
    Remove-IfPresent $generator
    Invoke-Checked 'Publishing src\Lustral.Generator' {
        dotnet publish (Join-Path $repo 'src\Lustral.Generator') -c $Configuration -o $generator -nologo -v quiet
    }

    # 4. Interop assemblies, generated with the installed Generator and runtime. Generation is skipped when the cache is
    #    up to date.
    if (-not $NoInterop) {
        Invoke-Checked 'Checking interop assemblies' { & (Join-Path $generator 'Lustral.Generator.exe') $GameDir }
    }
}

# 5. The sample mod. Its build generates mod.toml and copies the mod to Mods\<id>\. IL2CPP samples compile against the
#    interop assemblies, so with -NoInterop and no existing cache, the sample is deployed only after the next game start.
if ($sample -and $il2cpp -and -not (Test-Path -LiteralPath (Join-Path $lustral 'interop\interop.json'))) {
    Write-Host "==> Skipping $sample`: it requires the interop assemblies, which are generated on the next game start. Deploy again afterwards." -ForegroundColor Yellow
    if (Test-Path -LiteralPath (Join-Path $GameDir "Mods\$(Get-ModId $sample)")) { $installed.Add("Mods\$(Get-ModId $sample)") }
    $sample = $null
}
if ($sample) {
    Invoke-Checked "Building $sample" {
        dotnet build (Join-Path $repo $sample) -c $Configuration -nologo -v quiet "-p:LustralGameDir=$GameDir"
    }
    $installed.Add("Mods\$(Get-ModId $sample)")
}

[IO.File]::WriteAllLines($record, $installed)

Write-Host ''
Write-Host "Lustral ($backend, loader: $Loader) deployed to $GameDir" -ForegroundColor Green
Write-Host "Launch the game through Steam, then read $(Join-Path $lustral 'Logs\latest.log')"

} finally {
    Pop-Location
}
