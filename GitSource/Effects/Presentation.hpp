#pragma once
#include <Windows.h>

namespace reshade { namespace api { struct device; } }

bool IsBorderless(HWND window);
void HandleFullscreenShortcut(MSG &message);
void DrawStartupPrompt(reshade::api::device *device, bool menuOpen);
void ReleaseStartupLogo(reshade::api::device *device);
