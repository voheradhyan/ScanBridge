// rs_log.h — minimal structured logger for the native components.
//
// The data source and the DVC plugin are loaded into third-party processes (Acrobat,
// mstsc.exe), so they cannot take a .NET or third-party dependency. This writes the same
// field names Serilog uses on the managed side so the diagnostics report can merge both.
//
// Rule enforced by construction: this logger has no API that accepts pixel data.

#ifndef RS_LOG_H
#define RS_LOG_H

#include <windows.h>
#include <shlobj.h>
#include <cstdio>
#include <cstdarg>
#include <share.h>      // _SH_DENYNO — logs must stay readable while being written
#include <string>
#include <mutex>

namespace rs {

enum class LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Off = 5 };

class Log {
public:
    static Log& instance() {
        static Log log;
        return log;
    }

    // component is a short tag such as L"twainds" or L"dvcplugin"; it names the log file.
    void init(const wchar_t* component) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (file_) return;

        component_ = component;
        level_ = readLevelFromRegistry();
        if (level_ == LogLevel::Off) return;

        wchar_t programData[MAX_PATH]{};
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, 0, programData))) return;

        std::wstring dir = std::wstring(programData) + L"\\RemoteScanner\\logs";
        createDirectoryTree(dir);

        wchar_t path[MAX_PATH]{};
        _snwprintf_s(path, _TRUNCATE, L"%s\\%s-%lu.log", dir.c_str(), component, GetCurrentProcessId());
        // _wfsopen with _SH_DENYNO, not _wfopen_s: the latter opens the file with no sharing
        // at all, so the log of a *running* process cannot be read or copied by anything —
        // including COLLECT-LOGS.bat, which silently omitted it from the zip. The one log that
        // matters most during a fault is the one being written at that moment, and it was the
        // only one that could never be collected.
        file_ = _wfsopen(path, L"a, ccs=UTF-8", _SH_DENYNO);
    }

    void write(LogLevel level, const char* format, ...) {
        if (level < level_) return;

        std::lock_guard<std::mutex> lock(mutex_);
        if (!file_) return;

        SYSTEMTIME t{};
        GetLocalTime(&t);
        fwprintf(file_, L"%04d-%02d-%02d %02d:%02d:%02d.%03d [%s] [%lu] ",
                 t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds,
                 levelName(level), GetCurrentThreadId());

        va_list args;
        va_start(args, format);
        char buffer[1024];
        _vsnprintf_s(buffer, _TRUNCATE, format, args);
        va_end(args);

        fwprintf(file_, L"%S\n", buffer);
        fflush(file_);
    }

    LogLevel level() const { return level_; }

    ~Log() {
        if (file_) fclose(file_);
    }

private:
    Log() = default;
    Log(const Log&) = delete;
    Log& operator=(const Log&) = delete;

    static const wchar_t* levelName(LogLevel level) {
        switch (level) {
            case LogLevel::Trace: return L"TRC";
            case LogLevel::Debug: return L"DBG";
            case LogLevel::Info:  return L"INF";
            case LogLevel::Warn:  return L"WRN";
            case LogLevel::Error: return L"ERR";
            default: return L"???";
        }
    }

    // Shares SOFTWARE\RemoteScanner\LogLevel with the managed side so one setting controls the
    // whole stack.
    //
    // HKCU is consulted first and HKLM second. On a server the setting belongs in HKLM, where
    // an administrator sets it once for every session. On a user's own PC there is no
    // administrator: the client installs per-user, and the DVC plugin runs inside mstsc.exe as
    // that user — so requiring HKLM would make it impossible to raise the log level on exactly
    // the machine whose logs are needed, without calling someone with admin rights.
    static LogLevel readLevelFromRegistry() {
        LogLevel result = LogLevel::Info;
        if (readLevelFrom(HKEY_CURRENT_USER, result)) return result;
        readLevelFrom(HKEY_LOCAL_MACHINE, result);
        return result;
    }

    /// Returns true when the key held a value we understood, so the caller knows to stop.
    static bool readLevelFrom(HKEY root, LogLevel& result) {
        HKEY key{};
        if (RegOpenKeyExW(root, L"SOFTWARE\\RemoteScanner", 0, KEY_READ, &key) != ERROR_SUCCESS)
            return false;

        wchar_t value[32]{};
        DWORD size = sizeof(value);
        DWORD type = 0;
        bool found = false;

        if (RegQueryValueExW(key, L"LogLevel", nullptr, &type, reinterpret_cast<LPBYTE>(value), &size) == ERROR_SUCCESS
            && type == REG_SZ) {
            found = true;
            if (!_wcsicmp(value, L"Trace")) result = LogLevel::Trace;
            else if (!_wcsicmp(value, L"Debug")) result = LogLevel::Debug;
            else if (!_wcsicmp(value, L"Information")) result = LogLevel::Info;
            else if (!_wcsicmp(value, L"Warning")) result = LogLevel::Warn;
            else if (!_wcsicmp(value, L"Error")) result = LogLevel::Error;
            else if (!_wcsicmp(value, L"Off")) result = LogLevel::Off;
            else found = false;
        }
        RegCloseKey(key);
        return found;
    }

    static void createDirectoryTree(const std::wstring& path) {
        for (size_t i = 3; i <= path.size(); ++i) {
            if (i == path.size() || path[i] == L'\\') {
                std::wstring part = path.substr(0, i);
                CreateDirectoryW(part.c_str(), nullptr);
            }
        }
    }

    std::mutex mutex_;
    FILE* file_ = nullptr;
    LogLevel level_ = LogLevel::Info;
    std::wstring component_;
};

}  // namespace rs

#define RS_LOG_TRACE(...) ::rs::Log::instance().write(::rs::LogLevel::Trace, __VA_ARGS__)
#define RS_LOG_DEBUG(...) ::rs::Log::instance().write(::rs::LogLevel::Debug, __VA_ARGS__)
#define RS_LOG_INFO(...)  ::rs::Log::instance().write(::rs::LogLevel::Info,  __VA_ARGS__)
#define RS_LOG_WARN(...)  ::rs::Log::instance().write(::rs::LogLevel::Warn,  __VA_ARGS__)
#define RS_LOG_ERROR(...) ::rs::Log::instance().write(::rs::LogLevel::Error, __VA_ARGS__)

#endif  // RS_LOG_H
