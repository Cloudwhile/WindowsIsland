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
struct QtImage6 {
    int reference = 1, width = 320, height = 80, depth = 32;
    intptr_t bytes = (320 * 4 + 16) * 80;
    double ratio = 1;
    std::array<void*, 3> colors{};
    unsigned char* pixels = nullptr;
    int format = 6, padding = 0;
    intptr_t stride = 320 * 4 + 16;
};
static_assert(offsetof(QtImage6, pixels) == 56 && offsetof(QtImage6, format) == 64 && offsetof(QtImage6, stride) == 72);
struct QtString6 { void* header; const wchar_t* text; intptr_t size; };
struct QtText6 { const void* style; QtString6 text; };
static_assert(offsetof(QtText6, text) == 8);

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
    QtImage6 image6;
    bool qt6 = GetEnvironmentVariableW(L"WINI_TELEGRAM_FIXTURE_QT6", nullptr, 0) != 0;
    Notification() {
        *reinterpret_cast<uintptr_t*>(bytes.data()) = 0x5447464958545552;
        if (qt6) pixels.resize(static_cast<size_t>(image6.bytes));
        image.pixels = pixels.data();
        image6.pixels = pixels.data();
        if (qt6) *reinterpret_cast<QtImage6**>(bytes.data() + 0x100) = &image6;
        else *reinterpret_cast<QtImage**>(bytes.data() + 0x110) = &image;
        for (int y = 9; y < 71; ++y) for (int x = 9; x < 71; ++x) {
            auto pixel = pixels.data() + y * (qt6 ? image6.stride : image.stride) + x * 4;
            pixel[0] = 0x33; pixel[1] = 0x99; pixel[2] = 0xff; pixel[3] = 0xff;
        }
    }
};
extern "C" __declspec(dllexport) __declspec(noinline) void FixturePaintText(void* object, void*) {
    if (reinterpret_cast<Notification*>(object)->qt6) { Sleep(1); return; }
    auto bytes = static_cast<unsigned char*>(object);
    volatile auto text = *reinterpret_cast<QtArray**>(bytes + 0x180);
    if (text && text->size > 0) Sleep(1);
}
extern "C" __declspec(dllexport) __declspec(noinline) void FixtureUpdate(void* object) {
    auto notification = reinterpret_cast<Notification*>(object);
    auto bytes = notification->bytes.data();
    if (notification->qt6) {
        *reinterpret_cast<QtText6*>(bytes + 0x178) = { notification, { nullptr, notification->body.text, notification->body.size } };
        FixturePaintText(object, nullptr);
        *reinterpret_cast<QtText6*>(bytes + 0x178) = { notification, {} };
        *reinterpret_cast<QtText6*>(bytes + 0x108) = { notification, { nullptr, notification->title.text, notification->title.size } };
        return;
    }
    *reinterpret_cast<QtArray**>(bytes + 0x180) = &notification->body;
    FixturePaintText(object, nullptr);
    *reinterpret_cast<QtArray**>(bytes + 0x180) = nullptr;
    *reinterpret_cast<QtArray**>(bytes + 0x120) = &notification->title;
}
int main() {
    Notification notification;
    Sleep(2000);
    for (int i = 0; i < 14; ++i) {
        *reinterpret_cast<uint64_t*>(notification.bytes.data() + (notification.qt6 ? 0x230 : 0x228)) = GetTickCount64();
        FixtureUpdate(&notification);
        FixtureUpdate(&notification);
        // A repaint alone must never be emitted as another new notification.
        FixturePaintText(&notification, nullptr);
        Sleep(750);
    }
    return 0;
}
