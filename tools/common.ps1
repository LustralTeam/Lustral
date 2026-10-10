# Shared functions for deploy.ps1 and pack.ps1. Dot-sourced after $repo (the repository root) is set.

# The .NET runtime version shipped for IL2CPP games; matches the runtime pack that Lustral.Host restores.
$dotnetVersion = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.LustralDotNetRuntimeVersion |
    Where-Object { $_ } | Select-Object -First 1
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

function Remove-IfPresent([string]$path) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
        Write-Host "  removed $path"
    }
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# Creates Lustral\dotnet from the runtime pack, laid out as a .NET installation: host\fxr\<version>\hostfxr.dll for the
# Generator's apphost, and shared\Microsoft.NETCore.App\<version> for the runtime itself.
function Install-DotNetRuntime([string]$destination) {
    $pack = Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64\$dotnetVersion\runtimes\win-x64"
    if (-not (Test-Path -LiteralPath $pack)) { throw "The .NET $dotnetVersion runtime pack was not found at $pack. Publish src\Lustral.Host first." }
    $framework = Join-Path $destination "shared\Microsoft.NETCore.App\$dotnetVersion"
    if (Test-Path -LiteralPath (Join-Path $framework 'coreclr.dll')) { return }

    Write-Host "==> Installing the .NET $dotnetVersion runtime" -ForegroundColor Cyan
    Remove-IfPresent $destination # Removes other versions, which stage 0 and the Generator would otherwise prefer if newer.
    $fxr = Join-Path $destination "host\fxr\$dotnetVersion"
    New-Item -ItemType Directory -Force $framework, $fxr | Out-Null
    Copy-Item -Path (Join-Path $pack 'lib\net10.0\*') -Destination $framework
    Get-ChildItem -LiteralPath (Join-Path $pack 'native') -File | Where-Object Name -ne 'hostfxr.dll' | Copy-Item -Destination $framework
    Copy-Item -LiteralPath (Join-Path $pack 'native\hostfxr.dll') -Destination $fxr
    # The runtime's license and third-party notices, as included in a .NET installation.
    foreach ($notice in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
        Copy-Item -LiteralPath (Join-Path (Split-Path (Split-Path $pack)) $notice) -Destination $destination
    }
}
