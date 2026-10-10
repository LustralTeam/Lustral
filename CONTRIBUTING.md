# Contributing to Lustral

This guide describes how to report issues, set up a development environment, build and test Lustral, and submit
changes. Contributions to the documentation are made in the
[Lustral.Docs](https://github.com/LustralTeam/Lustral.Docs) repository.

## Reporting Issues

Report bugs and request features on the [issue tracker](https://github.com/LustralTeam/Lustral/issues). Before opening
an issue, check whether it has already been reported. Questions belong on the [Discord server](https://discord.gg/3Y6Ete73E3) instead:
`#user-support` for using Lustral, `#developer-support` for mod development, and `#lustral-development` for working on
Lustral itself.

A bug report should include:

- The Lustral version, and whether the Mono or IL2CPP package is installed.
- The game, its version and its Unity version. The first lines of `Lustral/Logs/latest.log` contain all three.
- The steps to reproduce the problem, and the expected and actual behavior.
- `Lustral/Logs/latest.log` from the affected session and, after a crash, the report in `Lustral/Reports`.
- The installed mods. If the problem occurs only with a particular mod, report it to the mod's author first.

Do not report security vulnerabilities in public issues. Use GitHub's private vulnerability reporting
(**Security > Report a vulnerability**) instead.

## Development Environment

Lustral is developed on Windows. The following tools are required:

- **Windows 10 or later, x64.**
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**, version 10.0.100 or a later feature band (see
  `global.json`).
- **Visual Studio 2022 (17.14 or later) or 2026** with the **Desktop development with C++** workload, which provides the
  MSVC compiler, CMake and Ninja for stage 0, the native loader. The managed projects can be developed in any IDE,
  including JetBrains Rider and Visual Studio Code.
- **Git.**

The end-to-end tests run against prebuilt Unity test players, which are downloaded (see
[End-to-End Tests](#end-to-end-tests)); no Unity installation is required.

## Building

```sh
dotnet build Lustral.slnx    # managed projects
./stage0/build.ps1           # stage 0 (stage0/build/x64-release/winhttp.dll); -Preset x64-debug for a debug build
./tools/pack.ps1             # release packages, written to artifacts/release/<version>
```

Building the solution also builds the sample mods, without deploying them to their games. A sample is deployed when it
is built on its own, for example `dotnet build samples/Lustral.Samples.PlagueInc` or from an IDE, or with
`tools/deploy.ps1` (see below); `-p:LustralDeploy=true` deploys all samples from a solution build. Samples whose game is
not installed, or whose IL2CPP game has not been prepared by Lustral yet, are skipped with a warning.

The NuGet packages for mod developers (Lustral.Api, Lustral.Sdk and Lustral.Templates) are created with
`dotnet pack src/<project> -o artifacts/packages`.

## Testing

### Unit Tests

```sh
dotnet test --project tests/Lustral.Core.Tests
dotnet test --project tests/Lustral.Analyzers.Tests
dotnet test --project tests/Lustral.Backend.Il2Cpp.Tests
./stage0/build/x64-release/stage0_test.exe forwarders ./stage0/build/x64-release/winhttp.dll
```

Some tests in Lustral.Backend.Il2Cpp.Tests require a specific game and are skipped if it is not installed.

### End-to-End Tests

The end-to-end tests run Lustral and a test mod in Lustral's own Unity test players, on both scripting backends and on
each tested Unity version. They cover loading, patching, scene and frame events, coroutines, asset bundles, settings,
crash reports and automatic updates.

```sh
./tools/get-testplayers.ps1    # downloads the test players
./tools/e2e.ps1                # runs all scenarios on all test players
./tools/e2e.ps1 -Backend il2cpp -Scenario quit -UnityVersion 6000.5.4f1
```

The test players are published as releases of [Lustral.TestPlayers](https://github.com/LustralTeam/Lustral.TestPlayers),
one per Unity version, whose README describes what they do; `tests/testplayers.json` lists the releases that Lustral
tests against, and CI tests each Unity version in its own job. The players are built from LustralTeam's Unity project,
which is not public.

End-to-end tests, and crash tests in particular, must only be run against the test players, never against commercial
games: many games send crash reports to their developers.

### Testing in a Game

`tools/deploy.ps1` builds Lustral and installs it into a game. For the games that have a sample mod, `-Game` (for
example `-Game plagueinc`) also installs the sample.

```sh
./tools/deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Some Game"
./tools/deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Some Game" -Uninstall
```

The installation is recorded in `Lustral/installed.txt`, and `-Uninstall` removes exactly what was installed. Setting
the environment variable `LUSTRAL_LOG_LEVEL=debug` enables debug logging without editing `Lustral/loader.toml`.

Do not test with games that use anti-cheat software, and do not join online sessions with modified games.

## Repository Structure

| Path | Contents |
| --- | --- |
| `src/Lustral.Api` | Public mod API; its XML documentation is published as the API reference |
| `src/Lustral.Core` | Mod discovery, loading, manifests, configuration, updates, logging and crash reporting |
| `src/Lustral.Backend.Mono`, `src/Lustral.Backend.Il2Cpp` | Scripting backend implementations |
| `src/Lustral.Host`, `src/Lustral.Host.Mono` | Managed entry points called by stage 0 |
| `src/Lustral.Generator` | IL2CPP interop assembly generator |
| `src/Lustral.Sdk`, `src/Lustral.Templates`, `src/Lustral.Analyzers` | Mod SDK, project template and analyzers |
| `src/Shared` | Code compiled into several projects |
| `stage0` | Native loader (`winhttp.dll` proxy), in C++ |
| `samples` | Sample mods |
| `tests` | Unit tests, test mods and the Unity test player project |
| `tools` | Build, deployment, packaging and test scripts |

## Guidelines

### Code

- Follow the conventions in `.editorconfig`. Nullable reference types are enabled in all projects.
- Add comments only where they explain something that is not evident from the code, such as the reason for a
  workaround. Write them, and all user-facing text, in a formal and neutral style.
- Log messages, exceptions and build diagnostics should state the problem and, where possible, how to resolve it. The
  documentation quotes some messages; update it when changing them.
- Changes to the public API (`src/Lustral.Api`) require XML documentation for every public member. After an API change,
  regenerate the API reference in Lustral.Docs with `npm run api`.
- Add or update tests for every change in behavior: unit tests for logic, and end-to-end scenarios for behavior that
  depends on Unity.
- New third-party dependencies must have a license compatible with LGPL-3.0 and must be added to
  `THIRD-PARTY-NOTICES.md`. Discuss them in an issue first.

### Commits and Pull Requests

- Write commit messages in the [Conventional Commits](https://www.conventionalcommits.org) format, for example
  `fix(core): report mods placed in the wrong folder`.
- Keep each pull request focused on a single change, and describe how it was tested.
- Pull requests must pass the CI workflow (build, unit tests, stage 0 tests and end-to-end tests).

## License

Lustral is licensed under the [GNU Lesser General Public License, version 3](COPYING.LESSER), which supplements
the [GNU General Public License, version 3](COPYING). The project template (`src/Lustral.Templates`) and the sample mods
(`samples`) are licensed under [MIT No Attribution](samples/LICENSE), so that mods based on them can use any license. By submitting a contribution, you agree that it is licensed under the license that applies to the files
it changes.
