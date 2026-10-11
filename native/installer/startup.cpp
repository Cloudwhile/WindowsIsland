#include <windows.h>
#include <msiquery.h>
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
