#include "Profile.hpp"

#include <Windows.h>
#include <algorithm>
#include <cmath>
#include <fstream>
#include <initializer_list>
#include <json.hpp>
#include <stdexcept>
#include <utility>

using nlohmann::json;

Stack DefaultStack()
{
    Stack stack{};
    for (size_t i = 0; i < EffectCount; ++i)
        stack[i] = { false, Catalog[i].strength };
    return stack;
}

static Group MakeGroup(const char *name, const char *description,
    std::initializer_list<std::pair<const char *, float>> effects)
{
    Group group{ name, DefaultStack(), description };
    for (const auto &pair : effects)
    {
        auto effect = std::find_if(std::begin(Catalog), std::end(Catalog),
            [&](const Effect &entry) { return entry.id == std::string_view(pair.first); });
        if (effect == std::end(Catalog))
            throw std::logic_error("Unknown starter effect");
        group.stack[effect - std::begin(Catalog)] = { true, pair.second };
    }
    return group;
}

const std::vector<Group> &StarterGroups()
{
    static const std::vector<Group> groups = {
        MakeGroup("Cinematic Neon", "Softer edges, colored glow, and a cool night grade.",
            { {"PixelEdge", .78f}, {"SMAA", .64f}, {"SoftBloom", .29f}, {"NeonGlow", .23f}, {"SplitTone", .32f} }),
        MakeGroup("Clean and Smooth", "Smoother diagonal edges with the original palette.",
            { {"PixelEdge", .76f}, {"SMAA", .72f}, {"EdgeSoften", .23f} }),
        MakeGroup("Painterly", "Flattened color, softened edges, and paper texture.",
            { {"PixelEdge", .76f}, {"Watercolor", .67f}, {"Paper", .24f}, {"Vibrance", .18f}, {"Diffusion", .22f} }),
        MakeGroup("Film Noir", "Monochrome, deep contrast, and subtle grain.",
            { {"SMAA", .70f}, {"Monochrome", 1.f}, {"Contrast", .51f}, {"Grain", .18f}, {"Vignette", .36f} }),
        MakeGroup("Warm Film", "Warm highlights, lifted blacks, and red halation.",
            { {"SMAA", .63f}, {"Temperature", .43f}, {"Halation", .28f}, {"Fade", .22f}, {"Grain", .12f} }),
        MakeGroup("Cool Night", "Cool shadows with subdued highlights and glow.",
            { {"PixelEdge", .73f}, {"SplitTone", .61f}, {"HighlightRoll", .23f}, {"NeonGlow", .24f}, {"Vignette", .23f} }),
        MakeGroup("CRT Arcade", "Scanlines and phosphor with a mild tube glow.",
            { {"SoftBloom", .25f}, {"Scanlines", .32f}, {"Phosphor", .24f}, {"Chromatic", .13f}, {"Vignette", .14f} }),
        MakeGroup("High Clarity", "Clean diagonals and crisp local detail.",
            { {"SMAA", .85f}, {"AdaptiveSharp", .25f}, {"LocalContrast", .24f}, {"ShadowDetail", .18f} }),
        MakeGroup("Performance", "A fast, single-effect smoothing preset.",
            { {"PixelEdge", .78f} })
    };
    return groups;
}

static Stack DecodeStack(const json &object)
{
    Stack stack = DefaultStack();
    if (!object.is_object())
        return stack;
    for (size_t i = 0; i < EffectCount; ++i)
    {
        const auto entry = object.find(Catalog[i].id);
        if (entry == object.end() || !entry->is_object())
            continue;
        const auto enabled = entry->find("on");
        const auto strength = entry->find("strength");
        if (enabled != entry->end() && enabled->is_boolean())
            stack[i].enabled = enabled->get<bool>();
        if (strength != entry->end() && strength->is_number())
        {
            const auto value = strength->get<float>();
            if (std::isfinite(value))
                stack[i].strength = std::clamp(value, 0.f, 1.f);
        }
    }
    return stack;
}

static json EncodeStack(const Stack &stack)
{
    json object = json::object();
    for (size_t i = 0; i < EffectCount; ++i)
        object[Catalog[i].id] = { {"on", stack[i].enabled}, {"strength", stack[i].strength} };
    return object;
}

void Profile::Load()
{
    const DWORD size = GetEnvironmentVariableW(L"ROM_FX_PROFILE", nullptr, 0);
    if (!size)
        return;
    std::wstring value(size, L'\0');
    value.resize(GetEnvironmentVariableW(L"ROM_FX_PROFILE", value.data(), size));
    path = value;
    std::ifstream file(path, std::ios::binary);
    const auto data = json::parse(file, nullptr, false);
    if (!data.is_object())
        return;
    const auto settings = data.find("current");
    if (settings != data.end() && settings->is_object())
        current = DecodeStack(*settings);
    const auto groups = data.find("groups");
    if (groups == data.end() || !groups->is_array())
        return;
    for (const auto &group : *groups)
    {
        if (!group.is_object() || !group.contains("name") || !group["name"].is_string() || !group.contains("stack"))
            continue;
        const std::string name = group["name"].get<std::string>();
        if (!name.empty() && name.size() < 80 && custom.size() < 100)
            custom.push_back({ name, DecodeStack(group["stack"]) });
    }
}

bool Profile::Save() const
{
    if (path.empty())
        return false;
    json groups = json::array();
    for (const auto &group : custom)
        groups.push_back({ {"name", group.name}, {"stack", EncodeStack(group.stack)} });
    const json data = { {"version", 1}, {"current", EncodeStack(current)}, {"groups", std::move(groups)} };
    auto temporary = path;
    temporary += L".tmp";
    std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
    file << data.dump(2, ' ', false, json::error_handler_t::replace) << '\n';
    file.close();
    return file.good() && MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}
