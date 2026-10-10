<#
.SYNOPSIS
    End-to-end tests: runs Lustral and a test mod in Lustral's Unity test players, on both backends and each Unity version.

.DESCRIPTION
    Deploys Lustral and tests/Lustral.TestPlayer.Mod to each test player in -Players, and runs scenarios through the
    player's test driver:
      quit             loading, patching, scenes, settings, Update and shutdown; the session marker is removed afterwards
      crash            a native crash (Unity's ForceCrash), followed by a normal start that writes the crash report
      throw-on-thread  IL2CPP only: an unhandled exception on a mod's thread is reported immediately
      updates          a mod with an update source (a feed on localhost): a newer version is downloaded and installed on the
                       next start, and a broken version is rolled back and not downloaded again
    Scenarios that depend on Unity's Player.log rotation (crash) run windowed for a few seconds; the others run with
    -batchmode -nographics. Tests run only against the test players, because commercial games may upload crash reports
    to their developers.
    The test players are published as releases of LustralTeam/Lustral.TestPlayers, one per Unity version, whose README
    describes them and the Unity versions they cover. -Players is a folder laid out as <unity version>\<platform>
    (.cache\testplayers by default, where tools/get-testplayers.ps1 downloads the releases that tests/testplayers.json
    lists). A win-x64-mono-legacy player, built with Unity's legacy .NET 3.5 runtime, checks that Lustral reports that
    runtime as unsupported and the game still runs.

.EXAMPLE
    ./tools/get-testplayers.ps1; ./tools/e2e.ps1
    ./tools/e2e.ps1 -Backend mono -Scenario quit
    ./tools/e2e.ps1 -UnityVersion 2022.3.62f2
    ./tools/e2e.ps1 -Players ../Lustral.TestPlayerProject/.cache/build    # players built locally, before publishing
#>
[CmdletBinding()]
param(
    [ValidateSet('mono', 'il2cpp', 'all')]
    [string]$Backend = 'all',
    [ValidateSet('quit', 'crash', 'throw-on-thread', 'updates', 'all')]
    [string]$Scenario = 'all',
    [string[]]$UnityVersion,
    [string]$Players,
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
$failures = [System.Collections.Generic.List[string]]::new()
$windowed = @('-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360')
# Unity versions whose PlayerSettings layout is not yet in src/Lustral.Core/GameData/PlayerSettingsLayouts.txt (AssetRipper's
# type tree dumps had no 6000.6 release on 2026-10-10). Lustral cannot read the game's version from its files there, so on
# the first start, Mono mods with version requirements load with a warning, and the version reported by the game is used
# from then on. Remove a version when the table is regenerated with it.
$beyondLayoutTable = @('6000.6.5f1')

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  ok    $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name $detail" -ForegroundColor Red; $failures.Add("$script:current - $name") }
}

function Start-Player([string]$dir, [string[]]$arguments, [int]$timeoutSeconds = 60) {
    $p = Start-Process (Join-Path $dir 'LustralTestPlayer.exe') -ArgumentList $arguments -PassThru
    if (-not $p.WaitForExit($timeoutSeconds * 1000)) { $p.Kill(); $p.WaitForExit(); return -1 }
    Start-Sleep -Milliseconds 500 # Allows Unity's crash handler to finish writing.
    return $p.ExitCode
}

# Serves a folder at http://localhost:<port>/ from a background job, as the feed for the updates scenario. The job's
# process is killed to stop it, because a pipeline blocked in HttpListener.GetContext ignores Stop-Job.
function Start-FileServer([string]$root, [int]$port) {
    $job = Start-Job -ArgumentList $root, $port -ScriptBlock {
        param($root, $port)
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add("http://localhost:$port/")
        $listener.Start()
        $PID
        while ($listener.IsListening) {
            $context = $listener.GetContext()
            $path = Join-Path $root ($context.Request.Url.AbsolutePath.TrimStart('/') -replace '/', '\')
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $bytes = [IO.File]::ReadAllBytes($path)
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            } else {
                $context.Response.StatusCode = 404
            }
            $context.Response.Close()
        }
    }
    $deadline = (Get-Date).AddSeconds(30)
    $serverPid = $null
    while (-not $serverPid -and (Get-Date) -lt $deadline -and $job.State -eq 'Running') {
        Start-Sleep -Milliseconds 100
        $serverPid = Receive-Job $job | Select-Object -First 1
    }
    if (-not $serverPid) { Remove-Job $job -Force; throw "The feed server on port $port did not start" }
    [pscustomobject]@{ Job = $job; ProcessId = [int]$serverPid }
}

function Stop-FileServer($server) {
    Stop-Process -Id $server.ProcessId -Force -ErrorAction SilentlyContinue
    # In PowerShell 7, removing a job whose process was killed throws "The pipe is being closed", which -ErrorAction does
    # not suppress.
    try { Remove-Job $server.Job -Force -ErrorAction Stop } catch { }
}

function Read-Log([string]$dir) { Get-Content -LiteralPath (Join-Path $dir 'Lustral\Logs\latest.log') -Raw }

function Markers([string]$dir) { @(Get-ChildItem -LiteralPath (Join-Path $dir 'Lustral\Logs') -Filter 'session-*.txt') }

if (-not $Players) { $Players = Join-Path $repo '.cache\testplayers' }
if (-not (Test-Path -LiteralPath $Players)) { throw "No test players in $Players. Download them with tools/get-testplayers.ps1." }
$Players = (Resolve-Path -LiteralPath $Players).Path
$runs = [System.Collections.Generic.List[object]]::new()
$versionDirs = Get-ChildItem -LiteralPath $Players -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+[abfp]\d+$' } |
    Sort-Object { [version]($_.Name -replace '[abfp]\d+$', '') } -Descending
foreach ($versionDir in $versionDirs) {
    if ($UnityVersion -and $versionDir.Name -notin $UnityVersion) { continue }
    foreach ($b in $(if ($Backend -eq 'all') { @('mono', 'il2cpp') } else { @($Backend) })) {
        $dir = Join-Path $versionDir.FullName "win-x64-$b"
        # The player and the asset bundle that its build produces for the test mod.
        if ((Test-Path -LiteralPath (Join-Path $dir 'LustralTestPlayer.exe')) -and (Test-Path -LiteralPath (Join-Path $dir 'e2e\lustral-e2e.bundle'))) {
            $runs.Add([pscustomobject]@{ Version = $versionDir.Name; Backend = $b; Dir = $dir })
        }
    }
}
if ($runs.Count -eq 0) { throw "No test players in $Players match the options. Download them with tools/get-testplayers.ps1." }

try {
    foreach ($run in $runs) {
        $b = $run.Backend; $dir = $run.Dir
        if (-not $NoDeploy) {
            Write-Host "==> Deploying Lustral and the test mod to the Unity $($run.Version) $b test player" -ForegroundColor Cyan
            & (Join-Path $PSScriptRoot 'deploy.ps1') -GameDir $dir | Out-Null
            # The test mod and a dependency chain: a library mod, a mod that depends on it, and a mod that depends on both.
            foreach ($project in 'Lustral.TestPlayer.Mod', 'Lustral.TestPlayer.Chain\First', 'Lustral.TestPlayer.Chain\Second') {
                dotnet build (Join-Path $repo "tests\$project") -nologo -v quiet "-p:LustralGameDir=$dir"
                if ($LASTEXITCODE -ne 0) { throw "Building $project for $b failed" }
            }
        }
        Get-ChildItem -LiteralPath (Join-Path $dir 'Lustral\Logs') -Filter 'session-*.txt' -ErrorAction SilentlyContinue | Remove-Item
        Remove-Item -LiteralPath (Join-Path $dir 'Lustral\Reports') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $dir 'Lustral\config') -Recurse -Force -ErrorAction SilentlyContinue

        if ($Scenario -in 'quit', 'all') {
            $script:current = "$($run.Version) $b quit"
            Write-Host "==> $script:current" -ForegroundColor Cyan
            # A mod for another game, which must not load. Its DLL does not exist, because the game check runs first.
            $otherGame = Join-Path $dir 'Mods\lustral.tests.othergame'
            New-Item -ItemType Directory -Force $otherGame | Out-Null
            Set-Content -LiteralPath (Join-Path $otherGame 'mod.toml') -Encoding utf8 -Value @(
                'id = "lustral.tests.othergame"', 'version = "1.0.0"', 'assembly = "Missing.dll"', '', '[requires]', 'games = [{ name = "Some Other Game" }]')
            $code = Start-Player $dir (@('-batchmode', '-nographics', '-lustral-test', 'quit'))
            Remove-Item -LiteralPath $otherGame -Recurse -Force
            $log = Read-Log $dir
            Check 'exits normally' ($code -eq 0) "(exit $code)"
            Check 'mod loads with its settings and patches' ($log -match 'e2e: loaded, greeting hello, 2 patched method\(s\)')
            Check 'a patch on game code runs' ($log -match 'e2e: TestDriver.Awake patched')
            Check 'game ready' ($log -match 'e2e: game ready')
            Check 'the game named from its files' ($log -match "Game: Lustral Test Player .*by Lustral, Unity $([regex]::Escape($run.Version))")
            Check "mods see the game and its version" ($log -match "e2e: game Lustral Test Player by Lustral, version 1\.0, Unity $([regex]::Escape($run.Version)), $(if ($b -eq 'il2cpp') { 'Il2Cpp' } else { 'Mono' })")
            Check 'the version read from the files is right' (-not ($log -match 'The game reports version'))
            Check 'a mod for another game stays out' ($log -match 'Not loading lustral\.tests\.othergame: is for Some Other Game; the running game is Lustral Test Player')
            Check 'a library mod loads' ($log -match 'Loaded library Chain test library 1\.2\.0 \(lustral\.tests\.chainlibrary\)')
            Check 'dependents load in order and share the library' ($log -match 'e2e chain: first loaded' -and
                $log -match 'e2e chain: second loaded after \[lustral\.tests\.chainfirst\], library 1\.2\.0\.0')
            if ($b -eq 'il2cpp') {
                Check 'interop types nested in generic instances work' ($log -match 'e2e: IL2CPP list enumerated, total length 6')
                # With unstripped interop (when Unity libraries are published for this version), Unity's code is executed.
                $interop = Get-Content -Raw -LiteralPath (Join-Path $dir 'Lustral\interop\interop.json') | ConvertFrom-Json
                if ($interop.Unstripped) {
                    Check 'unstripped Unity code runs (Color.Lerp)' ($log -match 'e2e: unstripped: Color\.Lerp grayscale 0\.50')
                } else {
                    Check "not unstripped ($($interop.UnstripProblem)); Color.Lerp stays left out" ($log -match "e2e: unstripped: Color\.Lerp isn't there")
                }
                # The mod's injected MonoBehaviour ([InjectedType]): Unity messages, SendMessage with an argument, and field state.
                if ($log -match 'e2e: injected component: (\d+) updates, messages (.*)') {
                    $componentUpdates = [int]$Matches[1]; $messages = $Matches[2].Trim()
                    Check "a mod's own MonoBehaviour gets Awake, Start, Update and SendMessage ($componentUpdates updates; $messages)" (
                        $componentUpdates -gt 0 -and $messages -match 'hello' -and $messages -match 'started')
                    # It implements IPointerClickHandler: the player finds it with GetComponent<IPointerClickHandler> and calls
                    # it (click 1), then clicks it through Unity UI's ExecuteEvents (click 2).
                    Check "the game calls a mod's IL2CPP interface implementation (UI clicks)" ($messages -match 'click 1' -and $messages -match 'click 2')
                } else { Check "a mod's own MonoBehaviour gets Unity's messages" $false }
            } else {
                Check "a mod's MonoBehaviour receives UI clicks" ($log -match 'e2e: click target: messages click 1, click 2')
            }
            Check 'scene events with names' ($log -match 'e2e: scene loaded Main \(0\)' -and $log -match 'e2e: scene loaded Second \(1\)')
            $expectedSteps = 'frame, frames, fixed, late, seconds, nested' + $(if ($b -eq 'mono') { ', unity' } else { '' })
            if ($log -match 'e2e: coroutine ran (.*) over (\d+) updates') {
                $steps = $Matches[1].Trim(); $coroutineUpdates = [int]$Matches[2]
                Check "a coroutine goes through every kind of wait ($steps over $coroutineUpdates updates)" ($steps -eq $expectedSteps -and $coroutineUpdates -ge 6)
            } else { Check 'a coroutine goes through every kind of wait' $false }
            # The asset bundle produced by the player's build, shipped in the mod's folder. IL2CPP players do not include
            # Unity's AssetBundle class.
            Check 'asset bundles wait for the game to be ready' ($log -match 'e2e: asset bundles wait for the game to be ready')
            Check 'a mod loads assets from its asset bundle' ($log -match ('e2e: asset bundle: 4 assets \(assets/lustral\.testbundle/atlas\.asset, .*\), ' +
                'contains note True, text note, prefab Lustral Cube, sprites left\+right, meshes 1, missing asset refused, loaded once, ' +
                'unloaded, refused after unloading, from bytes note'))
            Check 'settings file written' (Test-Path -LiteralPath (Join-Path $dir 'Lustral\config\lustral.tests.player.toml'))
            Check 'shutdown runs' ($log -match 'e2e: shutdown after (\d+) updates and (\d+) driver frames')
            $updates = [int]$Matches[1]; $frames = [int]$Matches[2]
            $playerLoop = $log -notmatch "per-frame Update follows rendering"
            if ($playerLoop) {
                # Batch mode does not render, so only the player loop drives Update. Stripped IL2CPP builds may lack its API.
                Check "Update once per frame ($updates updates, $frames frames)" ($updates -gt 0 -and [Math]::Abs($frames - $updates) -le 3)
            }
            if ($log -match 'e2e: (\d+) late updates \((\d+) after an Update\), (\d+) fixed updates') {
                # Driven by the player loop, or by a Lustral component where Lustral cannot use the player loop.
                $late = [int]$Matches[1]; $lateAfter = [int]$Matches[2]; $fixed = [int]$Matches[3]
                Check "LateUpdate once per frame ($late late updates, $frames frames)" ($late -gt 0 -and [Math]::Abs($frames - $late) -le 3)
                if ($playerLoop) { Check "LateUpdate after Update ($lateAfter of $late)" ($lateAfter -ge $late - 3) }
                Check "FixedUpdate runs ($fixed fixed updates)" ($fixed -gt 0)
            } else { Check 'LateUpdate and FixedUpdate reported' $false }
            $unexpected = $log -split "`n" | Select-String '\[(Warn |Error)\]' | Where-Object { $_ -notmatch 'lustral\.tests\.othergame' } |
                Where-Object { $run.Version -notin $beyondLayoutTable -or $_ -notmatch 'the version of Lustral Test Player is not yet known' }
            Check 'no other warnings or errors' (-not $unexpected) (($unexpected | Select-Object -First 3) -join ' | ')
            Check 'session marker removed' ((Markers $dir).Count -eq 0)

            # Windowed, so that rendering-driven fallbacks are also exercised.
            $code = Start-Player $dir (@('-lustral-test', 'quit') + $windowed)
            $log = Read-Log $dir
            if ($log -match 'e2e: shutdown after (\d+) updates and (\d+) driver frames') {
                $updates = [int]$Matches[1]; $frames = [int]$Matches[2]
                Check "windowed: Update once per frame ($updates updates, $frames frames)" ($updates -gt 0 -and [Math]::Abs($frames - $updates) -le 3)
            } else { Check 'windowed: shutdown runs' $false }
            # OnGUI requires rendering: Unity sends IMGUI events (layout, repaint) only to a window.
            Check 'windowed: OnGUI runs' ($log -match 'e2e: \d+ late updates .*, ([1-9]\d*) GUI calls') (($log -split "`n" | Select-String 'GUI calls|IMGUI|OnGUI' | Select-Object -First 2) -join ' | ')
            Check 'windowed: no warnings or errors' (-not ($log -match '\[(Warn |Error)\]')) (($log -split "`n" | Select-String '\[(Warn |Error)\]' | Select-Object -First 3) -join ' | ')

            # --no-mods on the command line (for example in Steam's launch options) starts the game without Lustral.
            $latest = Join-Path $dir 'Lustral\Logs\latest.log'
            Remove-Item -LiteralPath $latest -ErrorAction SilentlyContinue
            $code = Start-Player $dir (@('-batchmode', '-nographics', '-lustral-test', 'quit', '--no-mods'))
            $stage0 = Get-Content -Raw -LiteralPath (Join-Path $dir 'Lustral\Logs\stage0.log')
            Check '--no-mods: the game runs without Lustral' ($code -eq 0 -and $stage0 -match 'Started with --no-mods' -and -not (Test-Path -LiteralPath $latest)) "(exit $code)"
        }

        if ($Scenario -in 'crash', 'all') {
            $script:current = "$($run.Version) $b crash"
            Write-Host "==> $script:current" -ForegroundColor Cyan
            # A crashed player exits once Unity's crash handler and Windows Error Reporting are done, which took up to a
            # minute on GitHub's runners.
            $started = Get-Date
            $code = Start-Player $dir (@('-lustral-test', 'crash') + $windowed) 180
            $seconds = [int]((Get-Date) - $started).TotalSeconds
            Check "crashes (exited after $seconds s)" ($code -eq -1073741819) "(exit $code)"
            Check 'leaves its session marker' ((Markers $dir).Count -eq 1)
            Start-Player $dir (@('-lustral-test', 'quit') + $windowed) | Out-Null
            $reports = @(Get-ChildItem -LiteralPath (Join-Path $dir 'Lustral\Reports') -Filter 'crash-*.txt' -ErrorAction SilentlyContinue)
            Check 'the next start writes one report' ($reports.Count -eq 1)
            if ($reports.Count -eq 1) {
                $report = Get-Content -LiteralPath $reports[0].FullName -Raw
                Check 'report lists the mod' ($report -match 'lustral.tests.player 1.0.0')
                Check 'report lists the patches' ($report -match 'lustral.tests.player: .*TestDriver.Awake')
                Check "report has Windows' record" ($report -match "Windows' record of the crash")
                if ($b -eq 'mono') {
                    Check "report has Unity's stack trace" ($report -match 'OUTPUTTING STACK TRACE' -and $report -match 'END OF STACKTRACE')
                    # MonoMod runs patched methods as dynamic methods named DMD<Type::Method>.
                    Check 'report names the crashing managed frame' ($report -match 'TestDriver:Update' -or $report -match 'DMD<Lustral\.TestPlayer\.TestDriver::Update>')
                    Check 'report names the mods behind the crash' ($report -match 'patched a method that does: .*lustral\.tests\.player')
                } else {
                    # The crash occurs within the mod's patch on Update, so the .NET runtime terminates the process
                    # instead of Unity's crash handler.
                    Check ".NET's record names the patched method" ($report -match "\.NET's record of the crash" -and $report -match 'TestDriver::Update')
                    Check 'report names the mod that patched it' ($report -match 'or patched a method that does: lustral.tests.player')
                }
            }
            Check 'markers cleaned up' ((Markers $dir).Count -eq 0)
        }

        if ($Scenario -in 'throw-on-thread', 'all' -and $b -eq 'il2cpp') {
            $script:current = "$($run.Version) $b throw-on-thread"
            Write-Host "==> $script:current" -ForegroundColor Cyan
            Remove-Item -LiteralPath (Join-Path $dir 'Lustral\Reports') -Recurse -Force -ErrorAction SilentlyContinue
            Start-Player $dir (@('-batchmode', '-nographics', '-lustral-test', 'idle', '-lustral-e2e', 'throw-on-thread')) 30 | Out-Null
            $reports = @(Get-ChildItem -LiteralPath (Join-Path $dir 'Lustral\Reports') -Filter 'crash-*.txt' -ErrorAction SilentlyContinue)
            Check 'reported as it happens' ($reports.Count -eq 1)
            if ($reports.Count -eq 1) {
                $report = Get-Content -LiteralPath $reports[0].FullName -Raw
                Check 'report has the exception' ($report -match "e2e: unhandled exception in a mod's thread")
                Check 'report names the mod on the stack' ($report -match 'These mods are on the exception''s stack: lustral.tests.player')
            }
            Check 'no marker left to report twice' ((Markers $dir).Count -eq 0)
        }

        if ($Scenario -in 'updates', 'all') {
            $script:current = "$($run.Version) $b updates"
            Write-Host "==> $script:current" -ForegroundColor Cyan
            $project = Join-Path $repo 'tests\Lustral.TestPlayer.Updated'
            $feedRoot = Join-Path $repo ".cache\e2e-updates\$($run.Version)-$b"
            $modDir = Join-Path $dir 'Mods\lustral.tests.updated'
            $state = Join-Path $dir 'Lustral\updates\state.toml'
            $port = 8765 + $runs.IndexOf($run)
            $feed = "http://localhost:$port/feed.json"
            $modData = Join-Path $dir 'Lustral\data\lustral.tests.updated'
            Remove-Item -LiteralPath $feedRoot, $modDir, $modData, (Join-Path $dir 'Lustral\updates') -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $feedRoot | Out-Null

            # Version 1.0.0 is installed; 2.0.0 and a broken 3.0.0 are packaged for the feed (for this backend, as bin
            # contains the builds for both target frameworks).
            Get-ChildItem -LiteralPath (Join-Path $project 'bin') -Recurse -Filter 'lustral.tests.updated-*.zip' -ErrorAction SilentlyContinue | Remove-Item
            dotnet build $project -nologo -v quiet "-p:LustralGameDir=$dir" "-p:Version=1.0.0" "-p:ModUpdateSource=$feed" | Out-Null
            foreach ($package in @(@{ Version = '2.0.0'; Broken = 'false' }, @{ Version = '3.0.0'; Broken = 'true' })) {
                dotnet build $project -nologo -v quiet "-p:LustralGameDir=$dir" "-p:Version=$($package.Version)" "-p:E2EBroken=$($package.Broken)" `
                    "-p:ModUpdateSource=$feed" '-p:LustralDeploy=false' '-p:ModPackage=true' | Out-Null
                Copy-Item -LiteralPath (Get-ChildItem -LiteralPath (Join-Path $project 'bin') -Recurse -Filter "lustral.tests.updated-$($package.Version).zip" | Select-Object -First 1).FullName -Destination $feedRoot
            }
            # Restores the 1.0.0 build output, so that the project is not left at 3.0.0.
            dotnet build $project -nologo -v quiet "-p:LustralGameDir=$dir" "-p:Version=1.0.0" "-p:ModUpdateSource=$feed" '-p:LustralDeploy=false' | Out-Null
            function Offer([string]$version) {
                $zip = Join-Path $feedRoot "lustral.tests.updated-$version.zip"
                $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
                Set-Content -LiteralPath (Join-Path $feedRoot 'feed.json') -Encoding ascii -Value (
                    "{ `"releases`": [ { `"version`": `"$version`", `"url`": `"lustral.tests.updated-$version.zip`", `"sha256`": `"$hash`" } ] }")
            }
            # Update sources are checked at most every six hours, so the test clears the time of the last check.
            function Forget-LastCheck { if (Test-Path -LiteralPath $state) { (Get-Content -LiteralPath $state) | Where-Object { $_ -notmatch '^checked = ' } | Set-Content -LiteralPath $state } }
            function Updates-Run([string]$label) {
                Start-Player $dir (@('-batchmode', '-nographics', '-lustral-test', 'quit')) | Out-Null
                $text = Read-Log $dir
                Forget-LastCheck
                return $text
            }

            $server = Start-FileServer $feedRoot $port
            try {
                Offer '2.0.0'
                $log = Updates-Run 'download 2.0.0'
                Check '1.0.0 runs and downloads 2.0.0 in the background' ($log -match 'e2e update: running 1\.0\.0' -and $log -match 'Downloaded Updates test mod 2\.0\.0')
                $log = Updates-Run 'install 2.0.0'
                Check 'the next start installs 2.0.0 before loading it' ($log -match 'Updated Updates test mod from 1\.0\.0 to 2\.0\.0' -and $log -match 'e2e update: running 2\.0\.0')
                Check "the update brings the mod's files, and its data folder is kept" ($log -match 'e2e update: running 2\.0\.0, asset "shipped with the mod", run 2')
                Offer '3.0.0'
                $log = Updates-Run 'download 3.0.0'
                Check 'a newer, broken 3.0.0 is downloaded' ($log -match 'Downloaded Updates test mod 3\.0\.0')
                $log = Updates-Run 'install 3.0.0'
                Check '3.0.0 fails to load and is rolled back' ($log -match 'Updated Updates test mod from 2\.0\.0 to 3\.0\.0' -and
                    $log -match "e2e update: this version is broken" -and $log -match 'Updates test mod 3\.0\.0 failed to load; Lustral restores 2\.0\.0')
                $log = Updates-Run 'roll back'
                Check '2.0.0 is restored, and 3.0.0 is not downloaded again' ($log -match 'Restored Updates test mod 2\.0\.0' -and
                    $log -match 'e2e update: running 2\.0\.0' -and -not ($log -match 'Downloaded Updates test mod 3\.0\.0'))
            } finally {
                Stop-FileServer $server
                Remove-Item -LiteralPath $modDir, $modData, (Join-Path $dir 'Lustral\updates') -Recurse -Force -ErrorAction SilentlyContinue
                Check 'session marker removed' ((Markers $dir).Count -eq 0)
            }
        }
    }
    foreach ($v in @($runs | ForEach-Object Version | Select-Object -Unique)) {
        $dir = Join-Path $Players "$v\win-x64-mono-legacy"
        if (-not (Test-Path -LiteralPath (Join-Path $dir 'LustralTestPlayer.exe')) -or $Scenario -notin 'quit', 'all' -or $Backend -eq 'il2cpp') { continue }
        $script:current = "$v mono legacy runtime"
        Write-Host "==> $script:current" -ForegroundColor Cyan
        if (-not $NoDeploy) { & (Join-Path $PSScriptRoot 'deploy.ps1') -GameDir $dir | Out-Null }
        $env:LUSTRAL_NO_MESSAGE_BOX = '1'
        try { $code = Start-Player $dir (@('-batchmode', '-nographics', '-lustral-test', 'quit')) } finally { Remove-Item Env:LUSTRAL_NO_MESSAGE_BOX }
        $stage0 = Get-Content -LiteralPath (Join-Path $dir 'Lustral\Logs\stage0.log') -Raw
        Check 'the game runs without Lustral' ($code -eq 0) "(exit $code)"
        Check 'stage 0 names the legacy runtime' ($stage0 -match "uses Unity's legacy \.NET 3\.5 scripting runtime, which Lustral does not support")
    }
} finally {
    Get-Process -Name LustralTestPlayer -ErrorAction SilentlyContinue | Stop-Process -Force
    Pop-Location
}

Write-Host ''
if ($failures.Count -eq 0) { Write-Host 'All end-to-end checks passed.' -ForegroundColor Green }
else { Write-Host "$($failures.Count) end-to-end check(s) failed:" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  $_" }; exit 1 }
