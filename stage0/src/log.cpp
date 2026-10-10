#include "log.h"

#include "stage0.h"

#include <windows.h>

#include <cstdarg>
#include <cwchar>

namespace lustral::log {
namespace {

SRWLOCK g_lock = SRWLOCK_INIT;
HANDLE g_file = INVALID_HANDLE_VALUE;

const wchar_t* level_name(Level level)
{
    switch (level) {
    case Level::debug: return L"Debug";
    case Level::info: return L"Info ";
    case Level::warn: return L"Warn ";
    default: return L"Error";
    }
}

} // namespace

void open(const std::wstring& logs_directory)
{
    AcquireSRWLockExclusive(&g_lock);
    if (g_file == INVALID_HANDLE_VALUE) {
        CreateDirectoryW(logs_directory.c_str(), nullptr);
        auto path = logs_directory + L"\\stage0.log";
        g_file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                             CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

void write(Level level, std::wstring_view message)
{
    SYSTEMTIME time;
    GetLocalTime(&time);
    wchar_t prefix[64];
    swprintf(prefix, 64, L"[%02u:%02u:%02u.%03u] [%ls] [stage0] ", time.wHour, time.wMinute, time.wSecond,
             time.wMilliseconds, level_name(level));

    auto line = to_utf8(std::wstring(prefix) + std::wstring(message) + L"\r\n");
    OutputDebugStringA(line.c_str());

    AcquireSRWLockExclusive(&g_lock);
    if (g_file != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(g_file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

void writef(Level level, const wchar_t* format, ...)
{
    wchar_t buffer[2048];
    va_list args;
    va_start(args, format);
    vswprintf(buffer, 2048, format, args);
    va_end(args);
    write(level, buffer);
}

} // namespace lustral::log
