#include "bridge.h"
#include <array>

namespace {
struct Signature { uintptr_t object = 0; uint64_t started = 0; };
std::array<Signature, 128> recent{};
size_t cursor = 0;
SRWLOCK lock = SRWLOCK_INIT;

uint64_t Started(void* object) noexcept {
    __try { return *reinterpret_cast<const uint64_t*>(static_cast<unsigned char*>(object) + 0x228); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
}

bool FirstNotification(void* object) noexcept {
    // Palette and privacy changes redraw the same popup; they are not new messages.
    const Signature signature{reinterpret_cast<uintptr_t>(object), Started(object)};
    if (!signature.started) return false;
    AcquireSRWLockExclusive(&lock);
    for (const auto& item : recent) {
        if (item.object == signature.object && item.started == signature.started) {
            ReleaseSRWLockExclusive(&lock);
            return false;
        }
    }
    recent[cursor++ % recent.size()] = signature;
    ReleaseSRWLockExclusive(&lock);
    return true;
}
