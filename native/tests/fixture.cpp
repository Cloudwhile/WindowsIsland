#include <windows.h>
#include <array>
#include <cstdint>
#include <string>
#include <vector>

struct QtImage {
    int reference = 1, width = 320, height = 80, depth = 32;
    intptr_t bytes = 320 * 80 * 4;
    double ratio = 1;
    void* colors = nullptr;
    unsigned char* pixels = nullptr;
    int format = 6, padding = 0;
    intptr_t stride = 320 * 4;
};
static_assert(offsetof(QtImage, pixels) == 40 && offsetof(QtImage, format) == 48);

struct QtArray {
    int reference = 1;
    int size = 0;
    unsigned int allocated = 0;
    int padding = 0;
    intptr_t offset = 24;
    wchar_t text[4097]{};
    explicit QtArray(const wchar_t* value) { size = static_cast<int>(wcslen(value)); wcscpy_s(text, value); }
};
struct Notification {
    std::array<unsigned char, 0x400> bytes{};
    QtArray title{L"DLL 消息测试"};
    QtArray body{L"你好，Telegram 原始正文。🚀"};
    std::vector<unsigned char> pixels = std::vector<unsigned char>(320 * 80 * 4);
    QtImage image;
    Notification() {
        *reinterpret_cast<uintptr_t*>(bytes.data()) = 0x5447464958545552;
        image.pixels = pixels.data();
        *reinterpret_cast<QtImage**>(bytes.data() + 0x110) = &image;
        for (int y = 9; y < 71; ++y) for (int x = 9; x < 71; ++x) {
            auto pixel = pixels.data() + (y * 320 + x) * 4;
            pixel[0] = 0x33; pixel[1] = 0x99; pixel[2] = 0xff; pixel[3] = 0xff;
        }
    }
};
extern "C" __declspec(dllexport) __declspec(noinline) void FixturePaintText(void* object, void*) {
    auto bytes = static_cast<unsigned char*>(object);
    volatile auto text = *reinterpret_cast<QtArray**>(bytes + 0x180);
    if (text && text->size > 0) Sleep(1);
}
extern "C" __declspec(dllexport) __declspec(noinline) void FixtureUpdate(void* object) {
    auto notification = reinterpret_cast<Notification*>(object);
    auto bytes = notification->bytes.data();
    *reinterpret_cast<QtArray**>(bytes + 0x180) = &notification->body;
    FixturePaintText(object, nullptr);
    *reinterpret_cast<QtArray**>(bytes + 0x180) = nullptr;
    *reinterpret_cast<QtArray**>(bytes + 0x120) = &notification->title;
}
int main() {
    Notification notification;
    Sleep(2000);
    for (int i = 0; i < 14; ++i) {
        *reinterpret_cast<uint64_t*>(notification.bytes.data() + 0x228) = GetTickCount64();
        FixtureUpdate(&notification);
        FixtureUpdate(&notification);
        // A repaint alone must never be emitted as another new notification.
        FixturePaintText(&notification, nullptr);
        Sleep(750);
    }
    return 0;
}
