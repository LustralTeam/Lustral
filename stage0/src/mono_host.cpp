// Mono games: loads Lustral.Host.Mono into Unity's root domain and calls it, using Mono's embedding API.
#include "log.h"
#include "stage0.h"

#include <windows.h>

namespace lustral {
namespace {

using mono_thread_current_fn = void* (*)();
using mono_thread_set_main_fn = void (*)(void* thread);
using mono_domain_set_config_fn = void (*)(void* domain, const char* base_dir, const char* config_file_name);
using mono_config_parse_fn = void (*)(const char* filename);
using mono_domain_assembly_open_fn = void* (*)(void* domain, const char* name);
using mono_assembly_get_image_fn = void* (*)(void* assembly);
using mono_class_from_name_fn = void* (*)(void* image, const char* name_space, const char* name);
using mono_class_get_method_from_name_fn = void* (*)(void* klass, const char* name, int param_count);
using mono_runtime_invoke_fn = void* (*)(void* method, void* obj, void** params, void** exc);
using mono_object_to_string_fn = void* (*)(void* obj, void** exc);
using mono_string_to_utf8_fn = char* (*)(void* string);
using mono_free_fn = void (*)(void* pointer);

template <typename T>
T resolve(void* module, const char* name)
{
    return reinterpret_cast<T>(GetProcAddress(static_cast<HMODULE>(module), name));
}

std::wstring from_utf8(const char* text)
{
    int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    std::wstring result(static_cast<size_t>(length > 0 ? length - 1 : 0), L'\0');
    if (length > 1)
        MultiByteToWideChar(CP_UTF8, 0, text, -1, result.data(), length);
    return result;
}

} // namespace

bool start_mono(void* mono_module, void* domain, const Paths& paths)
{
    auto thread_current = resolve<mono_thread_current_fn>(mono_module, "mono_thread_current");
    auto thread_set_main = resolve<mono_thread_set_main_fn>(mono_module, "mono_thread_set_main");
    auto domain_set_config = resolve<mono_domain_set_config_fn>(mono_module, "mono_domain_set_config");
    auto config_parse = resolve<mono_config_parse_fn>(mono_module, "mono_config_parse");
    auto assembly_open = resolve<mono_domain_assembly_open_fn>(mono_module, "mono_domain_assembly_open");
    auto get_image = resolve<mono_assembly_get_image_fn>(mono_module, "mono_assembly_get_image");
    auto class_from_name = resolve<mono_class_from_name_fn>(mono_module, "mono_class_from_name");
    auto method_from_name = resolve<mono_class_get_method_from_name_fn>(mono_module, "mono_class_get_method_from_name");
    auto runtime_invoke = resolve<mono_runtime_invoke_fn>(mono_module, "mono_runtime_invoke");
    auto object_to_string = resolve<mono_object_to_string_fn>(mono_module, "mono_object_to_string");
    auto string_to_utf8 = resolve<mono_string_to_utf8_fn>(mono_module, "mono_string_to_utf8");
    auto mono_free = resolve<mono_free_fn>(mono_module, "mono_free");

    if (domain == nullptr || assembly_open == nullptr || get_image == nullptr || class_from_name == nullptr ||
        method_from_name == nullptr || runtime_invoke == nullptr) {
        log::write(log::Level::error, L"This Mono runtime lacks the embedding functions that Lustral requires");
        return false;
    }

    // Sets up what Unity would otherwise set up later: this thread as Mono's main thread, and the domain's base directory
    // and configuration, so that Lustral's startup code sees the same AppDomain as the game.
    if (thread_current != nullptr && thread_set_main != nullptr)
        thread_set_main(thread_current());
    if (domain_set_config != nullptr) {
        auto config = to_utf8(paths.executable + L".config");
        domain_set_config(domain, to_utf8(paths.game_directory).c_str(), config.c_str());
    }
    if (config_parse != nullptr)
        config_parse(nullptr);

    // Read by Lustral.Host.Mono, because Mono's embedding API cannot pass a struct as CoreCLR's can.
    SetEnvironmentVariableW(L"LUSTRAL_GAME_EXECUTABLE", paths.executable.c_str());
    SetEnvironmentVariableW(L"LUSTRAL_CORE_DIRECTORY", paths.core_directory.c_str());

    auto host_path = paths.core_directory + L"\\Lustral.Host.Mono.dll";
    if (GetFileAttributesW(host_path.c_str()) == INVALID_FILE_ATTRIBUTES) {
        if (report_wrong_package(paths, false))
            return false;
        report_fatal((L"Lustral cannot start: " + host_path + L" is missing. Reinstall Lustral, or delete winhttp.dll from the game folder to play without mods.").c_str());
        return false;
    }

    void* assembly = assembly_open(domain, to_utf8(host_path).c_str());
    void* image = assembly != nullptr ? get_image(assembly) : nullptr;
    void* klass = image != nullptr ? class_from_name(image, "Lustral.Host.Mono", "NativeEntry") : nullptr;
    void* method = klass != nullptr ? method_from_name(klass, "Start", 0) : nullptr;
    if (method == nullptr) {
        report_fatal((L"Lustral cannot start: Lustral.Host.Mono.NativeEntry.Start was not found in " + host_path).c_str());
        return false;
    }

    void* exception = nullptr;
    runtime_invoke(method, nullptr, nullptr, &exception);
    if (exception != nullptr) {
        std::wstring text = L"(no details)";
        if (object_to_string != nullptr && string_to_utf8 != nullptr) {
            void* inner = nullptr;
            void* string = object_to_string(exception, &inner);
            if (string != nullptr) {
                char* utf8 = string_to_utf8(string);
                text = from_utf8(utf8);
                if (mono_free != nullptr)
                    mono_free(utf8);
            }
        }
        log::write(log::Level::error, L"Lustral.Host.Mono threw: " + text);
        return false;
    }

    log::write(log::Level::info, L"Lustral.Host.Mono started");
    return true;
}

} // namespace lustral
