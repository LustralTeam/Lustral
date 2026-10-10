// Lustral stage 0: Lustral's only native code. It is loaded as a proxy DLL, intercepts the scripting runtime's
// initialization, and hands over to .NET. It contains no mod logic.
#pragma once

#include <cstdint>
#include <string>

namespace lustral {

// The locations of the game and Lustral's folders. Created once and kept for the lifetime of the process, because
// NativeApi points into it.
struct Paths {
    std::wstring executable;        // <game>\<name>.exe
    std::wstring game_directory;    // <game>
    std::wstring lustral_directory; // <game>\Lustral
    std::wstring core_directory;    // <game>\Lustral\core
    std::wstring dotnet_directory;  // <game>\Lustral\dotnet (IL2CPP: the .NET runtime, laid out as a .NET installation)
    std::wstring logs_directory;    // <game>\Lustral\Logs
};

Paths paths_for_executable(const std::wstring& executable);

// The command-line argument that starts the game without Lustral, for example from Steam's launch options. MelonLoader
// uses the same argument, so players already know it.
inline constexpr const wchar_t* disable_argument = L"--no-mods";

// Whether the command line contains the argument, compared without regard to case. Arguments are separated by spaces or
// tabs, and double quotes group them, as the C runtime parses a command line (backslash escapes are not needed here).
bool command_line_has(const wchar_t* command_line, const wchar_t* argument);

// The data that stage 0 passes to Lustral.Host on IL2CPP. Mirrored by Lustral.Host.NativeApi; only append fields, and
// increment the version when doing so.
struct NativeApi {
    static constexpr uint32_t current_version = 1;

    uint32_t size;
    uint32_t version;
    const wchar_t* game_executable;
    const wchar_t* core_directory;
    void (*log)(int32_t level, const wchar_t* message); // Writes to Lustral\Logs\stage0.log.
};

// Called from DllMain. Only patches UnityPlayer's import of GetProcAddress; everything else is deferred to the
// initialization hooks.
void on_process_attach();

// Starts .NET on IL2CPP games (CoreCLR) or loads Lustral into Unity's Mono domain. Never called under the loader lock.
bool start_coreclr(const Paths& paths);
bool start_mono(void* mono_module, void* domain, const Paths& paths);

// Logs the message and shows it in a message box (unless LUSTRAL_NO_MESSAGE_BOX=1), for installation problems that the
// user can resolve.
void report_fatal(const wchar_t* message);

// When the installed package is the other scripting backend's (the Mono package in an IL2CPP game, or the IL2CPP package
// in a Mono game), reports that, naming the package to install, and returns true. Each package is identified by its host:
// Lustral\core\Lustral.Host.dll (IL2CPP) or Lustral.Host.Mono.dll (Mono).
bool report_wrong_package(const Paths& paths, bool il2cpp_game);

std::string to_utf8(const std::wstring& text);

} // namespace lustral
