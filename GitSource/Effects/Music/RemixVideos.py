import subprocess

import numpy as np
import soundfile as sf

RATE = 48000
VIDEO_CUES = {
    "LIPSBoot": "titlescreen", "ROMVideo_1": "welcometoneosf", "ROMVideo_2": "strangedevelopments",
    "ROMVideo_3": "scrubbing for clues", "ROMVideo_4": "emotionaltheme",
    "ROMVideo_5": "tension", "ROMVideo_6": "turingtheme", "ROMVideo_7": "pausemusic",
    "ROMVideo_8": "distantscenery", "ROMVideo_9": "actionstreets", "ROMVideo_10": "parallaxserverroom",
    "ROMVideo_11": "dekkermemory", "ROMVideo_12": "thefinalrun", "ROMVideo_13": "epiloguedark",
    "ROMVideo_14": "epiloguelight", "ROMVideo_15": "tellmeyourstory", "ROMVideo_16": "2064",
    "ROMVideo_17": "jesstheme", "ROMVideo_18": "crowtheme", "ROMVideo_19": "creditsmusic"
}

def read_cue(path, frames):
    cmd = ["ffmpeg", "-nostdin", "-v", "error", "-i", str(path),
           "-t", f"{(frames + 2048) / RATE:.8f}", "-ac", "2", "-ar", str(RATE),
           "-f", "f32le", "-"]
    result = subprocess.run(cmd, check=True, capture_output=True)
    wave = np.frombuffer(result.stdout, "<f4").reshape(-1, 2)
    if not len(wave):
        raise RuntimeError(f"No audio was decoded from {path.name}.")
    if len(wave) < frames:
        wave = np.tile(wave, ((frames + len(wave) - 1) // len(wave), 1))
    return wave[:frames]

def points(data):
    return (np.arange(len(data["music_rms"])) + .5) * int(data["block"])

def score_video(video, base, stats, cue, final):
    dry, rate = sf.read(base, dtype="float32", always_2d=True)
    if rate != RATE or len(dry) != int(stats["frames"]):
        raise ValueError("Dry mix and video audio lengths disagree.")

    replacement = read_cue(cue, len(dry))
    estimated = stats["music_rms"]
    speech = stats["speech_rms"]
    relative = estimated / np.maximum(stats["original_rms"], .0001)
    mask = np.clip((relative - .055) / .12, 0, 1) * np.clip((estimated - .0017) / .01, 0, 1)
    duck = 1 / (1 + 11 * speech)
    levels = np.minimum(.085, estimated * .8) * mask * duck
    reference = max(float(np.sqrt(np.mean(replacement ** 2))), .008)
    level = np.interp(np.arange(len(dry)), points(stats), levels / reference).astype(np.float32)
    envelope_fade = min(len(dry) // 2, RATE // 6)
    up = np.linspace(0, 1, envelope_fade, dtype=np.float32)
    level[:envelope_fade] *= up
    level[-envelope_fade:] *= 1 - up
    mixed = dry + replacement * level[:, None]
    clip = float(np.max(np.abs(mixed)))
    if clip > .99:
        mixed *= .99 / clip
    final.parent.mkdir(parents=True, exist_ok=True)
    tmp = final.with_suffix(".partial.webm")
    cmd = ["ffmpeg", "-nostdin", "-y", "-v", "error", "-i", str(video), "-f", "f32le", "-ar", str(RATE),
           "-ac", "2", "-i", "-", "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy",
           "-c:a", "libvorbis", "-q:a", "5", "-map_metadata", "0", str(tmp)]
    subprocess.run(cmd, input=mixed.astype("<f4").tobytes(), check=True)
    tmp.replace(final)
