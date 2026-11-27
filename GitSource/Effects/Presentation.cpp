#include "Presentation.hpp"
#include <imgui.h>
#include <reshade.hpp>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <vector>

namespace
{
    using Clock = std::chrono::steady_clock;
    constexpr wchar_t borderlessProperty[] = L"NeonLoop.Borderless";
    constexpr UINT frame = SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
    std::atomic<bool> titleShown{false};
    WINDOWPLACEMENT windowedPlacement{};
    LONG_PTR windowedStyle = 0;
    LONG_PTR windowedExStyle = 0;
    bool enterHeld = false;
    HWND enterWindow = nullptr;
    reshade::api::device *logoDevice = nullptr;
    reshade::api::resource logoTexture{};
    reshade::api::resource_view logoView{};

    void LoadLogo(reshade::api::device *device)
    {
        using namespace reshade::api;
        if (logoDevice == device)
            return;
        if (logoDevice)
            ReleaseStartupLogo(logoDevice);
        logoDevice = device;
        const DWORD length = GetEnvironmentVariableW(L"ROM_PROFILE_LOGO", nullptr, 0);
        if (!length)
            return;
        std::wstring path(length, L'\0');
        path.resize(GetEnvironmentVariableW(L"ROM_PROFILE_LOGO", path.data(), length));
        constexpr uint32_t side = 256;
        std::vector<char> pixels(side * side * 4);
        std::ifstream input(std::filesystem::path(path), std::ios::binary);
        if (!input.read(pixels.data(), pixels.size()) || input.peek() != std::char_traits<char>::eof())
            return;
        const subresource_data data{pixels.data(), side * 4, 0};
        const auto pixelFormat = format::r8g8b8a8_unorm;
        if (device->create_resource(resource_desc(side, side, 1, 1, pixelFormat, 1, memory_heap::default_,
            resource_usage::shader_resource), &data, resource_usage::shader_resource, &logoTexture))
            device->create_resource_view(logoTexture, resource_usage::shader_resource, resource_view_desc(pixelFormat), &logoView);
    }

    bool ApplyStyle(HWND window, int field, LONG_PTR style)
    {
        SetLastError(ERROR_SUCCESS);
        return SetWindowLongPtrW(window, field, style) != 0 || GetLastError() == ERROR_SUCCESS;
    }

    void RestoreFrame(HWND window)
    {
        if (!ApplyStyle(window, GWL_STYLE, windowedStyle & ~static_cast<LONG_PTR>(WS_MAXIMIZE | WS_MINIMIZE)) ||
            !ApplyStyle(window, GWL_EXSTYLE, windowedExStyle))
            return;
        RemovePropW(window, borderlessProperty);
        SetWindowPos(window, nullptr, 0, 0, 0, 0, frame | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        SetWindowPlacement(window, &windowedPlacement);
    }

    void ToggleBorderless(HWND window)
    {
        if (!GetPropW(window, L"NeonLoop.StartupPlacementReady"))
            return;
        if (IsBorderless(window))
        {
            RestoreFrame(window);
            return;
        }
        MONITORINFO monitor{};
        monitor.cbSize = sizeof(monitor);
        windowedPlacement.length = sizeof(windowedPlacement);
        if (!GetWindowPlacement(window, &windowedPlacement) ||
            !GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitor) ||
            !SetPropW(window, borderlessProperty, reinterpret_cast<HANDLE>(1)))
            return;

        windowedStyle = GetWindowLongPtrW(window, GWL_STYLE);
        windowedExStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
        if (IsZoomed(window))
            ShowWindow(window, SW_RESTORE);
        const LONG_PTR style = (windowedStyle & ~static_cast<LONG_PTR>(WS_OVERLAPPEDWINDOW | WS_MAXIMIZE)) | WS_POPUP;
        constexpr LONG_PTR edges = WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE;
        const RECT &screen = monitor.rcMonitor;
        if (!ApplyStyle(window, GWL_STYLE, style) ||
            !ApplyStyle(window, GWL_EXSTYLE, windowedExStyle & ~edges) ||
            !SetWindowPos(window, HWND_TOP, screen.left, screen.top,
                screen.right - screen.left, screen.bottom - screen.top, frame))
            RestoreFrame(window);
    }
}

bool IsBorderless(HWND window)
{
    return GetPropW(window, borderlessProperty) != nullptr;
}

void HandleFullscreenShortcut(MSG &message)
{
    if (message.hwnd != enterWindow)
    {
        enterWindow = message.hwnd;
        enterHeld = false;
    }
    if (message.wParam != VK_RETURN)
        return;
    const bool alt = (message.lParam & (1L << 29)) != 0;
    const bool down = message.message == WM_SYSKEYDOWN || message.message == WM_KEYDOWN;
    const bool up = message.message == WM_SYSKEYUP || message.message == WM_KEYUP;
    if (down && (alt || enterHeld))
    {
        if (alt && !(message.lParam & (1L << 30)))
            ToggleBorderless(message.hwnd);
        enterHeld = true;
        message.message = WM_NULL;
    }
    else if (up && (alt || enterHeld))
    {
        enterHeld = false;
        message.message = WM_NULL;
    }
}

extern "C" __declspec(dllexport) void ROMShowStartupPrompt()
{
    titleShown = true;
}

void ReleaseStartupLogo(reshade::api::device *device)
{
    if (device != logoDevice)
        return;
    if (logoView.handle)
        device->destroy_resource_view(logoView);
    if (logoTexture.handle)
        device->destroy_resource(logoTexture);
    logoView = {};
    logoTexture = {};
    logoDevice = nullptr;
}

void DrawStartupPrompt(reshade::api::device *device, bool menuOpen)
{
    static Clock::time_point start{};
    static Clock::time_point titleAt{};
    static bool finished = false;
    if (finished)
        return;
    if (menuOpen)
    {
        ReleaseStartupLogo(device);
        finished = true;
        return;
    }
    const auto now = Clock::now();
    if (start == Clock::time_point{})
        start = now;
    if (titleAt == Clock::time_point{} && titleShown.load())
        titleAt = now;
    const float time = std::chrono::duration<float>(now - start).count();
    constexpr float holdAfterTitle = 5.f;
    constexpr float fallbackHold = 120.f;
    constexpr float fadeDuration = .34f;
    const float fadeTime = titleAt == Clock::time_point{} ? time - fallbackHold :
        std::chrono::duration<float>(now - titleAt).count() - holdAfterTitle;
    if (fadeTime >= fadeDuration)
    {
        ReleaseStartupLogo(device);
        finished = true;
        return;
    }
    LoadLogo(device);
    const ImVec2 display = ImGui::GetIO().DisplaySize;
    const float scale = std::clamp(std::min(display.x / 830.f, display.y / 540.f), .35f, 1.4f);
    const float width = 660.f * scale;
    const float height = 148.f * scale;
    const float incoming = 1.f - std::clamp(time / .38f, 0.f, 1.f);
    const float outgoing = std::clamp(fadeTime / fadeDuration, 0.f, 1.f);
    const auto alpha = [outgoing](int opacity) { return static_cast<int>(opacity * (1.f - outgoing)); };
    const float x = (display.x - width) * .5f;
    const float y = 24.f * scale - (height + 28.f * scale) *
        std::max(incoming * incoming * incoming, outgoing * outgoing);
    auto *draw = ImGui::GetForegroundDrawList();
    draw->AddRectFilled(ImVec2(x + 4.f, y + 7.f), ImVec2(x + width + 4.f, y + height + 7.f),
        IM_COL32(0, 0, 0, alpha(112)), 12.f * scale);
    draw->AddRectFilled(ImVec2(x, y), ImVec2(x + width, y + height),
        IM_COL32(17, 24, 45, alpha(250)), 12.f * scale);
    const float sweep = std::clamp(time / .7f, 0.f, 1.f);
    draw->AddRectFilled(ImVec2(x + 9.f * scale, y + 9.f * scale),
        ImVec2(x + (9.f + 642.f * sweep) * scale, y + 12.f * scale), IM_COL32(113, 238, 215, alpha(255)), 2.f * scale);

    auto *font = ImGui::GetFont();
    float textX = x + 25.f * scale;
    if (logoView.handle)
    {
        const float logoX = x + 20.f * scale;
        const float logoY = y + (24.f + 12.f * incoming) * scale;
        draw->AddImage(ImTextureRef(logoView.handle), ImVec2(logoX, logoY),
            ImVec2(logoX + 106.f * scale, logoY + 106.f * scale), ImVec2(0.f, 0.f), ImVec2(1.f, 1.f),
            IM_COL32(255, 255, 255, alpha(255)));
        textX = x + 147.f * scale;
    }
    draw->AddText(font, 36.f * scale, ImVec2(textX, y + 19.f * scale),
        IM_COL32(245, 250, 255, alpha(255)), "NeonLoop");
    const float keyY = y + 63.f * scale;
    draw->AddRectFilled(ImVec2(textX, keyY), ImVec2(textX + 70.f * scale, keyY + 35.f * scale),
        IM_COL32(144, 249, 212, alpha(255)), 6.f * scale);
    draw->AddText(font, 24.f * scale, ImVec2(textX + 11.f * scale, keyY + 3.f * scale),
        IM_COL32(13, 32, 43, alpha(255)), "F10");
    draw->AddText(font, 25.f * scale, ImVec2(textX + 82.f * scale, keyY + 2.f * scale),
        IM_COL32(245, 250, 255, alpha(255)), "Open the mod menu");
    draw->AddText(font, 16.f * scale, ImVec2(textX, y + 111.f * scale),
        IM_COL32(174, 193, 220, alpha(255)), "@GoodTripGoodTip   /   github.com/goodtripgoodtip");
}
