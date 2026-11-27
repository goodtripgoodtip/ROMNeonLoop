import concurrent.futures
import html
import json
import pathlib
import re
import sys
import urllib.parse

from FetchAudio import get

SCOTT = "https://www.scottbuckley.com.au/library/"
KEVIN = "https://incompetech.com/music/royalty-free/"

def slug(title):
    return re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")

def scott_entries(url, mood):
    page = html.unescape(get(url).decode("utf-8", "replace"))
    entries = []
    for block in re.split(r"<article\b", page)[1:]:
        mp3 = re.search(r'mp3:\s*"(https://www\.scottbuckley\.com\.au/library/[^"]+\.mp3)"', block)
        post = re.search(r'(https://www\.scottbuckley\.com\.au/library/[^/"#]+/)#comments', block)
        name = re.search(r'<h[23][^>]*class="[^"]*post-title[^"]*"[^>]*>(.*?)</h[23]>', block, re.S)
        if mp3:
            track_url = mp3[1]
            label = re.sub(r"<[^>]+>", "", name[1]).strip() if name else ""
            if not label and post:
                label = post[1].strip("/").rsplit("/", 1)[-1].replace("-", " ").title()
            if not label:
                label = urllib.parse.unquote(track_url.rsplit("/", 1)[-1][:-4])
            entries.append((label, post[1] if post else url, track_url, mood))
    return entries

def scott():
    pages = [
        ("instrumentation/synth-atmospherics/", "mystery"),
        ("instrumentation/synth-pad/", "quiet"),
        ("mood/energetic/", "action"),
        ("mood/suspenseful/", "mystery"),
        ("mood/uplifting/", "city"),
        ("instrumentation/synth-bass/", "club"),
        ("mood/emotional/", "credits")
    ]
    with concurrent.futures.ThreadPoolExecutor(max_workers=7) as pool:
        for number in range(1, 5):
            tasks = [(category, pool.submit(scott_entries,
                       SCOTT + category + (f"page/{number}/" if number > 1 else ""), mood))
                     for category, mood in pages]
            for category, task in tasks:
                try:
                    yield from task.result()
                except OSError:
                    continue

def kevin():
    pieces = json.loads(get(KEVIN + "pieces.json"))
    candidates = []
    for track in pieces:
        genre = track["genre"]
        title = track["title"]
        feel = (track.get("feel") or "").lower()
        description = (track.get("description") or "").lower()
        instruments = (track.get("instruments") or "").lower()
        if re.search(r"\b(voice|vocals|singing)\b", instruments):
            continue
        if genre not in {"7", "11", "22"}:
            continue
        if genre == "22" and not re.search(r"detective|spy|crime|electro|synth|mystery|sci.fi|suspense|tension", description):
            continue
        if re.search(r"christmas|halloween|comedy|cartoon|goofy", feel + " " + description):
            continue
        if re.search(r"tension|mysterious|scary|dark|unnerving", feel):
            mood = "mystery"
        elif re.search(r"intense|action|aggressive|driving", feel):
            mood = "action"
        elif re.search(r"calm|relaxed|somber|sad|eerie", feel):
            mood = "quiet"
        elif re.search(r"groov|bouncy|funk", feel):
            mood = "club"
        else:
            mood = "city"
        url = KEVIN + "mp3-royaltyfree/" + urllib.parse.quote(track["filename"])
        source = KEVIN + "index.html?isrc=" + track["isrc"]
        favored = title.lower() in {"space jazz", "on the cool side", "i knew a guy", "spy glass", "hard boiled",
                                    "night on the docks - sax", "lobby time", "consequence", "investigations"}
        rank = 0 if favored else 1 if genre == "11" else 2 if genre == "7" else 3
        candidates.append((rank, (title, source, url, mood)))
    candidates.sort(key=lambda entry: (entry[0], entry[1][0].lower()))
    return [entry for _, entry in candidates]

def save(entry, directory):
    title, source, media, mood = entry
    key = slug(title)
    target = directory / (key + ".mp3")
    if not target.exists():
        payload = get(media)
        if not (payload.startswith(b"ID3") or payload.startswith(b"\xff")):
            raise ValueError(f"{title}: Response was not MP3 audio.")
        tmp = target.with_suffix(".part")
        tmp.write_bytes(payload)
        tmp.replace(target)
    return key, dict(slug=key, title=title, source=source, file=target.name, mood=mood)

def source_all(entries, directory):
    directory.mkdir(parents=True, exist_ok=True)
    index = directory / "sources.json"
    library = json.loads(index.read_text(encoding="utf-8")) if index.exists() else {}
    buckets = {}
    observed = set(library)
    for entry in entries:
        key = slug(entry[0])
        if key in observed:
            continue
        observed.add(key)
        buckets.setdefault(entry[3], []).append(entry)
    maximum = max(map(len, buckets.values()), default=0)
    ordered = [entries[i] for i in range(maximum) for entries in buckets.values() if i < len(entries)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        for start in range(0, len(ordered), 16):
            if len(library) >= 85:
                break
            tasks = [pool.submit(save, entry, directory) for entry in ordered[start:start + 16]]
            for task in tasks:
                try:
                    key, row = task.result()
                    library[key] = row
                except (ValueError, OSError):
                    continue
            index.write_text(json.dumps(library, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(f"{directory.name} {len(library)}", flush=True)

if __name__ == "__main__":
    out = pathlib.Path(sys.argv[2])
    mode = sys.argv[1]
    source_all(scott() if mode == "cinematic-sci-fi" else kevin(), out / mode)
