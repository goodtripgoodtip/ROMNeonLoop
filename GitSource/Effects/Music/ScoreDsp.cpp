#include "rubberband/RubberBandLiveShifter.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <stdexcept>
#include <vector>

namespace
{
    using RubberBand::RubberBandLiveShifter;
    constexpr double Pi = 3.14159265358979323846;

    class Delay
    {
    public:
        explicit Delay(size_t count) : buffer(count, 0.f) {}

        float Advance(float input, float feedback)
        {
            const float result = buffer[index];
            buffer[index] = input + result * feedback;
            if (++index == buffer.size())
                index = 0;
            return result;
        }

        void Clear()
        {
            std::fill(buffer.begin(), buffer.end(), 0.f);
            index = 0;
        }

    private:
        std::vector<float> buffer;
        size_t index = 0;
    };

    class Ambience
    {
    public:
        explicit Ambience(int rate)
            : sampleRate(static_cast<float>(rate)),
              transition(1.f - std::exp(-1.f / (sampleRate * .055f))),
              leftEcho(Length(rate, .166f)), rightEcho(Length(rate, .239f)),
              leftRoom{ Delay(Length(rate, .0297f)), Delay(Length(rate, .0411f)), Delay(Length(rate, .0533f)) },
              rightRoom{ Delay(Length(rate, .0331f)), Delay(Length(rate, .0479f)), Delay(Length(rate, .0593f)) }
        {
        }

        void Reset()
        {
            leftEcho.Clear();
            rightEcho.Clear();
            for (auto &delay : leftRoom)
                delay.Clear();
            for (auto &delay : rightRoom)
                delay.Clear();
            lowL = lowR = echoL = echoR = mix = roomWet = echoWet = 0.f;
            stereoWidth = 1.f;
            filterBlend = 0.f;
            lowCoeff = 1.f;
        }

        void Process(float *audio, int frames, float enabled, float room, float echo, float cutoff, float width)
        {
            const float frequency = std::clamp(cutoff, 250.f, 20000.f);
            const float coeff = 1.f - std::exp(static_cast<float>(-2.0 * Pi) * frequency / sampleRate);
            const float occlusion = 1.f - std::clamp((frequency - 10500.f) / 9500.f, 0.f, 1.f);

            for (int i = 0; i < frames; ++i)
            {
                Follow(mix, enabled);
                Follow(roomWet, room);
                Follow(echoWet, echo);
                Follow(lowCoeff, coeff);
                Follow(stereoWidth, width);
                Follow(filterBlend, occlusion);

                const float left = audio[i * 2];
                const float right = audio[i * 2 + 1];
                lowL += (left - lowL) * lowCoeff;
                lowR += (right - lowR) * lowCoeff;
                const float mid = ((left + (lowL - left) * filterBlend) +
                    (right + (lowR - right) * filterBlend)) * .5f;
                const float side = ((left + (lowL - left) * filterBlend) -
                    (right + (lowR - right) * filterBlend)) * .5f * stereoWidth;
                const float dryL = mid + side;
                const float dryR = mid - side;

                const float tapL = leftEcho.Advance(dryL * mix + echoR * .19f, .29f);
                const float tapR = rightEcho.Advance(dryR * mix + echoL * .19f, .29f);
                echoL = .57f * echoL + .43f * tapL;
                echoR = .57f * echoR + .43f * tapR;

                float reflL = 0.f;
                float reflR = 0.f;
                const float exciteL = (dryL * .75f + dryR * .25f) * mix;
                const float exciteR = (dryR * .75f + dryL * .25f) * mix;
                for (size_t n = 0; n < leftRoom.size(); ++n)
                {
                    const float feedback = .68f - .055f * static_cast<float>(n);
                    reflL += leftRoom[n].Advance(exciteL, feedback);
                    reflR += rightRoom[n].Advance(exciteR, feedback);
                }

                const float processedL = dryL * (1.f - roomWet * .16f - echoWet * .10f) +
                    reflL * roomWet * .24f + echoL * echoWet * .60f;
                const float processedR = dryR * (1.f - roomWet * .16f - echoWet * .10f) +
                    reflR * roomWet * .24f + echoR * echoWet * .60f;
                if (mix > .0001f)
                {
                    audio[i * 2] = std::clamp(left + (processedL - left) * mix, -.997f, .997f);
                    audio[i * 2 + 1] = std::clamp(right + (processedR - right) * mix, -.997f, .997f);
                }
            }
        }

    private:
        static size_t Length(int rate, float seconds)
        {
            return std::max<size_t>(1, static_cast<size_t>(rate * seconds));
        }

        void Follow(float &value, float target) const
        {
            value += (target - value) * transition;
        }

        const float sampleRate;
        const float transition;
        Delay leftEcho;
        Delay rightEcho;
        std::array<Delay, 3> leftRoom;
        std::array<Delay, 3> rightRoom;
        float lowL = 0.f;
        float lowR = 0.f;
        float echoL = 0.f;
        float echoR = 0.f;
        float mix = 0.f;
        float roomWet = 0.f;
        float echoWet = 0.f;
        float stereoWidth = 1.f;
        float filterBlend = 0.f;
        float lowCoeff = 1.f;
    };

    class ScoreDsp
    {
    public:
        explicit ScoreDsp(int rate)
            : pitch(static_cast<size_t>(rate), 2, RubberBandLiveShifter::OptionWindowShort |
                RubberBandLiveShifter::OptionChannelsTogether |
                RubberBandLiveShifter::OptionFormantPreserved),
              block(pitch.getBlockSize()), inputL(block), inputR(block),
              outputL(block), outputR(block), ambience(rate)
        {
        }

        int Prime(float cents)
        {
            pitch.reset();
            lastCents = 1000.f;
            Tune(cents);
            return static_cast<int>(pitch.getStartDelay());
        }

        void Transform(const float *input, float *output, float cents)
        {
            Tune(cents);
            for (size_t i = 0; i < block; ++i)
            {
                inputL[i] = input[2 * i];
                inputR[i] = input[2 * i + 1];
            }
            const float *in[] = { inputL.data(), inputR.data() };
            float *out[] = { outputL.data(), outputR.data() };
            pitch.shift(in, out);
            for (size_t i = 0; i < block; ++i)
            {
                output[2 * i] = outputL[i];
                output[2 * i + 1] = outputR[i];
            }
        }

        size_t Block() const { return block; }
        Ambience &Effects() { return ambience; }

    private:
        void Tune(float cents)
        {
            cents = std::clamp(cents, -65.f, 65.f);
            if (std::abs(lastCents - cents) >= .02f)
            {
                pitch.setPitchScale(std::exp2(static_cast<double>(cents) / 1200.0));
                lastCents = cents;
            }
        }

        RubberBandLiveShifter pitch;
        const size_t block;
        std::vector<float> inputL;
        std::vector<float> inputR;
        std::vector<float> outputL;
        std::vector<float> outputR;
        Ambience ambience;
        float lastCents = 1000.f;
    };
}

extern "C" __declspec(dllexport) ScoreDsp *RomScoreCreate(int rate) noexcept
{
    try
    {
        return rate == 44100 || rate == 48000 ? new ScoreDsp(rate) : nullptr;
    }
    catch (...)
    {
        return nullptr;
    }
}

extern "C" __declspec(dllexport) void RomScoreDestroy(ScoreDsp *state) noexcept
{
    delete state;
}

extern "C" __declspec(dllexport) int RomScoreBlock(const ScoreDsp *state) noexcept
{
    return static_cast<int>(state->Block());
}

extern "C" __declspec(dllexport) int RomScorePrime(ScoreDsp *state, float cents) noexcept
{
    return state->Prime(cents);
}

extern "C" __declspec(dllexport) void RomScorePitch(ScoreDsp *state, const float *input, float *output, float cents) noexcept
{
    state->Transform(input, output, cents);
}

extern "C" __declspec(dllexport) void RomScoreResetEffects(ScoreDsp *state) noexcept
{
    state->Effects().Reset();
}

extern "C" __declspec(dllexport) void RomScoreEffects(ScoreDsp *state, float *audio, int frames,
    float enabled, float room, float echo, float cutoff, float width) noexcept
{
    state->Effects().Process(audio, frames, enabled, room, echo, cutoff, width);
}
