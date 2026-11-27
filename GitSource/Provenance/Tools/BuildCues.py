import argparse
import csv
import json
import pathlib
import subprocess
from collections import Counter

import numpy as np

RATE = 44100
MODES = ("dark-synthwave", "cinematic-sci-fi", "noir-jazz")
LEVEL = {"menu": -21, "quiet": -24, "mystery": -22, "city": -20,
         "action": -17, "club": -18, "credits": -19}
RELATED = {
    "menu": ("quiet", "mystery", "city"),
    "quiet": ("menu", "mystery", "city"),
    "mystery": ("quiet", "city", "action"),
    "city": ("club", "quiet", "credits"),
    "action": ("club", "mystery", "city"),
    "club": ("city", "action", "mystery"),
    "credits": ("quiet", "city", "menu")
}
FAVORITES = {
    "dark-synthwave": {
        "titlescreen": "endless-night", "maintheme": "heart-of-night", "welcometoneosf": "arcadia",
        "sunset apartments": "rain-city", "scrubbing for clues": "hackers", "creditsmusic": "end-credits",
        "stardust club": "miami-sky", "dekkerchase1": "the-drop", "parallaxserverroom": "data",
        "turingtheme": "sentimental", "pausemusic": "last-stop", "thefinalrun": "race-against-sunset",
        "phobetorheart": "virtual-death", "lovemebetter": "afterglow"
    },
    "cinematic-sci-fi": {
        "titlescreen": "legacy", "maintheme": "aphelion", "sunset apartments": "phase-shift",
        "scrubbing for clues": "shadows-and-dust", "creditsmusic": "convergence",
        "turingtheme": "chasing-daylight", "dekkerchase1": "escape-velocity"
    },
    "noir-jazz": {
        "titlescreen": "spy-glass", "maintheme": "space-jazz", "hassybar": "on-the-cool-side",
        "sunset apartments": "i-knew-a-guy", "scrubbing for clues": "investigations",
        "creditsmusic": "night-on-the-docks-sax", "parallaxserverroom": "consequence",
        "stardust club": "lobby-time", "dekkerchase1": "hard-boiled"
    }
}
SHAPES = {
    "mainthemedynamic": "highpass=f=38", "maintheme": "lowpass=f=7200",
    "hospital no arpeggio": "lowpass=f=2300",
    "parallaxsewer": "lowpass=f=1400", "parallaxserverroom": "lowpass=f=6200",
    "stardustoutsidech2": "lowpass=f=1550", "outsidestardust": "lowpass=f=2200",
    "pubcrawloutside": "lowpass=f=1250",
    "hassyphone": "highpass=f=350,lowpass=f=2350", "stardustphone": "highpass=f=350,lowpass=f=2350",
    "turing'swaynegative": "lowpass=f=1900", "turing'swayvanilla": "lowpass=f=4700",
    "dekkerchase1": "lowpass=f=2500", "dekkerchase2": "lowpass=f=5400"
}

def select_sources(cues, catalog, mode):
    picks, grouped = {}, {}
    uses = Counter()
    reserved = set(FAVORITES[mode].values()) & set(catalog)
    for cue in sorted(cues, key=lambda row: row["cue"] not in FAVORITES[mode]):
        name = cue["cue"]
        mood, _, group = cue["mood"].partition(":")
        if group in grouped:
            picks[name] = grouped[group]
            continue
        preferred = FAVORITES[mode].get(name)
        if preferred in catalog and not uses[preferred]:
            row = catalog[preferred]
        else:
            priority = (mood,) + RELATED[mood]
            row = min(catalog.values(), key=lambda item: (
                uses[item["slug"]] + (item["slug"] in reserved and not uses[item["slug"]]),
                priority.index(item.get("mood")) if item.get("mood") in priority else len(priority),
                item["slug"]))
        uses[row["slug"]] += 1
        picks[name] = row
        if group:
            grouped[group] = row
    return picks

def load_music(path, filters, offset):
    cmd = ["ffmpeg", "-v", "error", "-ss", str(offset), "-i", str(path), "-vn",
           "-ar", str(RATE), "-ac", "2"]
    if filters:
        cmd += ["-af", filters]
    cmd += ["-f", "f32le", "-"]
    result = subprocess.run(cmd, check=True, capture_output=True)
    audio = np.frombuffer(result.stdout, "<f4").reshape(-1, 2).copy()
    if len(audio) < RATE * 2:
        raise ValueError(f"Source cue too short: {path.name}")
    finite = np.isfinite(audio)
    audio[~finite] = 0
    return audio

def shape_audio(audio, frames, loop):
    blend = int(RATE * .25)
    desired = frames + (blend if loop else 0)
    if len(audio) >= desired:
        edited = audio[:desired].copy()
    else:
        edited = np.empty((desired, 2), dtype=np.float32)
        step = len(audio) - blend
        up = np.linspace(0, 1, blend, dtype=np.float32)[:, None]
        count = min(len(audio), desired)
        edited[:count] = audio[:count]
        while count < desired:
            offset = count - blend
            stop = min(offset + len(audio), desired)
            edited[offset:count] = edited[offset:count] * (1 - up) + audio[:blend] * up
            edited[count:stop] = audio[blend:stop-offset]
            count = stop
    fade = min(blend, frames // 4)
    up = np.linspace(0, 1, fade, dtype=np.float32)[:, None]
    if loop:
        edited[:fade] = edited[frames:frames + fade] * (1 - up) + edited[:fade] * up
    else:
        edited[:fade] *= up
        edited[frames - fade:frames] *= 1 - up
    return edited[:frames]

def rms(a):
    return float(np.sqrt(np.mean(a.astype(np.float64) ** 2)))

def export(path, audio, level):
    power = max(rms(audio), .0001)
    peak = max(float(np.max(np.abs(audio))), .0001)
    gain = min(10 ** (level / 20) / power, .89 / peak, 5.0)
    pcm = (np.clip(audio * gain, -.999, .999) * 32767).astype("<i2")
    temporary = path.with_suffix(".part")
    pcm.tofile(temporary)
    temporary.replace(path)

def compile_library(cues, sources, destination, mode):
    source = sources / mode
    catalog = json.loads((source / "sources.json").read_text(encoding="utf-8"))
    if len(catalog) < 32:
        raise ValueError(f"{mode} only has {len(catalog)} source tracks.")
    chosen = select_sources(cues, catalog, mode)
    directory = destination / mode / "Cues"
    directory.mkdir(parents=True, exist_ok=True)
    used = Counter()
    rows = []
    for cue in cues:
        name = cue["cue"]
        mood, _, group = cue["mood"].partition(":")
        row = chosen[name]
        key = row["slug"]
        frames = cue["frames"]
        file = directory / (name + ".pcm")
        loop = bool(group) or (frames >= 30 * RATE and mood != "credits" and not name.startswith("epilogue"))
        offset = 2.0 if group else float(2 + used[key] * 6)
        if not file.exists() or file.stat().st_size != frames * 4:
            audio = load_music(source / row["file"], SHAPES.get(name), offset)
            wave = shape_audio(audio, frames, loop)
            level = LEVEL[mood] - (5 if name.endswith("phone") else 0)
            export(file, wave, level)
        used[key] += 1
        rows.append(dict(library=mode, cue=name, title=row["title"], source=row["source"],
                         edit="loop" if loop else "timed", offset=offset, frames=frames))
    print(f"{mode}: {len(rows)} cues from {len(used)} tracks.", flush=True)
    return rows

def main(inventory, sources, destination, modes):
    with inventory.open(encoding="utf-8", newline="") as file:
        cues = list(csv.DictReader(file, delimiter="\t"))
    with inventory.with_name("CueFrames.tsv").open(encoding="utf-8", newline="") as file:
        frame_counts = {row["cue"]: int(row["frames"]) for row in csv.DictReader(file, delimiter="\t")}
    if set(frame_counts) != {cue["cue"] for cue in cues} or any(count <= 0 for count in frame_counts.values()):
        raise ValueError("The replacement frame table must match the cue inventory.")
    for cue in cues:
        cue["frames"] = frame_counts[cue["cue"]]
    for mode in modes:
        rows = compile_library(cues, sources, destination, mode)
        src = destination / mode / "CueCredits.tsv"
        with src.open("w", encoding="utf-8", newline="") as file:
            writer = csv.DictWriter(file, fieldnames=list(rows[0]), delimiter="\t", lineterminator="\n")
            writer.writeheader()
            writer.writerows(rows)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("inventory", type=pathlib.Path)
    parser.add_argument("sources", type=pathlib.Path)
    parser.add_argument("destination", type=pathlib.Path)
    parser.add_argument("--library", choices=MODES)
    args = parser.parse_args()
    main(args.inventory, args.sources, args.destination, [args.library] if args.library else MODES)
