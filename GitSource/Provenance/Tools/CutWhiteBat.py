import html
import json
import pathlib
import re
import subprocess
import sys

from AcquireAlternates import slug

MIXES = {
    "sunset": ("lab", "https://www.youtube.com/watch?v=g6hY7dB54bc"),
    "algorithm": ("algo", "https://www.youtube.com/watch?v=kANalBOlCOQ")
}

MOODS = {
    "quiet": "sentimental|last-stop|forgotten-planet|rain-city|astral-projection|nightscapes|astronomy|reflections|serenity|saturna|vhs-memory|atmos|echoes|seeking|moonlight|distance|floating|stargazing|horizon|remedy",
    "mystery": "heart-of-night|hackers|empty-city|androids-sentient-beings|glitch-in-reality|night-crawler|escape-from-lv426|night-dweller|virtual-death|darkfall|endless-night|odysseus|worship-the-night|voyager|rendezvous|absynth|agent-cooper|black-cream|lost-city|edge-of-tomorrow",
    "action": "countach|black-rainbows|the-drop|avenger|alliance|melt|into-the-technodome|to-live-and-die-in-la|dangerous|drive|vice|upgrade|legend|feel-the-heat|race-against-sunset",
    "city": "arcadia|new-beginnings|the-traveler|starlink|50-million-year-trip|brave-new-world|genesis|island-sun|fractals|galaxy|promise|sunshine|fantasy|aurora|solara|summer-is-calling|city-of-angels|slice-of-paradise|moment-of-truth|forever|the-saga",
    "club": "miami-sky|calypso|crystal-cola|beach-bum|neon-breeze|solo",
    "credits": "end-credits|the-call|digital-sunset|afterglow"
}

def timestamps(page):
    match = re.search(r'"shortDescription":("(?:[^"\\]|\\.)*")', page)
    if not match:
        raise ValueError("Creator's track listing is missing.")
    description = json.loads(match[1])
    rows = []
    for line in description.splitlines():
        found = re.match(r"^\d+\.\s+(.+?)\s+(?:-\s*)?(\d+(?::\d{2}){1,2})\s*$", line)
        if not found:
            continue
        title, clock = found.groups()
        seconds = 0
        for part in clock.split(":"):
            seconds = seconds * 60 + int(part)
        rows.append((seconds, html.unescape(title.strip())))
    return rows

def build(root, target):
    target.mkdir(parents=True, exist_ok=True)
    moods = {id: mood for mood, names in MOODS.items() for id in names.split("|")}
    rows = {}
    for mix, (metadata, url) in MIXES.items():
        tracklist = timestamps((root / (metadata + ".html")).read_text(encoding="utf-8"))
        path = next((root / (mix + ext) for ext in (".webm", ".m4a") if (root / (mix + ext)).exists()), None)
        if path is None:
            raise FileNotFoundError(mix + " audio is missing")
        for i, (start, title) in enumerate(tracklist):
            key = slug(title)
            if key in rows:
                continue
            file = target / (key + ".flac")
            if not file.exists():
                command = ["ffmpeg", "-y", "-v", "error", "-ss", str(start + .7), "-i", str(path), "-vn"]
                if i + 1 < len(tracklist):
                    command += ["-t", str(tracklist[i + 1][0] - start - 2.0)]
                command += ["-ac", "2", "-ar", "44100", "-sample_fmt", "s16", str(file)]
                subprocess.run(command, check=True)
            rows[key] = dict(slug=key, title=title, file=file.name, source=url + "&t=" + str(start),
                             mood=moods.get(key, "city"))
    (target / "sources.json").write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(rows)} White Bat Audio source tracks.", flush=True)

if __name__ == "__main__":
    build(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]))
