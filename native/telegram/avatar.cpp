#include "bridge.h"
#include <array>

namespace {
// Copy only the avatar from Telegram's existing notification image buffer.
// Text always comes from QString; this does not capture the desktop or recognize pixels.
bool CopyAvatar(void* object, const CaptureProfile& profile, unsigned char* output) noexcept {
    __try {
        auto image = *reinterpret_cast<const unsigned char**>(static_cast<unsigned char*>(object) + 0x110);
        if (!image) return false;
        const auto width = *reinterpret_cast<const int*>(image + 4);
        const auto height = *reinterpret_cast<const int*>(image + 8);
        const auto depth = *reinterpret_cast<const int*>(image + 12);
        const auto format = *reinterpret_cast<const int*>(image + 48);
        const auto pixels = *reinterpret_cast<const unsigned char* const*>(image + 40);
        if (width < 80 || width > 4096 || height < 40 || height > 4096 || depth != 32 || format != 6 || !pixels) return false;
        const auto ratio = profile.pixelRatio ? *profile.pixelRatio : 1;
        const auto size = (profile.photoSize ? *profile.photoSize : 62) * ratio;
        auto left = (profile.photoX ? *profile.photoX : 9) * ratio;
        const auto top = (profile.photoY ? *profile.photoY : 9) * ratio;
        if (profile.rtl && *profile.rtl) left = width - left - size;
        if (ratio < 1 || ratio > 8 || size < 8 || size > 1024 || left < 0 || top < 0 || left + size > width || top + size > height) return false;
        for (int y = 0; y < 64; ++y) {
            for (int x = 0; x < 64; ++x) {
                const auto source = pixels + ((top + y * size / 64) * width + left + x * size / 64) * 4;
                memcpy(output + (y * 64 + x) * 4, source, 4);
            }
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
}

std::vector<unsigned char> ReadAvatar(void* object, const CaptureProfile& profile) {
    std::array<unsigned char, 64 * 64 * 4> pixels{};
    return CopyAvatar(object, profile, pixels.data())
        ? std::vector<unsigned char>(pixels.begin(), pixels.end()) : std::vector<unsigned char>();
}
