import argparse
import csv
import re
from pathlib import Path

import UnityPy

TAGS = re.compile(r"<(?P<name>playMusic|stopMusic|playVideo|loadVideo)\b(?P<args>[^<>]*)>", re.I)
NAME = re.compile(r"\bname\s*=\s*(?:\"([^\"]*)\"|'([^']*)'|([^\s>]+))", re.I)
STORY_ANCHOR = re.compile(r"<(?:playVO|playAnim|loadDialog|loadScene|go|call|stopMusic)\b[^<>]*>", re.I)

def assets(data):
    files = [data / name for name in ("globalgamemanagers", "resources.assets", "sharedassets0.assets", "sharedassets1.assets")]
    env = UnityPy.load(*(str(file) for file in files))
    for obj in env.objects:
        if obj.type.name != "ResourceManager":
            continue
        for path, reference in obj.read().m_Container:
            if not re.match(r"^(dialogs|menus|scripts)/", path, re.I):
                continue
            item = reference.deref()
            if item.type.name != "TextAsset":
                continue
            payload = item.read().m_Script
            if isinstance(payload, bytes):
                payload = payload.decode("utf-8", "replace")
            yield path, payload.replace("\r\n", "\n").replace("\r", "\n")

def cue_for(args):
    match = NAME.search(args)
    return next((x for x in match.groups() if x is not None), "") if match else ""

def review_stock(data, out):
    out.mkdir(parents=True, exist_ok=True)
    rows = []
    resources = {}
    for path, source in assets(data):
        chapter = re.search(r"(?:^|[/_])ch([1-9])(?:$|[^0-9])", path)
        marks = []
        for m in TAGS.finditer(source):
            action = m["name"].lower()
            name = cue_for(m["args"])
            line = source.count("\n", 0, m.start()) + 1
            preceding = source[max(0, m.start() - 500):m.start()]
            following = source[m.end():m.end() + 500]
            before = STORY_ANCHOR.findall(preceding)
            after = STORY_ANCHOR.findall(following)
            rows.append((path, line, chapter[1] if chapter else "", action, name, m["args"].strip(),
                         before[-1] if before else "", after[0] if after else ""))
            marks.append((action, name))
        resources[path] = marks

    with (out / "ScriptCueInventory.tsv").open("w", encoding="utf-8", newline="") as file:
        csv.writer(file, delimiter="\t", lineterminator="\n").writerows(
            [("resource", "line", "chapter", "action", "cue", "arguments", "preceding_anchor",
              "following_anchor")] + sorted(rows)
        )
    print(f"Reviewed {len(resources)} text assets; {len(rows)} explicit music/video markers "
          f"in {sum(bool(marks) for marks in resources.values())} assets.", flush=True)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    review_stock(args.data, args.output)
