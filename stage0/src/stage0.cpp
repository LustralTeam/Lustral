#include "stage0.h"

#include "log.h"
#include "pe.h"
#include "version.h"

#include <windows.h>

#include <atomic>
#include <cstring>
#include <string>

namespace lustral {
namespace {

Paths g_paths;
std::atomic<bool> g_started{false};

using il2cpp_init_fn = int (*)(const char* domain_name);
using mono_jit_init_version_fn = void* (*)(const char* root_domain_name, const char* runtime_version);

il2cpp_init_fn g_il2cpp_init = nullptr;
mono_jit_init_version_fn g_mono_jit_init_version = nullptr;
HMODULE g_mono_module = nullptr;

bool environment_flag(const wchar_t* name)
{
    wchar_t value[8] = {};
    return GetEnvironmentVariableW(name, value, 8) > 0 && value[0] == L'1';
}

bool directory_exists(const std::wstring& path)
{
    DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

std::wstring module_path(HMODULE module)
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
        if (length == 0)
            return {};
        if (length < path.size()) {
            path.resize(length);
            return path;
        }
        path.resize(path.size() * 2);
    }
}

// Both wrappers call the original function first, so that the scripting runtime is fully initialized before .NET starts.
int hook_il2cpp_init(const char* domain_name)
{
    int result = g_il2cpp_init(domain_name);
    if (!g_started.exchange(true)) {
        log::writef(log::Level::info, L"il2cpp_init(\"%hs\") returned %d; starting CoreCLR", domain_name, result);
        start_coreclr(g_paths);
    }
    return result;
}

using mono_jit_parse_options_fn = void (*)(int argc, char* argv[]);
using mono_debug_init_fn = void (*)(int format);
using mono_debug_enabled_fn = int (*)();

// Mono retains the options it parses, so this string must outlive the call.
std::string g_debugger_option;

// LUSTRAL_MONO_DEBUGGER=<address>[,suspend]: enables Mono's soft debugger agent on that address for Visual Studio Tools
// for Unity ("Attach Unity Debugger"), Rider or dnSpy. The agent must be enabled before Mono starts, as with Doorstop's
// debug option. The variable is set by a mod's debug launch profile, not in normal play. With ",suspend", the game waits
// at startup until a debugger attaches, so that OnLoad can be debugged. (Unity's Mono has no "defer" option and exits on
// unknown options.)
void enable_mono_debugger()
{
    wchar_t value[128] = {};
    DWORD length = GetEnvironmentVariableW(L"LUSTRAL_MONO_DEBUGGER", value, 128);
    if (length == 0 || length >= 128)
        return;
    std::wstring setting(value, length);
    bool suspend = false;
    if (auto comma = setting.find(L','); comma != std::wstring::npos) {
        suspend = setting.compare(comma + 1, std::wstring::npos, L"suspend") == 0;
        setting.resize(comma);
    }

    auto parse_options = reinterpret_cast<mono_jit_parse_options_fn>(GetProcAddress(g_mono_module, "mono_jit_parse_options"));
    auto debug_init = reinterpret_cast<mono_debug_init_fn>(GetProcAddress(g_mono_module, "mono_debug_init"));
    auto debug_enabled = reinterpret_cast<mono_debug_enabled_fn>(GetProcAddress(g_mono_module, "mono_debug_enabled"));
    if (parse_options == nullptr || debug_init == nullptr) {
        log::writef(log::Level::warn, L"LUSTRAL_MONO_DEBUGGER is set, but this Mono runtime has no mono_jit_parse_options or mono_debug_init");
        return;
    }
    g_debugger_option = "--debugger-agent=transport=dt_socket,server=y,address=";
    for (wchar_t c : setting)
        g_debugger_option += static_cast<char>(c); // Addresses are ASCII.
    g_debugger_option += suspend ? ",suspend=y" : ",suspend=n";
    log::writef(log::Level::info, L"Enabling Mono's debugger agent: %hs", g_debugger_option.c_str());
    char* argv[] = {g_debugger_option.data()};
    parse_options(1, argv);
    // Unity players usually initialize Mono's debug support themselves (for line numbers), and a second call aborts.
    if (debug_enabled == nullptr || debug_enabled() == 0)
        debug_init(1); // MONO_DEBUG_FORMAT_MONO
    log::writef(log::Level::info, L"Mono's debugger agent is listening on %ls%ls", setting.c_str(),
                suspend ? L"; the game waits for a debugger to attach" : L"");
}

void* hook_mono_jit_init_version(const char* root_domain_name, const char* runtime_version)
{
    if (!g_started.load())
        enable_mono_debugger();
    void* domain = g_mono_jit_init_version(root_domain_name, runtime_version);
    // Unity's legacy scripting runtime (.NET 3.5, Mono 2.x; selectable until Unity 2018.4) cannot load Lustral, which
    // targets .NET Framework 4.7.2 and .NET Standard 2.0. The game continues without mods.
    if (runtime_version != nullptr && std::strncmp(runtime_version, "v2.", 3) == 0 && !g_started.exchange(true)) {
        report_fatal(L"Lustral cannot run in this game: it uses Unity's legacy .NET 3.5 scripting runtime, which Lustral does "
                     L"not support (Lustral requires Unity 2018.1 or later with the .NET 4.x runtime). The game continues "
                     L"without mods; delete winhttp.dll from the game folder to remove Lustral.");
        return domain;
    }
    if (!g_started.exchange(true)) {
        log::writef(log::Level::info, L"mono_jit_init_version(\"%hs\", \"%hs\") returned %p; loading Lustral into Mono",
                    root_domain_name, runtime_version, domain);
        start_mono(g_mono_module, domain, g_paths);
    }
    return domain;
}

// UnityPlayer looks up the scripting runtime's entry points with GetProcAddress, and two of them are wrapped. This runs
// on every lookup UnityPlayer makes, so it must be fast.
FARPROC WINAPI hook_GetProcAddress(HMODULE module, LPCSTR name)
{
    FARPROC real = GetProcAddress(module, name); // Lustral's own import of GetProcAddress is not patched.
    if (real == nullptr || IS_INTRESOURCE(name))
        return real;

    if (std::strcmp(name, "il2cpp_init") == 0) {
        g_il2cpp_init = reinterpret_cast<il2cpp_init_fn>(real);
        return reinterpret_cast<FARPROC>(&hook_il2cpp_init);
    }
    if (std::strcmp(name, "mono_jit_init_version") == 0) {
        g_mono_jit_init_version = reinterpret_cast<mono_jit_init_version_fn>(real);
        g_mono_module = module;
        return reinterpret_cast<FARPROC>(&hook_mono_jit_init_version);
    }
    return real;
}

} // namespace

Paths paths_for_executable(const std::wstring& executable)
{
    Paths paths;
    paths.executable = executable;
    paths.game_directory = executable.substr(0, executable.find_last_of(L"\\/"));
    paths.lustral_directory = paths.game_directory + L"\\Lustral";
    paths.core_directory = paths.lustral_directory + L"\\core";
    paths.dotnet_directory = paths.lustral_directory + L"\\dotnet";
    paths.logs_directory = paths.lustral_directory + L"\\Logs";
    return paths;
}

bool command_line_has(const wchar_t* command_line, const wchar_t* argument)
{
    std::wstring token;
    bool quoted = false;
    for (const wchar_t* c = command_line;; ++c) {
        if (*c == L'"') {
            quoted = !quoted;
        } else if (*c == L'\0' || (!quoted && (*c == L' ' || *c == L'\t'))) {
            if (!token.empty() && CompareStringOrdinal(token.c_str(), -1, argument, -1, TRUE) == CSTR_EQUAL)
                return true;
            if (*c == L'\0')
                return false;
            token.clear();
        } else {
            token += *c;
        }
    }
}

void on_process_attach()
{
    if (environment_flag(L"LUSTRAL_DISABLE"))
        return;

    // Only the Unity player loads UnityPlayer.dll. Other processes that load this proxy from the game folder (such as
    // Unity's and Sentry's crash handlers) receive only the winhttp.dll forwarding.
    HMODULE unity_player = GetModuleHandleW(L"UnityPlayer.dll");
    if (unity_player == nullptr)
        return;

    g_paths = paths_for_executable(module_path(nullptr));
    if (!directory_exists(g_paths.lustral_directory))
        return; // Not a Unity player; the proxy remains inactive.

    // The loader has already bound UnityPlayer's imports (its DllMain has not run yet), so patching its import table here
    // is sufficient. File I/O is permitted under the loader lock; loading DLLs and starting threads are not.
    log::open(g_paths.logs_directory);
    if (command_line_has(GetCommandLineW(), disable_argument)) {
        log::writef(log::Level::info, L"Started with %ls: Lustral is not loaded, and the game runs without mods", disable_argument);
        return;
    }
    log::writef(log::Level::info, L"Lustral stage 0 %ls in %ls", LUSTRAL_VERSION_W, g_paths.executable.c_str());
    int patched = pe::patch_import(unity_player, "GetProcAddress", reinterpret_cast<void*>(&hook_GetProcAddress));
    if (patched == 0)
        log::write(log::Level::error, L"UnityPlayer.dll does not import GetProcAddress by name; Lustral cannot hook this Unity version");
    else
        log::writef(log::Level::debug, L"Hooked %d GetProcAddress import(s) in UnityPlayer.dll", patched);
}

void report_fatal(const wchar_t* message)
{
    log::write(log::Level::error, message);
    if (!environment_flag(L"LUSTRAL_NO_MESSAGE_BOX"))
        MessageBoxW(nullptr, message, L"Lustral", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}

bool report_wrong_package(const Paths& paths, bool il2cpp_game)
{
    auto other_host = paths.core_directory + (il2cpp_game ? L"\\Lustral.Host.Mono.dll" : L"\\Lustral.Host.dll");
    DWORD attributes = GetFileAttributesW(other_host.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return false;
    // The folders to delete are those of the installed package (see "Updating Lustral" in the installation guide).
    report_fatal(il2cpp_game
        ? L"Lustral cannot start: this game uses IL2CPP, but the Mono package of Lustral is installed.\n\n"
          L"Delete the Lustral\\core folder, then download lustral-win-x64-il2cpp.zip from "
          L"github.com/LustralTeam/Lustral/releases and extract it into the game folder. Your settings and mods are kept. "
          L"To play without mods instead, delete winhttp.dll from the game folder."
        : L"Lustral cannot start: this game uses Mono, but the IL2CPP package of Lustral is installed.\n\n"
          L"Delete the Lustral\\core, Lustral\\dotnet and Lustral\\generator folders, then download lustral-win-x64-mono.zip "
          L"from github.com/LustralTeam/Lustral/releases and extract it into the game folder. Your settings and mods are "
          L"kept. To play without mods instead, delete winhttp.dll from the game folder.");
    return true;
}

std::string to_utf8(const std::wstring& text)
{
    if (text.empty())
        return {};
    int length = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), length, nullptr, nullptr);
    return result;
}

} // namespace lustral
