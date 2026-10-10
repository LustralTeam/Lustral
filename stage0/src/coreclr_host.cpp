// IL2CPP games: starts the .NET runtime that Lustral ships in Lustral\dotnet and calls Lustral.Host in Lustral\core.
//
// Lustral\dotnet is laid out as a .NET installation (host\fxr, shared\Microsoft.NETCore.App\<version>), so that the
// Generator, a separate framework-dependent application, runs on the same runtime. Stage 0 calls coreclr_initialize
// directly rather than through hostfxr, so that it controls all properties (trusted assemblies, probing paths, GC
// settings) and requires no runtimeconfig.json or deps.json.
#include "log.h"
#include "pe.h"
#include "stage0.h"

#include <windows.h>

#include <algorithm>
#include <cwchar>
#include <vector>

namespace lustral {
namespace {

using coreclr_initialize_fn = int (*)(const char* exe_path, const char* app_domain_friendly_name, int property_count,
                                      const char** property_keys, const char** property_values, void** host_handle,
                                      unsigned int* domain_id);
using coreclr_create_delegate_fn = int (*)(void* host_handle, unsigned int domain_id, const char* assembly_name,
                                           const char* type_name, const char* method_name, void** delegate);
using entry_fn = int (*)(const NativeApi* api);

NativeApi g_api{};

void log_from_managed(int32_t level, const wchar_t* message)
{
    log::write(static_cast<log::Level>(level), message);
}

// Appends all managed assemblies in a directory to a TPA list. Native DLLs are excluded.
void add_managed_assemblies(const std::wstring& directory, std::string& list)
{
    WIN32_FIND_DATAW entry;
    HANDLE find = FindFirstFileW((directory + L"\\*.dll").c_str(), &entry);
    if (find == INVALID_HANDLE_VALUE)
        return;
    do {
        auto path = directory + L"\\" + entry.cFileName;
        if (pe::is_managed_assembly(path)) {
            list += to_utf8(path);
            list += ';';
        }
    } while (FindNextFileW(find, &entry));
    FindClose(find);
}

// "10.0.9" -> {10, 0, 9}; a prerelease suffix is ignored. Lustral ships a single runtime, so this only resolves ties.
bool parse_version(const wchar_t* text, unsigned (&parts)[3])
{
    return swscanf_s(text, L"%u.%u.%u", &parts[0], &parts[1], &parts[2]) == 3;
}

// The newest <dotnet>\shared\Microsoft.NETCore.App\<version>, or empty if none exists.
std::wstring find_framework(const std::wstring& dotnet_directory)
{
    auto shared = dotnet_directory + L"\\shared\\Microsoft.NETCore.App";
    std::wstring best;
    unsigned best_version[3] = {};
    WIN32_FIND_DATAW entry;
    HANDLE find = FindFirstFileW((shared + L"\\*").c_str(), &entry);
    if (find == INVALID_HANDLE_VALUE)
        return best;
    do {
        unsigned version[3];
        if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 || !parse_version(entry.cFileName, version))
            continue;
        if (best.empty() || std::lexicographical_compare(best_version, best_version + 3, version, version + 3)) {
            best = shared + L"\\" + entry.cFileName;
            std::copy(version, version + 3, best_version);
        }
    } while (FindNextFileW(find, &entry));
    FindClose(find);
    return best;
}

} // namespace

bool start_coreclr(const Paths& paths)
{
    auto framework = find_framework(paths.dotnet_directory);
    if (framework.empty() && report_wrong_package(paths, true))
        return false;
    auto coreclr_path = (framework.empty() ? paths.dotnet_directory + L"\\shared\\Microsoft.NETCore.App\\<version>" : framework) + L"\\coreclr.dll";
    HMODULE coreclr = framework.empty() ? nullptr : LoadLibraryExW(coreclr_path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (coreclr == nullptr) {
        auto message = L"Lustral cannot start: the .NET runtime is missing or damaged.\n\n" + coreclr_path +
                       L"\n\nReinstall Lustral, or delete winhttp.dll from the game folder to play without mods.";
        report_fatal(message.c_str());
        return false;
    }
    log::writef(log::Level::debug, L"Runtime: %ls", framework.c_str());

    auto initialize = reinterpret_cast<coreclr_initialize_fn>(GetProcAddress(coreclr, "coreclr_initialize"));
    auto create_delegate = reinterpret_cast<coreclr_create_delegate_fn>(GetProcAddress(coreclr, "coreclr_create_delegate"));
    if (initialize == nullptr || create_delegate == nullptr) {
        report_fatal((L"Lustral cannot start: " + coreclr_path + L" is not a .NET runtime.").c_str());
        return false;
    }

    // The framework is listed first, so that if Lustral\core contains a copy of a framework assembly, the framework's
    // copy takes precedence.
    std::string tpa;
    add_managed_assemblies(framework, tpa);
    add_managed_assemblies(paths.core_directory, tpa);
    auto core = to_utf8(paths.core_directory);
    auto native_directories = to_utf8(framework) + ";" + core;
    auto base = core + "\\";
    const char* keys[] = {
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
        "System.GC.Server",
        "System.GC.Concurrent",
    };
    const char* values[] = {
        tpa.c_str(),
        core.c_str(),
        native_directories.c_str(),
        base.c_str(),
        "win-x64",
        "false", // Workstation GC: a single heap and no server GC threads, to minimize the impact on the game.
        "true",
    };

    void* host = nullptr;
    unsigned int domain = 0;
    auto executable = to_utf8(paths.executable);
    int result = initialize(executable.c_str(), "Lustral", static_cast<int>(std::size(keys)), keys, values, &host, &domain);
    if (result < 0) {
        wchar_t message[512];
        swprintf(message, 512, L"Lustral cannot start: the .NET runtime failed to initialize (0x%08X).", static_cast<unsigned>(result));
        report_fatal(message);
        return false;
    }
    log::write(log::Level::info, L"CoreCLR started");

    void* entry = nullptr;
    result = create_delegate(host, domain, "Lustral.Host", "Lustral.Host.NativeEntry", "Start", &entry);
    if (result < 0 || entry == nullptr) {
        wchar_t message[512];
        swprintf(message, 512, L"Lustral cannot start: Lustral.Host.NativeEntry.Start was not found (0x%08X).", static_cast<unsigned>(result));
        report_fatal(message);
        return false;
    }

    g_api = NativeApi{sizeof(NativeApi), NativeApi::current_version, paths.executable.c_str(), paths.core_directory.c_str(), &log_from_managed};
    int status = reinterpret_cast<entry_fn>(entry)(&g_api);
    log::writef(status == 0 ? log::Level::info : log::Level::error, L"Lustral.Host returned %d", status);
    return status == 0;
}

} // namespace lustral
