#include <Windows.h>
#include <imgui.h>
#include <reshade.hpp>
#include "GameplayMenu.hpp"
#include "Presentation.hpp"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>

namespace
{
    constexpr uint32_t rememberMonitor = 1u << 18;
    constexpr uint32_t startWindowState = 1u << 20;
    constexpr uint32_t startMaximized = 1u << 21;

    std::filesystem::path EnvPath(const wchar_t *key)
    {
        DWORD size = GetEnvironmentVariableW(key, nullptr, 0);
        if (!size)
            return {};
        std::wstring path(size, L'\0');
        path.resize(GetEnvironmentVariableW(key, path.data(), size));
        return path;
    }

    bool ReplaceText(const std::filesystem::path &path, const std::string &text)
    {
        if (path.empty())
            return false;
        auto temporary = path;
        temporary += L".pending";
        std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
        file << text;
        file.close();
        return file.good() && MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
    }

    struct Settings
    {
        const std::filesystem::path settings = EnvPath(L"ROM_GAMEPLAY_SETTINGS");
        const std::filesystem::path window = EnvPath(L"ROM_GAMEPLAY_WINDOW");
        const std::filesystem::path history = EnvPath(L"ROM_GAMEPLAY_HISTORY");
        std::atomic<uint32_t> flags{0};
        bool saveFailed = false;

        Settings()
        {
            uint32_t stored = 0;
            std::ifstream file(settings);
            if (file >> stored)
                flags = stored & ((1u << 23) - 1);
        }

        void Save(uint32_t value)
        {
            flags = value;
            saveFailed = !ReplaceText(settings, std::to_string(value));
        }
    };

    Settings &Gameplay()
    {
        static Settings settings;
        return settings;
    }

    void Option(unsigned index, const char *label, bool inverted = false)
    {
        auto &settings = Gameplay();
        const uint32_t bit = 1u << index;
        const uint32_t current = settings.flags.load();
        bool enabled = ((current & bit) != 0) != inverted;
        if (ImGui::Checkbox(label, &enabled))
            settings.Save(current ^ bit);
    }

    void ShowHistory()
    {
        static char search[160]{};
        ImGui::InputTextWithHint("##DialogueSearch", "Search this session", search, sizeof(search));
        ImGui::BeginChild("ConversationHistory", ImVec2(0, 150), ImGuiChildFlags_Borders);
        std::ifstream file(Gameplay().history, std::ios::binary);
        bool matched = false;
        for (std::string line; std::getline(file, line);)
        {
            if (search[0])
            {
                std::string value = line;
                std::string needle = search;
                for (char &character : value)
                    if (character >= 'A' && character <= 'Z') character += 'a' - 'A';
                for (char &character : needle)
                    if (character >= 'A' && character <= 'Z') character += 'a' - 'A';
                if (value.find(needle) == std::string::npos)
                    continue;
            }
            ImGui::PushTextWrapPos();
            ImGui::TextUnformatted(line.c_str());
            ImGui::PopTextWrapPos();
            matched = true;
        }
        if (!matched)
            ImGui::TextDisabled(search[0] ? "No matching lines." : "Completed dialogue will appear here.");
        ImGui::EndChild();
    }
}

extern "C" __declspec(dllexport) unsigned ROMGameplayFlags()
{
    return Gameplay().flags.load();
}

void RenderGameplay()
{
    auto &settings = Gameplay();
    ImGui::BeginChild("GameplayOptions", ImVec2(0, 0));
    if (settings.saveFailed)
        ImGui::TextWrapped("Options are active for this session. The settings file could not be saved.");
    if (ImGui::CollapsingHeader("Dialogue", ImGuiTreeNodeFlags_DefaultOpen))
    {
        Option(0, "Skip only dialogue already read");
        Option(1, "Reveal full lines instantly");
        Option(2, "Conversation history");
        if (settings.flags.load() & (1u << 2))
            ShowHistory();
    }
    if (ImGui::CollapsingHeader("Accessibility"))
    {
        Option(22, "Use readable in-game font", true);
        Option(11, "Larger dialogue choices");
    }
    if (ImGui::CollapsingHeader("Controls"))
    {
        Option(8, "Keep the exploration pointer visible");
        Option(10, "One-click selection in supported menus");
    }
    if (ImGui::CollapsingHeader("Saves"))
    {
        Option(6, "Keep three recovery copies per save slot");
        Option(12, "Preview the Continue destination");
        Option(16, "Show when a save finishes or fails");
    }
    if (ImGui::CollapsingHeader("Exploration"))
    {
        Option(5, "Reveal interactable hotspots with F8");
        Option(9, "Show the current map objective in the room");
        Option(14, "Preview hotspot actions as icons");
        Option(15, "Mark visited locations on the city map");
        Option(17, "Track completed hotspot actions and dialogue choices");
        Option(19, "Remember the last focused inventory item");
    }
    if (ImGui::CollapsingHeader("Puzzle assists"))
    {
        Option(4, "Retry the Chapter 2 taxi from its failure prompt");
        Option(7, "Label visible maintenance tunnel exits");
        Option(13, "Label glyph symbols and positions");
    }
    if (ImGui::CollapsingHeader("PC startup"))
    {
        Option(18, "Reopen on the last-used monitor");
        Option(20, "Choose the starting window state");
        const uint32_t flags = settings.flags.load();
        if (flags & startWindowState)
        {
            int selected = (flags & startMaximized) ? 1 : 0;
            if (ImGui::Combo("Next launch", &selected, "Restored\0Maximized\0"))
                settings.Save(selected ? flags | startMaximized : flags & ~startMaximized);
        }
    }
    ImGui::EndChild();
}

void RememberGameplayWindow(HWND window)
{
    auto &settings = Gameplay();
    if (settings.window.empty() || !(settings.flags.load() & (rememberMonitor | startWindowState)) ||
        !window || !GetPropW(window, L"NeonLoop.StartupPlacementReady") ||
        !IsWindowVisible(window) || IsIconic(window) || IsBorderless(window))
        return;

    using Clock = std::chrono::steady_clock;
    static Clock::time_point next{};
    static std::string previous;
    if (Clock::now() < next)
        return;
    next = Clock::now() + std::chrono::milliseconds(500);

    MONITORINFOEXA monitor{};
    monitor.cbSize = sizeof(monitor);
    RECT rect{};
    if (!GetMonitorInfoA(MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY),
        reinterpret_cast<MONITORINFO *>(&monitor)) || !GetWindowRect(window, &rect))
        return;

    std::string device = monitor.szDevice;
    std::string bounds;
    if (IsZoomed(window))
    {
        std::ifstream file(settings.window);
        std::string oldDevice;
        if (std::getline(file, oldDevice) && oldDevice == device)
            std::getline(file, bounds);
    }
    else
    {
        bounds = std::to_string(rect.left) + " " + std::to_string(rect.top) + " " +
            std::to_string(rect.right - rect.left) + " " + std::to_string(rect.bottom - rect.top);
    }
    std::string current = device + "\n" + bounds;
    if (current != previous && ReplaceText(settings.window, current))
        previous = current;
}
