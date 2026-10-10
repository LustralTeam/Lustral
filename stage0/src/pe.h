#pragma once

#include <string>

#include <windows.h>

namespace lustral::pe {

// Redirects all imports of `function` (by name, from any DLL) in `module`'s import table to `replacement`.
// Returns the number of patched entries.
int patch_import(HMODULE module, const char* function, void* replacement);

// Returns true if the file is a .NET assembly (has a CLR header) rather than a native DLL.
bool is_managed_assembly(const std::wstring& path);

} // namespace lustral::pe
