#pragma once
#include <windows.h>
#include <cstdint>
#include <string>
#include <vector>

constexpr uint32_t Magic = 0x57495447;
struct HookInit {
    uint32_t magic;
    uint32_t version;
    uint32_t fixture;
    uint32_t ownerPid;
    wchar_t pipe[256];
};
struct PacketHeader {
    uint32_t magic = Magic;
    uint32_t version = 2;
    uint32_t kind = 1;
    uint32_t processId = 0;
    uint64_t objectId = 0;
    uint64_t sequence = 0;
    uint64_t fileTime = 0;
    uint32_t titleLength = 0;
    uint32_t bodyLength = 0;
    uint32_t avatarLength = 0;
    uint32_t reserved = 0;
};
static_assert(sizeof(PacketHeader) == 56);
struct CaptureProfile {
    void* update = nullptr;
    void* paintText = nullptr;
    uintptr_t vtable = 0;
    size_t titleOffset = 0x120;
    size_t bodyOffset = 0x180;
    const int* photoX = nullptr;
    const int* photoY = nullptr;
    const int* photoSize = nullptr;
    const int* pixelRatio = nullptr;
    const bool* rtl = nullptr;
};
bool ResolveProfile(bool fixture, CaptureProfile& profile);
bool InstallCapture(const CaptureProfile& profile);
bool FirstNotification(void* object) noexcept;
void RemoveCapture();
std::vector<unsigned char> ReadAvatar(void* object, const CaptureProfile& profile);
void QueueMessage(uintptr_t object, std::wstring title, std::wstring body, std::vector<unsigned char> avatar) noexcept;
