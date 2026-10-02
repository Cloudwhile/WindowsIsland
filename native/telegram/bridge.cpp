#include "bridge.h"
#include <atomic>
#include <deque>
#include <utility>
#include <vector>

namespace {
struct Message { PacketHeader header; std::wstring title, body; std::vector<unsigned char> avatar; };
HMODULE self = nullptr;
HookInit configuration{};
std::atomic<bool> active = false;
std::atomic<uint64_t> sequence = 0;
SRWLOCK queueLock = SRWLOCK_INIT;
std::deque<Message> messages;

bool WritePacket(HANDLE pipe, const Message& message) {
    const auto& header = message.header;
    const auto textLength = (header.titleLength + header.bodyLength) * sizeof(wchar_t);
    std::vector<unsigned char> bytes(sizeof(header) + textLength + header.avatarLength);
    memcpy(bytes.data(), &header, sizeof(header));
    memcpy(bytes.data() + sizeof(header), message.title.data(), header.titleLength * sizeof(wchar_t));
    memcpy(bytes.data() + sizeof(header) + header.titleLength * sizeof(wchar_t), message.body.data(), header.bodyLength * sizeof(wchar_t));
    if (header.avatarLength) memcpy(bytes.data() + sizeof(header) + textLength, message.avatar.data(), header.avatarLength);
    DWORD written = 0;
    return WriteFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size();
}

DWORD WINAPI SendLoop(void*) {
    HANDLE pipe = INVALID_HANDLE_VALUE;
    auto deadline = GetTickCount64() + 5000;
    while (GetTickCount64() < deadline && pipe == INVALID_HANDLE_VALUE) {
        pipe = CreateFileW(configuration.pipe, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) Sleep(50);
    }
    if (pipe != INVALID_HANDLE_VALUE) {
        Message ready;
        ready.header.kind = 0;
        ready.header.processId = GetCurrentProcessId();
        ULONG server = 0;
        bool connected = GetNamedPipeServerProcessId(pipe, &server) && server == configuration.ownerPid && WritePacket(pipe, ready);
        auto heartbeat = GetTickCount64();
        while (connected) {
            std::deque<Message> batch;
            AcquireSRWLockExclusive(&queueLock);
            batch.swap(messages);
            ReleaseSRWLockExclusive(&queueLock);
            for (const auto& message : batch) {
                if (!WritePacket(pipe, message)) { connected = false; break; }
            }
            if (connected && GetTickCount64() - heartbeat >= 500) {
                ready.header.kind = 2;
                connected = WritePacket(pipe, ready);
                heartbeat = GetTickCount64();
            }
            if (connected) Sleep(25);
        }
        CloseHandle(pipe);
    }
    RemoveCapture();
    active = false;
    FreeLibraryAndExitThread(self, 0);
}
}

void QueueMessage(uintptr_t object, std::wstring title, std::wstring body, std::vector<unsigned char> avatar) noexcept {
    try {
        Message message;
        message.header.processId = GetCurrentProcessId();
        message.header.objectId = object;
        message.header.sequence = ++sequence;
        FILETIME now;
        GetSystemTimeAsFileTime(&now);
        message.header.fileTime = (static_cast<uint64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
        message.title = std::move(title);
        message.body = std::move(body);
        message.avatar = std::move(avatar);
        message.header.titleLength = static_cast<uint32_t>(message.title.size());
        message.header.bodyLength = static_cast<uint32_t>(message.body.size());
        message.header.avatarLength = static_cast<uint32_t>(message.avatar.size());
        AcquireSRWLockExclusive(&queueLock);
        // A bounded buffer keeps Telegram's drawing thread independent of the tray app.
        if (messages.size() >= 64) messages.pop_front();
        try { messages.push_back(std::move(message)); }
        catch (...) { ReleaseSRWLockExclusive(&queueLock); return; }
        ReleaseSRWLockExclusive(&queueLock);
    }
    catch (...) { }
}

extern "C" __declspec(dllexport) DWORD WINAPI StartHook(void* parameter) {
    if (active.exchange(true)) return 2;
    __try { configuration = *static_cast<HookInit*>(parameter); }
    __except (EXCEPTION_EXECUTE_HANDLER) { active = false; return 3; }
    if (configuration.magic != Magic || configuration.version != 2 || configuration.pipe[255] != 0
        || wcsncmp(configuration.pipe, L"\\\\.\\pipe\\WindowsIsland.Telegram.", 32) != 0) { active = false; return 4; }
    CaptureProfile profile;
    if (!ResolveProfile(configuration.fixture != 0, profile)) { active = false; return 5; }
    if (!InstallCapture(profile)) { active = false; return 6; }
    auto thread = CreateThread(nullptr, 0, SendLoop, nullptr, 0, nullptr);
    if (!thread) { RemoveCapture(); active = false; return 7; }
    CloseHandle(thread);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) { self = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
