import csv
import ctypes as ct
import math
import subprocess

import numpy as np
import soundfile as sf

from RemixVideos import RATE, points, read_cue

VARIANTS = ("effects", "pitch-high", "pitch-low", "both-high", "both-low")
POINTER = ct.POINTER(ct.c_float)

def table(path):
    with path.open(encoding="utf-8", newline="") as file:
        return list(csv.DictReader(file, delimiter="\t"))

def native(path):
    lib = ct.CDLL(str(path.resolve()))
    signatures = (
        ("RomScoreCreate", ct.c_void_p, ct.c_int),
        ("RomScoreDestroy", None, ct.c_void_p),
        ("RomScoreBlock", ct.c_int, ct.c_void_p),
        ("RomScorePrime", ct.c_int, ct.c_void_p, ct.c_float),
        ("RomScorePitch", None, ct.c_void_p, POINTER, POINTER, ct.c_float),
        ("RomScoreResetEffects", None, ct.c_void_p),
        ("RomScoreEffects", None, ct.c_void_p, POINTER, ct.c_int, *([ct.c_float] * 5)),
    )
    for name, result, *arguments in signatures:
        wrapper = getattr(lib, name)
        wrapper.restype = result
        wrapper.argtypes = arguments
    return lib

def pitch(lib, dsp, original, start, contour):
    block = lib.RomScoreBlock(dsp)
    delay = lib.RomScorePrime(dsp, start)
    count = len(original)
    length = math.ceil((count + delay) / block) * block
    padded = np.zeros((length, 2), dtype=np.float32)
    padded[:count] = original
    rendered = np.empty_like(padded)
    timing = np.clip((np.arange(length // block) * block - delay) / max(1, count - 1), 0., 1.)
    steps = np.interp(timing, contour[:, 0], contour[:, 1])
    for n, cents in enumerate(steps):
        start_index = n * block
        index_end = start_index + block
        lib.RomScorePitch(dsp, padded[start_index:index_end].ctypes.data_as(POINTER),
                          rendered[start_index:index_end].ctypes.data_as(POINTER), float(cents))
    return np.ascontiguousarray(rendered[delay:delay + count])

def effects(lib, dsp, audio, profile, spec):
    lib.RomScoreResetEffects(dsp)
    result = np.ascontiguousarray(audio, dtype=np.float32).copy()
    room = float(profile["room"])
    echo = float(profile["echo"])
    width = float(profile["width"])
    cutoff = float(profile["cutoff_hz"])
    block = RATE // 4
    accent = float(spec["accent_at"])
    for begin in range(0, len(result), block):
        section = result[begin:begin + block]
        progress = begin / len(result)
        pulse = math.exp(-((progress - accent) / .105) ** 2) if accent > 0 else 0.
        amount = float(spec["effect_scale"])
        lib.RomScoreEffects(dsp, section.ctypes.data_as(POINTER), len(section),
                            min(1., float(profile["wet"]) * amount),
                            min(.66, room * amount + pulse * .14),
                            min(.45, echo * amount + pulse * .12),
                            cutoff * (1 - pulse * .18), width + pulse * .08)
    return result

def envelope(stats):
    estimate = stats["music_rms"]
    speech = stats["speech_rms"]
    relative = estimate / np.maximum(stats["original_rms"], .0001)
    attention = np.clip((relative - .055) / .12, 0., 1.) * np.clip((estimate - .0017) / .01, 0., 1.)
    levels = np.minimum(.085, estimate * .8) * attention / (1 + 11 * speech)
    frames = int(stats["frames"])
    gain = np.interp(np.arange(frames), points(stats), levels).astype(np.float32)
    edge = min(frames // 2, RATE // 6)
    if edge:
        fade = np.linspace(0, 1, edge, dtype=np.float32)
        gain[:edge] *= fade
        gain[-edge:] *= 1 - fade
    return gain

def combine(score, dry, gain):
    reference = max(float(np.sqrt(np.mean(score ** 2))), .008)
    audio = np.empty_like(dry)
    np.multiply(score, gain[:, None] / reference, out=audio)
    np.add(audio, dry, out=audio)
    np.clip(audio, -.995, .995, out=audio)
    return audio

def write_video(source, mix, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(".partial.webm")
    command = ["ffmpeg", "-nostdin", "-y", "-v", "error", "-i", str(source), "-f", "f32le",
               "-ar", str(RATE), "-ac", "2", "-i", "pipe:0", "-map", "0:v:0", "-map", "1:a:0",
               "-c:v", "copy", "-c:a", "libvorbis", "-q:a", "5", "-map_metadata", "0", str(partial)]
    subprocess.run(command, input=mix.astype("<f4", copy=False).tobytes(), check=True)
    partial.replace(destination)

def contour_for(spec, start, sign):
    peak = float(spec["peak_cents"]) * sign
    accent = float(spec["accent_at"])
    if not accent:
        return np.array([[0, start], [1, start]], dtype=float)
    approach = max(.07, accent - .22)
    release = min(.96, accent + .25)
    return np.array([[0., start], [approach, start], [accent, peak],
                     [release, start * .35], [1., start * .35]], dtype=float)

def process_video(video, spec, profiles, lib, dry_folder, directory, cue_folder, completed=None):
    cue_id = spec["cue"]
    paths = {mode: directory / "DynamicVideos" / mode / video.name for mode in VARIANTS}
    ready = {mode for mode, file in paths.items() if file.is_file() and file.stat().st_size > 1024}
    if len(ready) == len(paths):
        return

    with np.load(dry_folder / (video.stem + ".npz")) as stats:
        dry, rate = sf.read(dry_folder / (video.stem + "-dialogue-effects.flac"),
                            dtype="float32", always_2d=True)
        frames = int(stats["frames"])
        if len(dry) != frames or rate != RATE:
            raise ValueError(f"Cutscene stem length does not match {video.name}")
        volume = envelope(stats)
    music = np.ascontiguousarray(read_cue(cue_folder / (cue_id + ".mp3"), frames))
    profile = profiles[cue_id]
    dsp = lib.RomScoreCreate(RATE)
    if not dsp:
        raise RuntimeError("Unable to create cutscene score processor.")
    try:
        if "effects" not in ready:
            wet = effects(lib, dsp, music, profile, spec)
            write_video(video, combine(wet, dry, volume), paths["effects"])
            if completed:
                completed()
        for sign, word in ((1, "high"), (-1, "low")):
            dry_mode, wet_mode = "pitch-" + word, "both-" + word
            if dry_mode in ready and wet_mode in ready:
                continue
            start = float(spec["start_cents"]) * sign
            shifted = pitch(lib, dsp, music, start, contour_for(spec, start, sign))
            if dry_mode not in ready:
                write_video(video, combine(shifted, dry, volume), paths[dry_mode])
                if completed:
                    completed()
            if wet_mode not in ready:
                wet = effects(lib, dsp, shifted, profile, spec)
                write_video(video, combine(wet, dry, volume), paths[wet_mode])
                if completed:
                    completed()
    finally:
        lib.RomScoreDestroy(dsp)
