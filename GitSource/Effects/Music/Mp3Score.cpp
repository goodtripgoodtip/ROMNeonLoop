#include <climits>
#include <new>

#define MINIMP3_IMPLEMENTATION
#define MINIMP3_ONLY_MP3
#define MINIMP3_FLOAT_OUTPUT
#include "minimp3_ex.h"

extern "C" __declspec(dllexport) mp3dec_ex_t *RomMp3Open(const wchar_t *path, int *frames) noexcept
{
    auto state = new (std::nothrow) mp3dec_ex_t{};
    if (!state)
        return nullptr;
    if (mp3dec_ex_open_w(state, path, MP3D_SEEK_TO_SAMPLE))
    {
        delete state;
        return nullptr;
    }
    if (state->info.hz != 44100 || state->info.channels != 2 ||
        !state->samples || state->samples % 2 || state->samples / 2 > INT_MAX)
    {
        mp3dec_ex_close(state);
        delete state;
        return nullptr;
    }
    *frames = static_cast<int>(state->samples / 2);
    return state;
}

extern "C" __declspec(dllexport) void RomMp3Close(mp3dec_ex_t *state) noexcept
{
    mp3dec_ex_close(state);
    delete state;
}

extern "C" __declspec(dllexport) int RomMp3Read(mp3dec_ex_t *state, int frame,
    float *output, int offset, int frames) noexcept
{
    const uint64_t start = static_cast<uint64_t>(frame) * 2;
    if (state->cur_sample != start && mp3dec_ex_seek(state, start))
        return -1;
    return static_cast<int>(mp3dec_ex_read(state, output + offset, static_cast<size_t>(frames) * 2));
}
