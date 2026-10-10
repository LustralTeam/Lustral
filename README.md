<p align="center">
  <img src=".github/logo.svg" width="128" height="128" alt="Lustral">
</p>

<h1 align="center">Lustral</h1>

<p align="center">
  A mod loader for Unity games, supporting both the Mono and IL2CPP scripting backends.<br>
  <a href="https://lustralteam.github.io/Lustral.Docs/">Documentation</a> ·
  <a href="https://github.com/LustralTeam/Lustral/releases">Releases</a> ·
  <a href="https://github.com/LustralTeam/Lustral/issues">Issue Tracker</a> ·
  <a href="https://discord.gg/3Y6Ete73E3">Discord</a>
</p>

<p align="center">
  <a href="https://github.com/LustralTeam/Lustral/releases/latest"><img src="https://img.shields.io/github/v/release/LustralTeam/Lustral?label=Latest%20release&color=0d9488" alt="Latest release"></a>
  <a href="https://github.com/LustralTeam/Lustral/actions/workflows/ci.yml"><img src="https://github.com/LustralTeam/Lustral/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://discord.gg/3Y6Ete73E3"><img src="https://img.shields.io/badge/Discord-Join%20the%20server-5865F2?logo=discord&logoColor=white" alt="Discord"></a>
</p>

<p align="center">
  <a href="https://github.com/LustralTeam/Lustral/releases/latest/download/lustral-win-x64-il2cpp.zip"><img src="https://img.shields.io/badge/Download_for_IL2CPP_games-0284c7?style=for-the-badge" alt="Download for IL2CPP games"></a>
  <a href="https://github.com/LustralTeam/Lustral/releases/latest/download/lustral-win-x64-mono.zip"><img src="https://img.shields.io/badge/Download_for_Mono_games-0d9488?style=for-the-badge" alt="Download for Mono games"></a><br>
  <sub>IL2CPP if the game folder contains <code>GameAssembly.dll</code>, otherwise Mono. Windows, 64-bit games.</sub>
</p>

## Overview

Lustral loads mods into Unity games and provides them with a common API for logging, Harmony patching, configuration,
per-frame events, coroutines and asset bundles. The API is identical on Mono and IL2CPP games. On IL2CPP games, mods run
on .NET 10 against interop assemblies that Lustral generates for each game on its first launch.

> [!NOTE]
> Lustral is in early testing. The current release supports Windows only, and the API may change before version 1.0.
> Feedback and bug reports are welcome on the [issue tracker](https://github.com/LustralTeam/Lustral/issues).

## Features

- A single mod API for Mono and IL2CPP games
- An MSBuild SDK and project template that locate the game, reference its assemblies, generate the mod manifest and
  deploy the mod on build
- HarmonyX patching, with Roslyn analyzers that validate patch targets at compile time
- Typed mod settings stored in TOML
- Dependency resolution with version ranges, optional dependencies, incompatibilities and library mods
- Automatic mod updates from GitHub releases or JSON feeds, with rollback on failure
- Restoration of Unity APIs stripped from IL2CPP builds
- Structured logging, an optional color console and crash reports that identify the mods involved

## System Requirements

| Component | Supported |
| --- | --- |
| Operating system | Windows 10 or later, x64 |
| Mono games | Unity 2018.1 and later, with the .NET 4.x scripting runtime |
| IL2CPP games | Unity 2018.1 and later |

Lustral is tested on Unity 2018.4, 2021.3, 2022.3, 6000.5 and 6000.6. Unity 2018 games that use the legacy .NET 3.5
scripting runtime are not supported; Lustral reports this when the game starts.

Mods written for MelonLoader or BepInEx are not compatible with Lustral.

## Installation

1. Download the latest release package that matches the game's scripting backend:
   - [`lustral-win-x64-il2cpp.zip`](https://github.com/LustralTeam/Lustral/releases/latest/download/lustral-win-x64-il2cpp.zip)
     if the game folder contains `GameAssembly.dll`
   - [`lustral-win-x64-mono.zip`](https://github.com/LustralTeam/Lustral/releases/latest/download/lustral-win-x64-mono.zip)
     otherwise

   Earlier versions are available on the [releases page](https://github.com/LustralTeam/Lustral/releases).
2. Extract the archive into the game folder, next to the game's executable.
3. Launch the game once. Lustral creates the `Mods` folder, where mods are installed.

See the [Installation Guide](https://lustralteam.github.io/Lustral.Docs/user-guide/installation) for configuration, mod updates and
troubleshooting.

## Getting Started with Mod Development

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and a game with Lustral installed.

```sh
dotnet new install Lustral.Templates
dotnet new lustral-mod -n BetterHud --game "Lethal Company" --id you.betterhud
cd BetterHud
dotnet build
```

In JetBrains Rider, the template can also be installed without the command line, from the `Lustral.Templates` package
on the releases page; once installed, it is available in the new-project dialogs of Visual Studio and Rider.

Building compiles the mod against the game, generates its manifest and deploys it to the game's `Mods` folder.
A single project can also target both Mono and IL2CPP games. See the
[Developer Guide](https://lustralteam.github.io/Lustral.Docs/developer-guide/) and the
[API Reference](https://lustralteam.github.io/Lustral.Docs/api/).

## Building from Source

Prerequisites: Windows, the .NET 10 SDK, and Visual Studio 2022 (17.14 or later) or 2026 with the "Desktop development
with C++" workload, which is required for the native loader in `stage0`.

```sh
dotnet build Lustral.slnx                         # managed projects
./stage0/build.ps1                                # native loader
./tools/deploy.ps1 -GameDir "D:\Games\Some Game"  # build and install into a game; -Uninstall removes it
./tools/pack.ps1                                  # release packages, written to artifacts/release/<version>
```

[CONTRIBUTING.md](CONTRIBUTING.md) describes the development environment, the unit and end-to-end tests, and the
guidelines for contributions.

## Repository Structure

| Path | Contents |
| --- | --- |
| `src/Lustral.Api` | Public mod API |
| `src/Lustral.Core` | Mod discovery and loading, manifests, configuration, updates, logging, crash reporting |
| `src/Lustral.Backend.Mono`, `src/Lustral.Backend.Il2Cpp` | Scripting backend implementations |
| `src/Lustral.Host`, `src/Lustral.Host.Mono` | Managed entry points |
| `src/Lustral.Generator` | IL2CPP interop assembly generator |
| `src/Lustral.Sdk`, `src/Lustral.Templates`, `src/Lustral.Analyzers` | Mod SDK, project template and analyzers |
| `stage0` | Native loader (`winhttp.dll` proxy) |
| `samples`, `tests`, `tools` | Sample mods, tests and build scripts |

Related repositories:

- [Lustral.Docs](https://github.com/LustralTeam/Lustral.Docs): documentation website
- [Lustral.UnityLibraries](https://github.com/LustralTeam/Lustral.UnityLibraries): Unity managed libraries used to
  restore stripped APIs on IL2CPP games

## Contributing

Bug reports, feature requests and pull requests are welcome. Questions and discussion take place on the
[Discord server](https://discord.gg/3Y6Ete73E3). See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a
pull request. Contributions to the documentation are made in
[Lustral.Docs](https://github.com/LustralTeam/Lustral.Docs).

## License

Lustral is licensed under the [GNU Lesser General Public License, version 3](COPYING.LESSER), which supplements
the [GNU General Public License, version 3](COPYING). The project template (`src/Lustral.Templates`) and the sample mods
(`samples`) are licensed under [MIT No Attribution](samples/LICENSE), so mods created from them can use any license.

Mods that use Lustral's API are separate works and can be distributed under any license. The LGPL applies to Lustral
itself: distributions of a modified Lustral must make its source code available under the same license.

## Acknowledgements

Lustral builds on [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop), [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL),
[HarmonyX](https://github.com/BepInEx/HarmonyX), [MonoMod](https://github.com/MonoMod/MonoMod),
[AsmResolver](https://github.com/Washi1337/AsmResolver) and the [.NET runtime](https://github.com/dotnet/runtime).
Third-party licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

Mods are third-party software. Lustral does not verify their behavior and is not responsible for any damage they cause.
Lustral is not affiliated with or endorsed by Unity Technologies or any game developer.
