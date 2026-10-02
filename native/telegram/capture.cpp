#include "bridge.h"
#include <MinHook.h>
#include <array>
#include <atomic>

namespace {
using Update = void(__fastcall*)(void*);
using PaintText = void(__fastcall*)(void*, void*);
Update originalUpdate = nullptr;
PaintText originalPaintText = nullptr;
CaptureProfile profile;
std::atomic<unsigned int> calls = 0;
struct Call {
    Call() { ++calls; }
    ~Call() { --calls; }
};
struct Frame { void* object; std::wstring body; Frame* previous; };
thread_local Frame* current = nullptr;

// Qt 5 QString points at QArrayData: ref, size, allocation, alignment, data offset.
// Copy synchronously while Telegram owns the live string, before its cache is cleared.
bool CopyQString(void* object, size_t offset, wchar_t* result, int capacity, int& length) noexcept {
    __try {
        auto data = *reinterpret_cast<const unsigned char**>(static_cast<unsigned char*>(object) + offset);
        if (!data) return false;
        auto size = *reinterpret_cast<const int*>(data + 4);
        auto dataOffset = *reinterpret_cast<const intptr_t*>(data + 16);
        if (size < 0 || size > 65536 || dataOffset < 0 || dataOffset > 0x100000) return false;
        length = (std::min)(size, capacity - 1);
        memcpy(result, data + dataOffset, static_cast<size_t>(length) * sizeof(wchar_t));
        if (length && result[length - 1] >= 0xd800 && result[length - 1] <= 0xdbff) --length;
        result[length] = 0;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

std::wstring Read(void* object, size_t offset, int limit) {
    std::array<wchar_t, 4097> buffer{};
    int length = 0;
    return CopyQString(object, offset, buffer.data(), limit + 1, length) ? std::wstring(buffer.data(), length) : std::wstring();
}

bool IsNotification(void* object) noexcept {
    __try { return object && *reinterpret_cast<uintptr_t*>(object) == profile.vtable; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

void __fastcall HookPaintText(void* object, void* painter) {
    Call call;
    if (current && current->object == object) {
        try { current->body = Read(object, profile.bodyOffset, 4096); }
        catch (...) { }
    }
    originalPaintText(object, painter);
}

void __fastcall HookUpdate(void* object) {
    Call call;
    if (!IsNotification(object)) { originalUpdate(object); return; }
    Frame frame{object, {}, current};
    current = &frame;
    try { originalUpdate(object); }
    catch (...) { current = frame.previous; throw; }
    current = frame.previous;
    try {
        auto title = Read(object, profile.titleOffset, 1024);
        if (!title.empty() && FirstNotification(object)) QueueMessage(reinterpret_cast<uintptr_t>(object), std::move(title), std::move(frame.body), ReadAvatar(object, profile));
    }
    catch (...) { }
}
}

bool InstallCapture(const CaptureProfile& resolved) {
    profile = resolved;
    if (MH_Initialize() != MH_OK) return false;
    if (MH_CreateHook(profile.update, &HookUpdate, reinterpret_cast<void**>(&originalUpdate)) != MH_OK
        || MH_CreateHook(profile.paintText, &HookPaintText, reinterpret_cast<void**>(&originalPaintText)) != MH_OK
        || MH_QueueEnableHook(profile.update) != MH_OK || MH_QueueEnableHook(profile.paintText) != MH_OK
        || MH_ApplyQueued() != MH_OK) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        return false;
    }
    return true;
}

void RemoveCapture() {
    MH_DisableHook(MH_ALL_HOOKS);
    // Leave both the trampolines and this DLL alive until drawing callbacks return.
    Sleep(25);
    while (calls.load() != 0) Sleep(5);
    MH_Uninitialize();
}
