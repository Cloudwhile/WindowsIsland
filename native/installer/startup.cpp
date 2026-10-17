#include <windows.h>
#include <tlhelp32.h>
#include <msiquery.h>
#include <algorithm>
#include <filesystem>
#include <string>
#include <vector>
#include <stdexcept>

namespace {
constexpr auto RunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr auto ApprovalKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
constexpr auto ValueName = L"WindowsIsland";
struct Key {
    HKEY value = nullptr;
    ~Key() { if (value) RegCloseKey(value); }
};
struct Handle {
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(other.value) { other.value = nullptr; }
    Handle& operator=(Handle&& other) noexcept {
        if (this != &other) {
            if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
            value = other.value;
            other.value = nullptr;
        }
        return *this;
    }
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct ApplicationProcess {
    DWORD id;
    Handle handle;
};
void Require(bool success) { if (!success) throw std::runtime_error("Startup migration failed"); }

std::wstring Property(MSIHANDLE session, const wchar_t* name) {
    DWORD size = 0;
    wchar_t empty = 0;
    auto result = MsiGetPropertyW(session, name, &empty, &size);
    Require((result == ERROR_MORE_DATA || result == ERROR_SUCCESS) && size <= 8192);
    std::wstring value(size + 1, L'\0');
    ++size;
    Require(MsiGetPropertyW(session, name, value.data(), &size) == ERROR_SUCCESS);
    value.resize(size);
    return value;
}

bool Read(HKEY user, const wchar_t* path, DWORD& type, std::vector<BYTE>& data) {
    Key key;
    auto result = RegOpenKeyExW(user, path, 0, KEY_QUERY_VALUE, &key.value);
    if (result == ERROR_FILE_NOT_FOUND) return false;
    Require(result == ERROR_SUCCESS);
    DWORD size = 0;
    result = RegQueryValueExW(key.value, ValueName, nullptr, &type, nullptr, &size);
    if (result == ERROR_FILE_NOT_FOUND) return false;
    Require(result == ERROR_SUCCESS && size <= 4096);
    data.resize(size);
    Require(RegQueryValueExW(key.value, ValueName, nullptr, &type, data.data(), &size) == ERROR_SUCCESS);
    data.resize(size);
    return true;
}

std::wstring Command(const std::vector<BYTE>& data, DWORD type) {
    if (type != REG_SZ || data.size() < sizeof(wchar_t) || data.size() % sizeof(wchar_t)) return {};
    std::wstring value(data.size() / sizeof(wchar_t), L'\0');
    memcpy(value.data(), data.data(), data.size());
    if (value.back() != L'\0') return {};
    value.pop_back();
    if (value.find_first_of(L"\r\n") != std::wstring::npos || value.find(L'\0') != std::wstring::npos) return {};
    return value;
}

void Write(HKEY user, const wchar_t* path, DWORD type, const BYTE* bytes, DWORD size) {
    Key key;
    Require(RegCreateKeyExW(user, path, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key.value, nullptr) == ERROR_SUCCESS);
    Require(RegSetValueExW(key.value, ValueName, 0, type, bytes, size) == ERROR_SUCCESS);
}

UINT Failed(MSIHANDLE session) {
    auto record = MsiCreateRecord(1);
    if (record) {
        MsiRecordSetStringW(record, 0, L"Windows Island could not preserve the existing startup preference.");
        MsiProcessMessage(session, INSTALLMESSAGE_ERROR, record);
        MsiCloseHandle(record);
    }
    return ERROR_INSTALL_FAILURE;
}

UINT DirectoryCheckFailed(MSIHANDLE session) {
    auto record = MsiCreateRecord(1);
    if (record) {
        MsiRecordSetStringW(record, 0, L"Windows Island could not verify the selected installation directory.");
        MsiProcessMessage(session, INSTALLMESSAGE_ERROR, record);
        MsiCloseHandle(record);
    }
    return ERROR_INSTALL_FAILURE;
}

UINT CloseFailed(MSIHANDLE session) {
    auto record = MsiCreateRecord(1);
    if (record) {
        MsiRecordSetStringW(record, 0, L"Windows Island could not close the running application. Exit it and try again.");
        MsiProcessMessage(session, INSTALLMESSAGE_ERROR, record);
        MsiCloseHandle(record);
    }
    return ERROR_INSTALL_FAILURE;
}

std::wstring FullPath(const std::wstring& path) {
    auto required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    Require(required > 0 && required <= 32768);
    std::wstring result(required, L'\0');
    auto length = GetFullPathNameW(path.c_str(), required, result.data(), nullptr);
    Require(length > 0 && length < required);
    result.resize(length);
    return result;
}

std::vector<ApplicationProcess> FindApplicationProcesses(const std::wstring& executable) {
    std::vector<ApplicationProcess> result;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    Require(snapshot.value != INVALID_HANDLE_VALUE);
    PROCESSENTRY32W entry{ sizeof(entry) };
    if (!Process32FirstW(snapshot.value, &entry)) {
        Require(GetLastError() == ERROR_NO_MORE_FILES);
        return result;
    }
    do {
        if (_wcsicmp(entry.szExeFile, L"WindowsIsland.exe") != 0) continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
            FALSE, entry.th32ProcessID));
        if (!process.value) continue;
        std::wstring path(32768, L'\0');
        DWORD length = static_cast<DWORD>(path.size());
        if (!QueryFullProcessImageNameW(process.value, 0, path.data(), &length)) continue;
        path.resize(length);
        if (_wcsicmp(path.c_str(), executable.c_str()) == 0)
            result.push_back({ entry.th32ProcessID, std::move(process) });
    } while (Process32NextW(snapshot.value, &entry));
    Require(GetLastError() == ERROR_NO_MORE_FILES);
    return result;
}

BOOL CALLBACK RequestApplicationExit(HWND window, LPARAM data) {
    auto& processes = *reinterpret_cast<std::vector<ApplicationProcess>*>(data);
    DWORD owner = 0;
    GetWindowThreadProcessId(window, &owner);
    if (std::ranges::any_of(processes, [owner](const auto& process) { return process.id == owner; }))
        PostMessageW(window, RegisterWindowMessageW(L"WindowsIsland.ExitForMaintenance"), 0, 0);
    return TRUE;
}

bool WaitForExit(const std::vector<ApplicationProcess>& processes, DWORD timeout) {
    auto deadline = GetTickCount64() + timeout;
    for (;;) {
        auto running = std::ranges::any_of(processes, [](const auto& process) {
            auto state = WaitForSingleObject(process.handle.value, 0);
            Require(state == WAIT_OBJECT_0 || state == WAIT_TIMEOUT);
            return state == WAIT_TIMEOUT;
        });
        if (!running) return true;
        if (GetTickCount64() >= deadline) return false;
        Sleep(100);
    }
}

bool TerminateApplication(const ApplicationProcess& application) {
    if (WaitForSingleObject(application.handle.value, 0) == WAIT_OBJECT_0) return true;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE, FALSE, application.id));
    if (!process.value) return WaitForSingleObject(application.handle.value, 0) == WAIT_OBJECT_0;
    FILETIME expected{}, actual{}, exit{}, kernel{}, user{};
    Require(GetProcessTimes(application.handle.value, &expected, &exit, &kernel, &user)
        && GetProcessTimes(process.value, &actual, &exit, &kernel, &user));
    if (CompareFileTime(&expected, &actual) != 0)
        return WaitForSingleObject(application.handle.value, 0) == WAIT_OBJECT_0;
    return TerminateProcess(process.value, ERROR_INSTALL_FAILURE)
        || WaitForSingleObject(application.handle.value, 0) == WAIT_OBJECT_0;
}

DWORD ProbeWriteAccess(const std::wstring& directory) {
    std::filesystem::path candidate(directory);
    while (!candidate.empty()) {
        auto attributes = GetFileAttributesW(candidate.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES) {
            if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) return ERROR_DIRECTORY;
            break;
        }
        auto error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return error;
        auto parent = candidate.parent_path();
        if (parent == candidate) return error;
        candidate = std::move(parent);
    }
    if (candidate.empty()) return ERROR_PATH_NOT_FOUND;

    auto targetExists = _wcsicmp(candidate.c_str(), std::filesystem::path(directory).c_str()) == 0;
    for (unsigned attempt = 0; attempt != 8; ++attempt) {
        auto name = L".windows-island-write-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
            std::to_wstring(GetTickCount64()) + L"-" + std::to_wstring(attempt);
        auto probe = candidate / name;
        if (targetExists) {
            auto file = CreateFileW(probe.c_str(), GENERIC_WRITE | DELETE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_NEW,
                FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
            if (file != INVALID_HANDLE_VALUE) {
                CloseHandle(file);
                return ERROR_SUCCESS;
            }
        } else if (CreateDirectoryW(probe.c_str(), nullptr)) {
            auto removed = RemoveDirectoryW(probe.c_str());
            return removed ? ERROR_SUCCESS : GetLastError();
        }
        auto error = GetLastError();
        if (error != ERROR_FILE_EXISTS && error != ERROR_ALREADY_EXISTS) return error;
    }
    return ERROR_ALREADY_EXISTS;
}
}

extern "C" __declspec(dllexport) UINT __stdcall SelectInstallScope(MSIHANDLE session) {
    try {
        auto directory = Property(session, L"INSTALLFOLDER");
        Require(!directory.empty());
        auto result = ProbeWriteAccess(directory);
        auto needsElevation = result == ERROR_ACCESS_DENIED || result == ERROR_PRIVILEGE_NOT_HELD;
        Require(result == ERROR_SUCCESS || needsElevation);
        Require(MsiSetPropertyW(session, L"ALLUSERS", needsElevation ? L"1" : L"2") == ERROR_SUCCESS);
        Require(MsiSetPropertyW(session, L"MSIINSTALLPERUSER", needsElevation ? L"" : L"1") == ERROR_SUCCESS);
        return ERROR_SUCCESS;
    } catch (...) { return DirectoryCheckFailed(session); }
}

extern "C" __declspec(dllexport) UINT __stdcall CloseApplication(MSIHANDLE session) {
    try {
        auto directory = Property(session, L"INSTALLFOLDER");
        Require(!directory.empty());
        auto executable = FullPath((std::filesystem::path(directory) / L"WindowsIsland.exe").wstring());
        auto processes = FindApplicationProcesses(executable);
        if (processes.empty()) return ERROR_SUCCESS;
        EnumWindows(RequestApplicationExit, reinterpret_cast<LPARAM>(&processes));
        if (WaitForExit(processes, 10000)) return ERROR_SUCCESS;
        for (const auto& process : processes) {
            if (!TerminateApplication(process)) return CloseFailed(session);
        }
        return WaitForExit(processes, 5000) ? ERROR_SUCCESS : CloseFailed(session);
    } catch (...) { return CloseFailed(session); }
}

// Capture before the old MSI runs its uninstall script. Even old release candidates
// delete this preference, so fixing only the new uninstaller cannot protect upgrades.
extern "C" __declspec(dllexport) UINT __stdcall CaptureStartup(MSIHANDLE session) {
    try {
        Require(MsiSetPropertyW(session, L"RestoreStartup", L"") == ERROR_SUCCESS);
        auto directory = Property(session, L"INSTALLFOLDER");
        if (directory.empty()) return ERROR_SUCCESS;
        if (directory.back() != L'\\') directory += L'\\';
        auto expected = L"\"" + directory + L"WindowsIsland.exe\" --startup";
        Key user;
        Require(RegOpenCurrentUser(KEY_READ, &user.value) == ERROR_SUCCESS);
        DWORD type = 0;
        std::vector<BYTE> bytes;
        if (!Read(user.value, RunKey, type, bytes)) return ERROR_SUCCESS;
        auto command = Command(bytes, type);
        if (command.empty() || _wcsicmp(command.c_str(), expected.c_str()) != 0) return ERROR_SUCCESS;
        auto state = command + L"\n";
        if (Read(user.value, ApprovalKey, type, bytes)) {
            Require(type == REG_BINARY);
            constexpr wchar_t hex[] = L"0123456789ABCDEF";
            for (auto byte : bytes) { state += hex[byte >> 4]; state += hex[byte & 15]; }
        } else state += L"-";
        Require(MsiSetPropertyW(session, L"RestoreStartup", state.c_str()) == ERROR_SUCCESS);
        return ERROR_SUCCESS;
    } catch (...) { return Failed(session); }
}

extern "C" __declspec(dllexport) UINT __stdcall RestoreStartup(MSIHANDLE session) {
    try {
        auto state = Property(session, L"RestoreStartup");
        auto separator = state.find(L'\n');
        Require(separator != std::wstring::npos && separator <= 260);
        auto command = state.substr(0, separator);
        const std::wstring suffix = L"\\WindowsIsland.exe\" --startup";
        Require(command.starts_with(L"\"") && (command.size() >= suffix.size() && _wcsicmp(command.c_str() + command.size() - suffix.size(), suffix.c_str()) == 0)
            && command.find(L'\r') == std::wstring::npos);
        auto approval = state.substr(separator + 1);
        std::vector<BYTE> bytes;
        if (approval != L"-") {
            Require(approval.size() % 2 == 0);
            auto digit = [](wchar_t c) -> BYTE {
                Require(c >= L'0' && c <= L'9' || c >= L'A' && c <= L'F');
                return static_cast<BYTE>(c <= L'9' ? c - L'0' : c - L'A' + 10);
            };
            for (size_t i = 0; i < approval.size(); i += 2)
                bytes.push_back(static_cast<BYTE>((digit(approval[i]) << 4) | digit(approval[i + 1])));
        }
        Key user;
        Require(RegOpenCurrentUser(KEY_READ | KEY_WRITE, &user.value) == ERROR_SUCCESS);
        DWORD type = 0;
        std::vector<BYTE> current;
        // Do not overwrite a different installation that took ownership meanwhile.
        if (Read(user.value, RunKey, type, current)) {
            auto existing = Command(current, type);
            Require(!existing.empty() && _wcsicmp(existing.c_str(), command.c_str()) == 0);
        }
        if (approval != L"-") Write(user.value, ApprovalKey, REG_BINARY, bytes.data(), static_cast<DWORD>(bytes.size()));
        Write(user.value, RunKey, REG_SZ, reinterpret_cast<const BYTE*>(command.c_str()),
            static_cast<DWORD>((command.size() + 1) * sizeof(wchar_t)));
        return ERROR_SUCCESS;
    } catch (...) { return Failed(session); }
}
