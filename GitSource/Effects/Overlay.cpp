#include <Windows.h>
#include <dxgi.h>
#include <shellapi.h>
#include <imgui.h>
#include <reshade.hpp>
#include "Profile.hpp"
#include "GameplayMenu.hpp"
#include "Presentation.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <optional>
#include <string_view>

using reshade::api::effect_runtime;
using Clock = std::chrono::steady_clock;

extern "C" __declspec(dllexport) const char *NAME = "NeonLoop";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "F10 opens the effects menu.";

static HHOOK messageHook = nullptr;
static std::atomic<HWND> gameWindow{ nullptr };
static std::atomic<bool> hotkeyPressed{ false };
static std::atomic<bool> menuOpen{ false };
static std::atomic<unsigned int> hotspotPresses{ 0 };

extern "C" __declspec(dllexport) int ROMEffectsMenuOpen()
{
    return menuOpen ? 1 : 0;
}

extern "C" __declspec(dllexport) int ROMConsumeHotspotToggle()
{
    return (hotspotPresses.exchange(0) & 1u) != 0u ? 1 : 0;
}

static LRESULT CALLBACK GameMessage(int code, WPARAM removing, LPARAM data)
{
    if (code >= 0 && removing == PM_REMOVE)
    {
        auto &message = *reinterpret_cast<MSG *>(data);
        if (message.hwnd == gameWindow.load())
        {
            HandleFullscreenShortcut(message);
            if ((message.message == WM_KEYDOWN || message.message == WM_SYSKEYDOWN) &&
                !(message.lParam & (1L << 30)))
            {
                if (message.wParam == VK_F10)
                    hotkeyPressed = true;
                else if (message.wParam == VK_F8 && !menuOpen)
                    hotspotPresses.fetch_add(1);
            }
        }
    }
    return CallNextHookEx(nullptr, code, removing, data);
}

static void BindGameInput(HWND window)
{
    if (!window || window == gameWindow)
        return;
    if (messageHook)
        UnhookWindowsHookEx(messageHook);
    gameWindow = window;
    const DWORD windowThread = GetWindowThreadProcessId(window, nullptr);
    messageHook = SetWindowsHookExW(WH_GETMESSAGE, GameMessage, nullptr, windowThread);
    reshade::log::message(messageHook ? reshade::log::level::info : reshade::log::level::warning,
        messageHook ? "[ROM Effects] Window keyboard attached." : "[ROM Effects] Window keyboard hook unavailable.");
}

static void BindSwapchain(reshade::api::swapchain *chain, bool resize)
{
    if (resize || chain->get_device()->get_api() != reshade::api::device_api::d3d11)
        return;
    const auto window = static_cast<HWND>(chain->get_hwnd());
    auto *native = reinterpret_cast<IDXGISwapChain *>(chain->get_native());
    if (!window || !native)
        return;
    IDXGIFactory *factory = nullptr;
    if (SUCCEEDED(native->GetParent(__uuidof(IDXGIFactory), reinterpret_cast<void **>(&factory))))
    {
        factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
        factory->Release();
    }
    BindGameInput(window);
}

struct BoundEffect
{
    reshade::api::effect_technique technique{};
    reshade::api::effect_uniform_variable uniform{};
};

struct MusicLibrary
{
    const char *key;
    const char *name;
    const char *credit;
};

static constexpr MusicLibrary MusicLibraries[] = {
    { "dark-synthwave", "Dark Synthwave", "Music by Karl Casey @ White Bat Audio" },
    { "cinematic-sci-fi", "Cinematic Sci-Fi", "Music by Scott Buckley" },
    { "noir-jazz", "Noir Jazz", "Music by Kevin MacLeod (incompetech.com)" }
};

class Overlay
{
public:
    void Draw(effect_runtime *runtime);
    void Bind(effect_runtime *runtime);

private:
    Profile profile;
    std::array<BoundEffect, EffectCount> bound{};
    effect_runtime *boundRuntime = nullptr;
    size_t loaded = 0;
    LONG clientWidth = 0;
    LONG clientHeight = 0;
    bool initialized = false;
    bool hotkeyDown = false;
    bool open = false;
    bool compare = false;
    bool dirty = true;
    bool savePending = false;
    bool saveFailed = false;
    bool confirmExtreme = false;
    bool musicSaveFailed = false;
    bool musicLinkFailed = false;
    bool musicPitch = true;
    bool musicEffects = true;
    int selected = 0;
    size_t musicIndex = std::size(MusicLibraries);
    std::array<bool, std::size(MusicLibraries)> musicInstalled{};
    std::filesystem::path musicChoice;
    char groupName[80]{};
    char filter[80]{};
    std::optional<Clock::time_point> extremeEnd;
    Clock::time_point saveAt{};
    Clock::time_point bindAt{};
    double extremeFrameMs = 0;
    double extremePeakMs = 0;
    int extremeFrames = 0;
    std::string result;

    const Group &Selected() const;
    void Groups();
    void Effects();
    void Extreme();
    void FinishExtreme(const char *reason);
    void AdvanceExtreme();
    void Commit();
    void Flush();
    void Apply(effect_runtime *runtime);
    void ObserveSize(HWND window);
    void Render(effect_runtime *runtime);
    void RenderEffects(effect_runtime *runtime, float scale);
    void RenderMusic();
    void LoadMusicChoice();
    void SelectLoadedGroup();
    void SaveMusicChoice(size_t index, bool pitch, bool effects);
};

void Overlay::SelectLoadedGroup()
{
    const auto matches = [this](const Group &group)
    {
        return std::equal(profile.current.begin(), profile.current.end(), group.stack.begin(),
            [](const Setting &a, const Setting &b) { return a.enabled == b.enabled && a.strength == b.strength; });
    };
    const auto &starters = StarterGroups();
    const auto custom = std::find_if(profile.custom.begin(), profile.custom.end(), matches);
    if (custom != profile.custom.end())
        selected = static_cast<int>(starters.size() + std::distance(profile.custom.begin(), custom));
    else
    {
        const auto starter = std::find_if(starters.begin(), starters.end(), matches);
        if (starter != starters.end())
            selected = static_cast<int>(std::distance(starters.begin(), starter));
    }
}

void Overlay::Bind(effect_runtime *runtime)
{
    boundRuntime = runtime;
    loaded = 0;
    std::array<reshade::api::effect_technique, EffectCount> order{};
    for (size_t i = 0; i < EffectCount; ++i)
    {
        const auto &effect = Catalog[i];
        auto &handles = bound[i];
        handles.technique = runtime->find_technique(effect.file, effect.technique);
        handles.uniform = runtime->find_uniform_variable(effect.file, effect.uniform);
        if (handles.technique.handle && handles.uniform.handle)
            order[loaded++] = handles.technique;
    }
    bindAt = Clock::now() + std::chrono::milliseconds(250);
    if (!loaded)
        return;
    if (loaded != EffectCount)
    {
        for (size_t i = 0; i < EffectCount; ++i)
        {
            const auto &handles = bound[i];
            if (handles.technique.handle && handles.uniform.handle)
                continue;
            char msg[160];
            std::snprintf(msg, sizeof(msg), "[ROM Effects] Unavailable %s (%s).", Catalog[i].label,
                handles.technique.handle ? "strength uniform" : "technique");
            reshade::log::message(reshade::log::level::warning, msg);
        }
    }
    if (loaded == EffectCount)
        runtime->reorder_techniques(order.size(), order.data());
    char msg[128];
    std::snprintf(msg, sizeof(msg), "[ROM Effects] %zu/%zu effects ready.", loaded, EffectCount);
    reshade::log::message(reshade::log::level::info, msg);
    dirty = true;
}

void Overlay::Apply(effect_runtime *runtime)
{
    size_t active = 0;
    for (size_t i = 0; i < EffectCount; ++i)
    {
        const auto &handles = bound[i];
        if (!handles.technique.handle || !handles.uniform.handle)
            continue;
        const Setting &setting = profile.current[i];
        const bool enabled = extremeEnd.has_value() || (!compare && setting.enabled);
        const float strength = extremeEnd ? std::max(setting.strength, Catalog[i].strength) : setting.strength;
        runtime->set_uniform_value_float(handles.uniform, &strength, 1);
        if (runtime->get_technique_state(handles.technique) != enabled)
            runtime->set_technique_state(handles.technique, enabled);
        active += enabled;
    }
    char msg[128];
    std::snprintf(msg, sizeof(msg), "[ROM Effects] %zu active%s%s.", active,
        extremeEnd ? " in all-effects preview" : "", compare && !extremeEnd ? " in original view" : "");
    reshade::log::message(reshade::log::level::info, msg);
    dirty = false;
}

const Group &Overlay::Selected() const
{
    const auto &starters = StarterGroups();
    return selected < static_cast<int>(starters.size()) ? starters[selected] :
        profile.custom[selected - starters.size()];
}

void Overlay::Commit()
{
    dirty = true;
    savePending = true;
    saveAt = Clock::now() + std::chrono::milliseconds(200);
}

void Overlay::Flush()
{
    if (savePending)
    {
        saveFailed = !profile.Save();
        savePending = false;
    }
}

void Overlay::Groups()
{
    const auto &starters = StarterGroups();
    const int starterCount = static_cast<int>(starters.size());
    ImGui::TextUnformatted("Effect groups");
    ImGui::SetNextItemWidth(-1);
    if (ImGui::BeginCombo("##Groups", Selected().name.c_str()))
    {
        for (int i = 0; i < starterCount + static_cast<int>(profile.custom.size()); ++i)
        {
            const auto &group = i < starterCount ? starters[i] : profile.custom[i - starterCount];
            if (i == starterCount)
                ImGui::Separator();
            ImGui::PushID(i);
            if (ImGui::Selectable(group.name.c_str(), selected == i))
            {
                selected = i;
                if (selected >= starterCount)
                    strcpy_s(groupName, group.name.c_str());
            }
            if (selected == i)
                ImGui::SetItemDefaultFocus();
            ImGui::PopID();
        }
        ImGui::EndCombo();
    }
    if (*Selected().description)
        ImGui::TextWrapped("%s", Selected().description);

    if (ImGui::Button("Apply"))
    {
        profile.current = Selected().stack;
        compare = false;
        Commit();
    }
    ImGui::SameLine();
    if (ImGui::Button("Add to Current"))
    {
        const auto &incoming = Selected().stack;
        for (size_t i = 0; i < EffectCount; ++i)
            if (incoming[i].enabled)
                profile.current[i] = incoming[i];
        compare = false;
        Commit();
    }

    ImGui::Spacing();
    ImGui::TextUnformatted("Custom groups");
    ImGui::SetNextItemWidth(-1);
    ImGui::InputTextWithHint("##GroupName", "Group name", groupName, sizeof(groupName));
    const std::string_view input(groupName);
    const size_t start = input.find_first_not_of(" \t");
    const std::string name = start == input.npos ? "" :
        std::string(input.substr(start, input.find_last_not_of(" \t") - start + 1));
    auto existing = std::find_if(profile.custom.begin(), profile.custom.end(),
        [&](const Group &group) { return _stricmp(group.name.c_str(), name.c_str()) == 0; });
    const int existingIndex = existing == profile.custom.end() ? -1 :
        static_cast<int>(existing - profile.custom.begin());
    const bool isStarter = std::any_of(starters.begin(), starters.end(),
        [&](const Group &group) { return _stricmp(group.name.c_str(), name.c_str()) == 0; });

    ImGui::BeginDisabled(name.empty() || isStarter || existingIndex >= 0 || profile.custom.size() >= 100);
    if (ImGui::Button("Save as New"))
    {
        profile.custom.push_back({ name, profile.current });
        selected = starterCount + static_cast<int>(profile.custom.size()) - 1;
        Commit();
    }
    ImGui::EndDisabled();

    ImGui::SameLine();
    ImGui::BeginDisabled(selected < starterCount || name.empty() || isStarter ||
        (existingIndex >= 0 && existingIndex != selected - starterCount));
    if (ImGui::Button("Update"))
    {
        profile.custom[selected - starterCount] = { name, profile.current };
        Commit();
    }
    ImGui::EndDisabled();
    if (saveFailed)
        ImGui::TextWrapped("Could not write groups to the effects folder.");
}

static bool Contains(std::string_view text, std::string_view filter)
{
    return std::search(text.begin(), text.end(), filter.begin(), filter.end(), [](unsigned char a, unsigned char b)
    {
        return std::tolower(a) == std::tolower(b);
    }) != text.end();
}

void Overlay::Effects()
{
    ImGui::SetNextItemWidth(-1);
    ImGui::InputTextWithHint("##EffectSearch", "Search effects", filter, sizeof(filter));
    ImGui::BeginChild("EffectList", ImVec2(0, 0));
    for (size_t first = 0; first < EffectCount;)
    {
        size_t end = first + 1;
        while (end < EffectCount && std::strcmp(Catalog[first].category, Catalog[end].category) == 0)
            ++end;
        if (*filter || ImGui::CollapsingHeader(Catalog[first].category, first == 0 ? ImGuiTreeNodeFlags_DefaultOpen : 0))
        {
            for (size_t i = first; i < end; ++i)
            {
                const auto &effect = Catalog[i];
                if (*filter && !Contains(effect.label, filter) && !Contains(effect.category, filter))
                    continue;
                auto &setting = profile.current[i];
                ImGui::PushID(static_cast<int>(i));
                ImGui::BeginDisabled(!bound[i].technique.handle || !bound[i].uniform.handle);
                if (ImGui::Checkbox(effect.label, &setting.enabled))
                    Commit();
                ImGui::SetNextItemWidth(-1);
                if (ImGui::SliderFloat("##Strength", &setting.strength, 0.f, 1.f, "Strength %.2f", ImGuiSliderFlags_AlwaysClamp))
                    Commit();
                ImGui::EndDisabled();
                ImGui::PopID();
            }
            ImGui::Spacing();
        }
        first = end;
    }
    ImGui::EndChild();
}

void Overlay::FinishExtreme(const char *reason)
{
    extremeEnd.reset();
    confirmExtreme = false;
    dirty = true;
    char msg[200];
    std::snprintf(msg, sizeof(msg), "%s. Average %.1f ms, peak %.1f ms.",
        reason, extremeFrames ? extremeFrameMs / extremeFrames : 0.0, extremePeakMs);
    result = msg;
    reshade::log::message(reshade::log::level::info, result.c_str());
}

void Overlay::AdvanceExtreme()
{
    if (!extremeEnd)
        return;
    if (Clock::now() >= *extremeEnd)
    {
        FinishExtreme("Preview ended automatically");
        return;
    }
    const double elapsed = ImGui::GetIO().DeltaTime * 1000.0;
    extremeFrameMs += elapsed;
    extremePeakMs = std::max(extremePeakMs, elapsed);
    ++extremeFrames;
}

void Overlay::Extreme()
{
    if (extremeEnd)
    {
        const float remaining = std::chrono::duration<float>(*extremeEnd - Clock::now()).count();
        ImGui::Text("All 54 effects: %.1fs", std::max(0.f, remaining));
        if (ImGui::Button("Stop Preview"))
            FinishExtreme("Preview stopped");
        return;
    }
    if (ImGui::Button("Extreme Testing"))
        confirmExtreme = !confirmExtreme;
    if (confirmExtreme)
    {
        ImGui::TextWrapped("Runs every effect for 15 seconds. The game may slow down or flicker.");
        ImGui::BeginDisabled(loaded != EffectCount);
        if (ImGui::Button("Run All 54"))
        {
            Flush();
            extremeEnd = Clock::now() + std::chrono::seconds(15);
            extremeFrameMs = 0;
            extremePeakMs = 0;
            extremeFrames = 0;
            dirty = true;
            result.clear();
        }
        ImGui::EndDisabled();
        ImGui::SameLine();
        if (ImGui::Button("Cancel"))
            confirmExtreme = false;
    }
    if (!result.empty())
        ImGui::TextWrapped("%s", result.c_str());
}

void Overlay::LoadMusicChoice()
{
    char keys[128]{};
    GetEnvironmentVariableA("ROM_MUSIC_INSTALLED", keys, sizeof(keys));
    const auto list = "," + std::string(keys) + ",";
    for (size_t i = 0; i < std::size(MusicLibraries); ++i)
        musicInstalled[i] = list.find("," + std::string(MusicLibraries[i].key) + ",") != std::string::npos;
    const DWORD size = GetEnvironmentVariableW(L"ROM_MUSIC_SELECTION", nullptr, 0);
    if (!size)
        return;
    std::wstring path(size, L'\0');
    path.resize(GetEnvironmentVariableW(L"ROM_MUSIC_SELECTION", path.data(), size));
    musicChoice = path;
    std::ifstream file(musicChoice, std::ios::binary);
    auto readSetting = [&file](std::string &value)
    {
        if (!std::getline(file, value))
            return false;
        if (!value.empty() && value.back() == '\r')
            value.pop_back();
        return true;
    };
    std::string key;
    readSetting(key);
    for (size_t i = 0; i < std::size(MusicLibraries); ++i)
        if (musicInstalled[i] && MusicLibraries[i].key == key)
            musicIndex = i;
    while (readSetting(key))
    {
        if (key == "pitch=0")
            musicPitch = false;
        else if (key == "effects=0")
            musicEffects = false;
    }
}

void Overlay::SaveMusicChoice(size_t index, bool pitch, bool effects)
{
    auto temporary = musicChoice;
    temporary += L".tmp";
    std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
    file << (index == std::size(MusicLibraries) ? "original" : MusicLibraries[index].key)
        << '\n' << "pitch=" << pitch << '\n' << "effects=" << effects << '\n';
    file.close();
    musicSaveFailed = !file.good() || !MoveFileExW(temporary.c_str(), musicChoice.c_str(),
        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    if (!musicSaveFailed)
    {
        musicIndex = index;
        musicPitch = pitch;
        musicEffects = effects;
    }
}

void Overlay::RenderMusic()
{
    ImGui::TextUnformatted("Soundtrack library");
    ImGui::BeginDisabled(musicChoice.empty());
    if (ImGui::RadioButton("Original soundtrack", musicIndex == std::size(MusicLibraries)))
        SaveMusicChoice(std::size(MusicLibraries), musicPitch, musicEffects);
    ImGui::EndDisabled();
    for (size_t i = 0; i < std::size(MusicLibraries); ++i)
    {
        if (musicInstalled[i] && !musicChoice.empty())
        {
            if (ImGui::RadioButton(MusicLibraries[i].name, musicIndex == i))
                SaveMusicChoice(i, musicPitch, musicEffects);
        }
        else
            ImGui::TextDisabled("%s (not installed)", MusicLibraries[i].name);

        char url[512]{};
        const auto variable = "ROM_MUSIC_DOWNLOAD_" + std::string(MusicLibraries[i].key);
        const DWORD length = GetEnvironmentVariableA(variable.c_str(), url, sizeof(url));
        if (!length || length >= sizeof(url))
            continue;

        ImGui::SameLine();
        ImGui::PushID(static_cast<int>(i));
        if (ImGui::SmallButton("Download ZIP"))
            musicLinkFailed = reinterpret_cast<INT_PTR>(
                ShellExecuteA(gameWindow.load(), "open", url, nullptr, nullptr, SW_SHOWNORMAL)) <= 32;
        ImGui::PopID();
    }
    ImGui::TextWrapped("Import downloaded ZIPs under Get music packs in the NeonLoop launcher. Changes apply to new music cues and cutscenes.");
    if (musicLinkFailed)
        ImGui::TextWrapped("Could not open the download. Use Get music packs in the NeonLoop launcher.");
    if (musicIndex == std::size(MusicLibraries) || musicChoice.empty())
    {
        ImGui::TextUnformatted("Playing the original game soundtrack.");
        if (musicSaveFailed)
            ImGui::TextWrapped("Could not save the music settings.");
        return;
    }
    ImGui::Spacing();
    ImGui::TextWrapped("%s", MusicLibraries[musicIndex].credit);
    ImGui::Spacing();
    bool pitch = musicPitch;
    bool effects = musicEffects;
    const bool pitchChanged = ImGui::Checkbox("Music Pitch", &pitch);
    const bool effectsChanged = ImGui::Checkbox("Music Effects", &effects);
    if (ImGui::IsItemHovered())
        ImGui::SetTooltip("Adds score ambience and adjusts selected story cue entrances.");
    if (pitchChanged || effectsChanged)
        SaveMusicChoice(musicIndex, pitch, effects);
    if (musicSaveFailed)
        ImGui::TextWrapped("Could not save the music settings.");
}

void Overlay::RenderEffects(effect_runtime *runtime, float scale)
{
    const auto &style = ImGui::GetStyle();
    uint32_t width = 0, height = 0;
    runtime->get_screenshot_width_and_height(&width, &height);
    const size_t active = extremeEnd ? loaded : compare ? 0 :
        std::count_if(profile.current.begin(), profile.current.end(), [](const Setting &s) { return s.enabled; });
    ImGui::Text("%zu active  |  %zu/%zu ready  |  %u x %u  |  %.1f ms  |  F10 to close", active, loaded,
        EffectCount, width, height, ImGui::GetIO().Framerate > 0 ? 1000.f / ImGui::GetIO().Framerate : 0.f);
    ImGui::Separator();
    const float leftWidth = std::min(278.f * scale, ImGui::GetContentRegionAvail().x * .43f);
    ImGui::BeginChild("ControlPanel", ImVec2(leftWidth, 0), ImGuiChildFlags_Borders);
    ImGui::BeginDisabled(extremeEnd.has_value());
    if (ImGui::Checkbox("Compare with Original", &compare))
        dirty = true;
    ImGui::Spacing();
    Groups();
    ImGui::EndDisabled();
    ImGui::Separator();
    Extreme();
    ImGui::EndChild();
    ImGui::SameLine(0, style.ItemSpacing.x);
    ImGui::BeginChild("EffectsPanel", ImVec2(0, 0), ImGuiChildFlags_Borders);
    ImGui::BeginDisabled(extremeEnd.has_value());
    Effects();
    ImGui::EndDisabled();
    ImGui::EndChild();
}

void Overlay::Render(effect_runtime *runtime)
{
    const ImVec2 display = ImGui::GetIO().DisplaySize;
    const auto window = static_cast<HWND>(runtime->get_hwnd());
    const float dpi = std::clamp(GetDpiForWindow(window) / 96.f, 1.f, 1.75f);
    const float scale = std::min({ dpi, std::max(1.f, display.x / 800.f), std::max(1.f, display.y / 645.f) });
    const ImVec2 size(std::min(770.f * scale, display.x - 14.f), std::min(615.f * scale, display.y - 14.f));
    ImGui::SetNextWindowPos(ImVec2(display.x * .5f, display.y * .5f), ImGuiCond_Always, ImVec2(.5f, .5f));
    ImGui::SetNextWindowSize(size, ImGuiCond_Always);
    ImGui::PushFont(nullptr, ImGui::GetFontSize() * scale);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 10.f);
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 4.f);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(15.f * scale, 13.f * scale));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(.055f, .08f, .13f, .98f));
    ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(.13f, .23f, .34f, 1.f));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(.18f, .34f, .48f, 1.f));
    ImGui::PushStyleColor(ImGuiCol_CheckMark, ImVec4(.31f, .88f, .83f, 1.f));
    if (ImGui::Begin("NeonLoop", &open, ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoCollapse) &&
        ImGui::BeginTabBar("SettingsTabs"))
    {
        if (ImGui::BeginTabItem("Effects"))
        {
            RenderEffects(runtime, scale);
            ImGui::EndTabItem();
        }
        if (ImGui::BeginTabItem("Music"))
        {
            RenderMusic();
            ImGui::EndTabItem();
        }
        if (ImGui::BeginTabItem("Gameplay"))
        {
            RenderGameplay();
            ImGui::EndTabItem();
        }
        ImGui::EndTabBar();
    }
    ImGui::End();
    ImGui::PopStyleColor(4);
    ImGui::PopStyleVar(3);
    ImGui::PopFont();
}

void Overlay::ObserveSize(HWND window)
{
    RECT client{};
    if (!GetClientRect(window, &client) || (client.right == clientWidth && client.bottom == clientHeight))
        return;
    clientWidth = client.right;
    clientHeight = client.bottom;
    char message[128];
    std::snprintf(message, sizeof(message), "[ROM Effects] Client area %ld x %ld.", clientWidth, clientHeight);
    reshade::log::message(reshade::log::level::info, message);
}

void Overlay::Draw(effect_runtime *runtime)
{
    HWND window = static_cast<HWND>(runtime->get_hwnd());
    BindGameInput(window);
    ObserveSize(window);
    RememberGameplayWindow(window);
    if (!initialized)
    {
        profile.Load();
        SelectLoadedGroup();
        LoadMusicChoice();
        initialized = true;
        reshade::log::message(reshade::log::level::info, "[ROM Effects] Menu input ready.");
    }
    if (boundRuntime != runtime || (!loaded && Clock::now() >= bindAt))
        Bind(runtime);
    DWORD foregroundProcess = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &foregroundProcess);
    const SHORT key = foregroundProcess == GetCurrentProcessId() ? GetAsyncKeyState(VK_F10) : 0;
    const bool pressed = (key & 0x8000) != 0 || runtime->is_key_down(VK_F10);
    const bool fallback = !messageHook && ((key & 1) || runtime->is_key_pressed(VK_F10) || pressed);
    if (hotkeyPressed.exchange(false) || (fallback && !hotkeyDown))
    {
        open = !open;
        reshade::log::message(reshade::log::level::info, open ? "[ROM Effects] Menu opened." : "[ROM Effects] Menu closed.");
    }
    hotkeyDown = pressed;
    if (!messageHook && !open && foregroundProcess == GetCurrentProcessId() && runtime->is_key_pressed(VK_F8))
        hotspotPresses.fetch_add(1);
    AdvanceExtreme();

    ImGui::GetIO().MouseDrawCursor = open;
    DrawStartupPrompt(runtime->get_device(), open);
    if (open)
    {
        runtime->block_input_next_frame();
        Render(runtime);
    }
    menuOpen = open;
    if (!open || Clock::now() >= saveAt)
        Flush();
    if (dirty && loaded)
        Apply(runtime);
}

static Overlay &EffectsOverlay()
{
    static Overlay overlay;
    return overlay;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        if (!reshade::register_addon(module))
            return FALSE;
        reshade::register_event<reshade::addon_event::init_swapchain>(BindSwapchain);
        reshade::register_event<reshade::addon_event::destroy_effect_runtime>(
            [](effect_runtime *runtime) { ReleaseStartupLogo(runtime->get_device()); });
        reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(
            [](effect_runtime *runtime) { EffectsOverlay().Bind(runtime); });
        reshade::register_event<reshade::addon_event::reshade_overlay>(
            [](effect_runtime *runtime) { EffectsOverlay().Draw(runtime); });
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (messageHook)
            UnhookWindowsHookEx(messageHook);
        reshade::unregister_addon(module);
    }
    return TRUE;
}
