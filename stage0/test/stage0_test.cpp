// Tests stage 0 without launching a game.
//
//   stage0_test forwarders <proxy.dll>    every export of the proxy resolves to the same function in System32, and the
//                                         proxy remains inactive in a process without UnityPlayer.dll
//   stage0_test coreclr <fake game.exe>   starts the runtime in <game>\Lustral\dotnet and calls Lustral.Host in
//                                         <game>\Lustral\core, which logs to <game>\Lustral\Logs; with no GameAssembly.dll it stops after the backend check
//   stage0_test arguments                 finds --no-mods in sample command lines
//   stage0_test packages                  recognizes the other scripting backend's package in a temporary game folder
#include "log.h"
#include "stage0.h"

#include <windows.h>

#include <cstdio>
#include <cwchar>
#include <string>

namespace {

int fail(const wchar_t* message)
{
    std::fwprintf(stderr, L"FAIL: %ls\n", message);
    return 1;
}

int test_forwarders(const wchar_t* proxy_path)
{
    HMODULE proxy = LoadLibraryW(proxy_path);
    if (proxy == nullptr)
        return fail(L"the proxy didn't load");

    wchar_t name[MAX_PATH];
    GetModuleFileNameW(proxy, name, MAX_PATH);
    std::wstring proxy_name = name;
    std::wstring system_path(MAX_PATH, L'\0');
    system_path.resize(GetSystemDirectoryW(system_path.data(), MAX_PATH));
    system_path += proxy_name.substr(proxy_name.find_last_of(L'\\'));
    HMODULE real = LoadLibraryW(system_path.c_str());
    if (real == nullptr || real == proxy)
        return fail(L"couldn't load the System32 DLL next to the proxy");

    auto base = reinterpret_cast<BYTE*>(proxy);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
    auto exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
    auto names = reinterpret_cast<DWORD*>(base + exports->AddressOfNames);

    // The forwarders are generated from a recent Windows version. Older versions, such as GitHub's Windows Server runners,
    // lack some of the newest exports; those resolve through neither the proxy nor System32, as a program would expect.
    int checked = 0, absent = 0;
    for (DWORD i = 0; i < exports->NumberOfNames; ++i) {
        auto export_name = reinterpret_cast<const char*>(base + names[i]);
        FARPROC through_proxy = GetProcAddress(proxy, export_name);
        FARPROC direct = GetProcAddress(real, export_name);
        if (through_proxy == nullptr && direct == nullptr) {
            ++absent;
            continue;
        }
        if (through_proxy != direct) {
            std::fwprintf(stderr, L"FAIL: %hs resolves to %p through the proxy, %p directly\n", export_name,
                          reinterpret_cast<void*>(through_proxy), reinterpret_cast<void*>(direct));
            return 1;
        }
        ++checked;
    }

    std::wprintf(L"OK: %d exports forward to %ls", checked, system_path.c_str());
    if (absent > 0)
        std::wprintf(L"; %d more are not in this version of Windows", absent);
    std::wprintf(L"\n");
    return 0;
}

int test_arguments()
{
    struct Case {
        const wchar_t* command_line;
        bool expected;
    };
    const Case cases[] = {
        {L"\"C:\\Games\\My Game\\Game.exe\" --no-mods", true},
        {L"Game.exe -screen-width 640 --NO-MODS -screen-height 360", true},
        {L"Game.exe\t\"--no-mods\"", true},
        {L"Game.exe", false},
        {L"Game.exe --no-mods-please", false},
        {L"Game.exe -no-mods", false},
        {L"\"C:\\--no-mods games\\Game.exe\" -batchmode", false},
        {L"Game.exe \"--no-mods and more\"", false},
    };
    for (const Case& test : cases) {
        if (lustral::command_line_has(test.command_line, lustral::disable_argument) != test.expected) {
            std::fwprintf(stderr, L"FAIL: expected %ls for: %ls\n", test.expected ? L"true" : L"false", test.command_line);
            return 1;
        }
    }
    std::wprintf(L"OK: %zu command lines\n", sizeof(cases) / sizeof(cases[0]));
    return 0;
}

void create_file(const std::wstring& path)
{
    CloseHandle(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
}

int test_packages()
{
    SetEnvironmentVariableW(L"LUSTRAL_NO_MESSAGE_BOX", L"1");
    std::wstring temp(MAX_PATH, L'\0');
    temp.resize(GetTempPathW(MAX_PATH, temp.data()));
    auto game = temp + L"lustral-stage0-test-" + std::to_wstring(GetCurrentProcessId());
    auto paths = lustral::paths_for_executable(game + L"\\Game.exe");
    CreateDirectoryW(game.c_str(), nullptr);
    CreateDirectoryW(paths.lustral_directory.c_str(), nullptr);
    CreateDirectoryW(paths.core_directory.c_str(), nullptr);
    auto il2cpp_host = paths.core_directory + L"\\Lustral.Host.dll";
    auto mono_host = paths.core_directory + L"\\Lustral.Host.Mono.dll";

    struct Case {
        const wchar_t* installed;
        bool il2cpp_game;
        bool expected;
    };
    const Case cases[] = {
        {L"nothing", true, false},
        {L"nothing", false, false},
        {L"mono", true, true},
        {L"mono", false, false},
        {L"il2cpp", true, false},
        {L"il2cpp", false, true},
    };
    int result = 0;
    for (const Case& test : cases) {
        DeleteFileW(il2cpp_host.c_str());
        DeleteFileW(mono_host.c_str());
        if (std::wcscmp(test.installed, L"mono") == 0)
            create_file(mono_host);
        if (std::wcscmp(test.installed, L"il2cpp") == 0)
            create_file(il2cpp_host);
        if (lustral::report_wrong_package(paths, test.il2cpp_game) != test.expected) {
            std::fwprintf(stderr, L"FAIL: expected %ls for the %ls package in %ls game\n", test.expected ? L"true" : L"false",
                          test.installed, test.il2cpp_game ? L"an IL2CPP" : L"a Mono");
            result = 1;
            break;
        }
    }
    DeleteFileW(il2cpp_host.c_str());
    DeleteFileW(mono_host.c_str());
    RemoveDirectoryW(paths.core_directory.c_str());
    RemoveDirectoryW(paths.lustral_directory.c_str());
    RemoveDirectoryW(game.c_str());
    if (result == 0)
        std::wprintf(L"OK: %zu installations\n", sizeof(cases) / sizeof(cases[0]));
    return result;
}

int test_coreclr(const wchar_t* executable)
{
    SetEnvironmentVariableW(L"LUSTRAL_NO_MESSAGE_BOX", L"1");
    auto paths = lustral::paths_for_executable(executable);
    lustral::log::open(paths.logs_directory);
    bool started = lustral::start_coreclr(paths);
    std::wprintf(L"%ls: see %ls\\stage0.log and latest.log\n", started ? L"OK" : L"FAIL", paths.logs_directory.c_str());
    return started ? 0 : 1;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && std::wcscmp(argv[1], L"forwarders") == 0)
        return test_forwarders(argv[2]);
    if (argc == 3 && std::wcscmp(argv[1], L"coreclr") == 0)
        return test_coreclr(argv[2]);
    if (argc == 2 && std::wcscmp(argv[1], L"arguments") == 0)
        return test_arguments();
    if (argc == 2 && std::wcscmp(argv[1], L"packages") == 0)
        return test_packages();
    std::fwprintf(stderr, L"Usage: stage0_test forwarders <proxy.dll> | coreclr <fake game.exe> | arguments | packages\n");
    return 2;
}
