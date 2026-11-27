#pragma once

#include <array>
#include <filesystem>
#include <string>
#include <vector>

struct Effect
{
    const char *id;
    const char *label;
    const char *category;
    const char *file;
    const char *technique;
    const char *uniform;
    float strength;
};

#define ROM_EFFECT(id, label, category, amount) \
    { #id, label, category, "ROM.fx", "ROM_" #id, "ROMStrength_" #id, static_cast<float>(amount) },
#define ROM_UPSTREAM(id, label, category, amount) \
    { #id, label, category, #id ".fx", #id, "ROMStrength_" #id, static_cast<float>(amount) },
inline constexpr Effect Catalog[] = {
#include "Shaders/Catalog.inc"
};
#undef ROM_EFFECT
#undef ROM_UPSTREAM

inline constexpr size_t EffectCount = std::size(Catalog);
static_assert(EffectCount == 54);

struct Setting
{
    bool enabled;
    float strength;
};
using Stack = std::array<Setting, EffectCount>;

struct Group
{
    std::string name;
    Stack stack;
    const char *description = "";
};

Stack DefaultStack();
const std::vector<Group> &StarterGroups();

struct Profile
{
    Stack current = StarterGroups().front().stack;
    std::vector<Group> custom;
    std::filesystem::path path;

    void Load();
    bool Save() const;
};
