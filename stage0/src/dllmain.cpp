// winhttp.dll proxy: all exports forward to System32\winhttp.dll, and process attach is handed over to stage 0.
#include "forwarders_winhttp.h"
#include "stage0.h"

#include <windows.h>

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(self);
        lustral::on_process_attach();
    }
    return TRUE;
}
