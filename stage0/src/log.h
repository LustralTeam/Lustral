#pragma once

#include <cstdint>
#include <string>
#include <string_view>

namespace lustral::log {

// The same values as Lustral.Core.Logging.LogLevel.
enum class Level : int32_t { debug = 0, info = 1, warn = 2, error = 3 };

// Creates Lustral\Logs\stage0.log, replacing the previous session's log. Can safely be called more than once.
void open(const std::wstring& logs_directory);

void write(Level level, std::wstring_view message);

// Writes a message with a printf-style wide format string.
void writef(Level level, const wchar_t* format, ...);

} // namespace lustral::log
