#include "bridge.h"
#include <cstring>

bool ResolveProfile(bool fixture, CaptureProfile& profile) {
    auto module = GetModuleHandleW(nullptr);
    wchar_t path[MAX_PATH] = {};
    if (!GetModuleFileNameW(module, path, MAX_PATH)) return false;
    auto name = wcsrchr(path, L'\\');
    name = name ? name + 1 : path;
    if (fixture) {
        if (_wcsicmp(name, L"WindowsIsland.TelegramFixture.exe")) return false;
        profile.update = reinterpret_cast<void*>(GetProcAddress(module, "FixtureUpdate"));
        profile.paintText = reinterpret_cast<void*>(GetProcAddress(module, "FixturePaintText"));
        profile.vtable = 0x5447464958545552;
        return profile.update && profile.paintText;
    }
    if (_wcsicmp(name, L"Telegram.exe")) return false;
    auto base = reinterpret_cast<unsigned char*>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    // Telegram Desktop 7.2.9 x64. Host additionally verifies the complete executable hash.
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64
        || nt->FileHeader.TimeDateStamp != 0x6aab9a36 || nt->OptionalHeader.SizeOfImage != 0xe430000) return false;
    constexpr unsigned char updateBytes[] = {0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x48,0x89,0x7c,0x24,0x20,0x55};
    constexpr unsigned char paintBytes[] = {0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x48,0x8d,0x6c,0x24,0xb9,0x48,0x81,0xec};
    if (memcmp(base + 0x2b7f020, updateBytes, sizeof(updateBytes))
        || memcmp(base + 0x2b7ee30, paintBytes, sizeof(paintBytes))) return false;
    profile.update = base + 0x2b7f020;
    profile.paintText = base + 0x2b7ee30;
    profile.vtable = reinterpret_cast<uintptr_t>(base + 0x7cb2bf8);
    profile.photoX = reinterpret_cast<const int*>(base + 0xddfd7b8);
    profile.photoY = reinterpret_cast<const int*>(base + 0xddfd7bc);
    profile.photoSize = reinterpret_cast<const int*>(base + 0xddfd7a8);
    profile.pixelRatio = reinterpret_cast<const int*>(base + 0x8e41d48);
    profile.rtl = reinterpret_cast<const bool*>(base + 0xddf5b07);
    return true;
}
