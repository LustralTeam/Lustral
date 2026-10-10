#include "pe.h"

#include <cstring>

namespace lustral::pe {

int patch_import(HMODULE module, const char* function, void* replacement)
{
    auto base = reinterpret_cast<BYTE*>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return 0;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return 0;

    const auto& directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (directory.VirtualAddress == 0)
        return 0;

    int patched = 0;
    for (auto descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
         descriptor->Name != 0; ++descriptor) {
        // Without the name table (OriginalFirstThunk), imports cannot be matched by name, because the loader has already
        // overwritten FirstThunk with addresses.
        if (descriptor->OriginalFirstThunk == 0)
            continue;

        auto names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + descriptor->OriginalFirstThunk);
        auto slots = reinterpret_cast<IMAGE_THUNK_DATA*>(base + descriptor->FirstThunk);
        for (; names->u1.AddressOfData != 0; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
                continue;
            auto by_name = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(by_name->Name), function) != 0)
                continue;

            DWORD protection = 0;
            if (!VirtualProtect(&slots->u1.Function, sizeof(slots->u1.Function), PAGE_READWRITE, &protection))
                continue;
            slots->u1.Function = reinterpret_cast<ULONG_PTR>(replacement);
            VirtualProtect(&slots->u1.Function, sizeof(slots->u1.Function), protection, &protection);
            ++patched;
        }
    }
    return patched;
}

bool is_managed_assembly(const std::wstring& path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return false;

    BYTE header[4096] = {};
    DWORD read = 0;
    ReadFile(file, header, sizeof(header), &read, nullptr);
    CloseHandle(file);
    if (read < sizeof(IMAGE_DOS_HEADER))
        return false;

    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(header);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 ||
        static_cast<DWORD>(dos->e_lfanew) + sizeof(IMAGE_NT_HEADERS64) > read)
        return false;

    auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(header + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return false;

    // The CLR header's data directory entry is at different offsets in PE32 and PE32+ optional headers.
    if (nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC)
        return nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR].VirtualAddress != 0;
    auto nt32 = reinterpret_cast<const IMAGE_NT_HEADERS32*>(nt);
    return nt32->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR].VirtualAddress != 0;
}

} // namespace lustral::pe
