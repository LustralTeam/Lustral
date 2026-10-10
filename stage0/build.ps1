<#
.SYNOPSIS
    Builds stage 0 (stage0\build\<preset>\winhttp.dll) with MSVC and Ninja.
.DESCRIPTION
    Locates Visual Studio with the C++ x64 build tools using vswhere (including preview releases) and builds in its x64
    developer environment, so that a Developer PowerShell is not required.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64-release', 'x64-debug')]
    [string]$Preset = 'x64-release'
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ x64 build tools is required (component VC.Tools.x86.x64).' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
# vcvars64.bat invokes vswhere.exe without a path.
$env:PATH = "$(Split-Path $vswhere);$env:PATH"

Push-Location $PSScriptRoot
try {
    cmd /c "`"$vcvars`" >nul && cmake --preset $Preset && cmake --build --preset $Preset"
    if ($LASTEXITCODE -ne 0) { throw "Stage 0 build failed (exit code $LASTEXITCODE)" }
} finally {
    Pop-Location
}
