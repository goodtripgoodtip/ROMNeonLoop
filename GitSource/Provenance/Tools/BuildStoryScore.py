import argparse
import base64
import csv
import hashlib
import re
from pathlib import Path

from ReviewScore import TAGS, assets, cue_for

LOCALES = ("en", "jp")
TOKEN = re.compile(r"<[^<>\r\n]+>")
NODE = re.compile(r"^::[ \t]+.*$", re.M)
NAME_FIELD = re.compile(r'(\bname\s*=\s*)(?:"[^"]*"|\'[^\']*\'|[^\s>]+)', re.I)

def table(file):
    with file.open(encoding="utf-8", newline="") as contents:
        return list(csv.DictReader(contents, delimiter="\t"))

def write(file, fields, rows):
    with file.open("w", encoding="utf-8", newline="") as output:
        writer = csv.writer(output, delimiter="\t", lineterminator="\n")
        writer.writerow(fields)
        writer.writerows(rows)

def substituted(tag, target):
    return NAME_FIELD.sub(lambda match: match[1] + '"' + target + '"', tag, count=1)

def named(id, mode):
    return "_romscore_" + mode + "_" + id

def offset_for(text, spec):
    matching = [m for m in TOKEN.finditer(text) if spec["anchor"] in m[0]]
    offset = int(spec["occurrence"]) - 1
    if len(matching) != int(spec["anchor_matches"]) or not 0 <= offset < len(matching):
        raise ValueError(f"{spec['id']} has no unique indexed scene anchor.")
    return matching[offset]

def edits_for(text, spec):
    key, mode = spec["id"], spec["action"]
    anchor = offset_for(text, spec)
    if mode == "pulse":
        return [(anchor.start(), 0, f'<playMusic name="{named(key, "beat")}">')]
    if mode == "enter":
        return [(anchor.start(), 0, f'<playMusic name="{named(key, "enter")}" volume={spec["volume"]} crossFade>')]
    if mode not in ("move-earlier", "move-later"):
        raise ValueError(f"Unexpected story cue action: {mode}")
    earlier = mode == "move-earlier"
    edges = [0] + [m.start() for m in NODE.finditer(text)] + [len(text)]
    start = max(n for n in edges if n <= anchor.start())
    finish = min(n for n in edges if n > anchor.start())
    events = [m for m in TAGS.finditer(text, start, finish) if m["name"].lower() == "playmusic"
              and cue_for(m["args"]).lower() == spec["cue"]]
    candidates = [m for m in events if (m.start() > anchor.end()) == earlier]
    if not candidates:
        raise ValueError(f"{key} has no matching music tag on the same dialogue node.")
    source = min(candidates, key=lambda m: abs(m.start() - anchor.start()))
    destination = anchor.start() if earlier else anchor.end()
    start_check, end_check = sorted((destination, source.start()))
    between = text[start_check:end_check]
    if re.search(r"<(?:stopMusic|break|loadDialog|loadScene)\b", between, re.I):
        raise ValueError(f"{key} would cross an authored music stop or dialogue exit.")
    return [(destination, 0, substituted(source[0], named(key, "move"))),
            (source.start(), len(source[0]), substituted(source[0], named(key, "original")))]

def build(data, source, target, review):
    specs = table(source / "StoryBeats.tsv")
    valid_cues = {row["cue"] for row in table(source / "CueDynamics.tsv")}
    assets_by_key = dict(assets(data))
    missing = {spec["cue"] for spec in specs} - valid_cues
    if missing or len({spec["id"] for spec in specs}) != len(specs):
        raise ValueError(f"Invalid story map cue identifiers: {missing}")

    keys = set()
    edits = []
    report = []
    for spec in specs:
        for locale in LOCALES:
            path = f"dialogs/{locale}/{spec['dialog']}"
            text = assets_by_key[path]
            checksum = hashlib.sha256(text.encode("utf-8")).hexdigest()
            positions = edits_for(text, spec)
            for offset, size, content in positions:
                unit_index = len(text[:offset].encode("utf-16-le")) // 2
                unit_length = len(text[offset:offset + size].encode("utf-16-le")) // 2
                key = (path, unit_index)
                if key in keys:
                    raise ValueError(f"Overlapping adaptive edits in {path}.")
                keys.add(key)
                edits.append((path, checksum, unit_index, unit_length,
                              base64.b64encode(content.encode("utf-8")).decode("ascii")))
            report.append((path, spec["id"], spec["action"], spec["cue"], spec["intent"]))

    counts = {}
    for name, text in assets_by_key.items():
        for match in TAGS.finditer(text):
            cue = cue_for(match["args"]).lower()
            stock_action = match["name"].lower()
            covered = "existing cue and adaptive profile" if cue in valid_cues else "existing script trigger"
            report.append((name, text.count("\n", 0, match.start()) + 1, stock_action, cue, covered))
            counts[stock_action] = counts.get(stock_action, 0) + 1
    target.mkdir(exist_ok=True, parents=True)
    review.mkdir(exist_ok=True, parents=True)
    write(target / "DialogueEdits.tsv", ("resource", "sha256", "offset", "length", "markup_base64"), edits)
    write(review / "StoryCueReview.tsv", ("resource", "anchor", "action", "cue", "reason"), report)
    print(f"{len(specs)} story interventions validated in both locales, "
          f"{len(edits)} safe text insertions or replacements, {sum(counts.values())} original tag dispositions.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=Path)
    parser.add_argument("source", type=Path)
    parser.add_argument("target", type=Path)
    parser.add_argument("review", type=Path)
    args = parser.parse_args()
    build(args.data, args.source, args.target, args.review)
