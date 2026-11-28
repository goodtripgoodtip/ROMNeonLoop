import argparse
import json
import shutil
import subprocess
from pathlib import Path
from threading import Event, Thread

import numpy as np
import torch

from BuildDynamicVideos import VARIANTS, native, process_video, table
from RemixVideos import score_video
from SplitVideos import pending_stems, separate


def progress(done, total, label):
    print(f"PROGRESS\t{done}\t{total}\t{label}", flush=True)

def mixes_ready(output, names):
    return all(ready_file(file) for name in names for file in mixes(output, name))


def mixes(output, name):
    return [output / "Videos" / name] + [output / "DynamicVideos" / mode / name for mode in VARIANTS]


def ready_file(file):
    return file.is_file() and file.stat().st_size > 1024


def probe(source):
    data = subprocess.check_output(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", source]
    )
    return json.loads(data)


def picture(source, output):
    if output.is_file() and output.stat().st_size > 1024:
        return
    stream = next(stream for stream in probe(source)["streams"] if stream["codec_type"] == "video")
    width, height = stream["width"], stream["height"]
    scale = min(1, 1280 / width, 720 / height)
    width, height = (max(2, int(edge * scale / 2) * 2) for edge in (width, height))
    partial = output.with_suffix(".partial.webm")
    subprocess.run([
        "ffmpeg", "-nostdin", "-y", "-v", "error", "-i", source, "-map", "0:v:0", "-an",
        "-vf", f"scale={width}:{height}", "-c:v", "libvpx", "-pix_fmt", "yuv420p",
        "-threads", "4", "-deadline", "good", "-cpu-used", "6", "-b:v", "950k", "-crf", "31", partial
    ], check=True)
    partial.replace(output)


def render_scene(name, videos, pictures, stems, output, cues, spec, profiles, dsp, completed=None):
    frame_track = pictures / name
    if not ready_file(frame_track):
        picture(videos / name, frame_track)
        if completed:
            completed()
    original_mix = output / "Videos" / name
    if not original_mix.is_file() or original_mix.stat().st_size <= 1024:
        with np.load(stems / (Path(name).stem + ".npz")) as stats:
            score_video(
                frame_track, stems / (Path(name).stem + "-dialogue-effects.flac"), stats,
                cues / (spec["cue"] + ".mp3"), original_mix
            )
        if completed:
            completed()
    process_video(frame_track, spec, profiles, dsp, stems, output, cues, completed)


def generate(game, pack, cache, library, checkpoint, device, remaining_libraries=()):
    source = Path(__file__).resolve().parent
    video_specs = {row["video"]: row for row in table(source / "VideoDynamics.tsv")}
    video_list = table(source / "Videos.tsv")
    profiles = {row["cue"]: row for row in table(pack / "CueDynamics.tsv")}
    videos = game / "ROM_Data" / "StreamingAssets" / "Videos" / "WebM"
    names = {file.name for file in videos.glob("*.webm")}
    if set(video_specs) != names or {row["video"] for row in video_list} != names:
        raise ValueError("The Steam installation does not contain the expected cutscenes.")
    active = [row["video"] for row in video_list if row["disposition"] == "rescored"]
    cues = pack / library / "Cues"
    if any(not (cues / (video_specs[name]["cue"] + ".mp3")).is_file() for name in active):
        raise ValueError("The soundtrack is incomplete. Extract its ZIP again.")
    if any(row["replacement_cue"] != video_specs[row["video"]]["cue"] for row in video_list
           if row["video"] in active):
        raise ValueError("The cutscene and soundtrack cue lists disagree.")
    output = cache / library
    pictures = cache / "Pictures"
    stems = cache / "Dialogue"
    for folder in (cache, pictures, output):
        folder.mkdir(parents=True, exist_ok=True)
    marker = output / ".ready"
    if marker.is_file() and mixes_ready(output, active):
        progress(1, 1, "Cutscenes are ready")
        return
    if marker.is_file():
        progress(0, 0, "Clearing a stale readiness marker before repairing the missing cutscenes")
        marker.unlink()
    if shutil.disk_usage(cache).free < 2 * 1024 ** 3:
        raise OSError("Free at least 2 GiB for this soundtrack's cutscenes.")

    progress(0, 0, "Loading the audio separator")
    automatic = device == "auto"
    if automatic:
        device = "cuda" if torch.cuda.is_available() else "cpu"
    processor = torch.cuda.get_device_name() if device == "cuda" else "CPU"
    print(f"Audio separator processor: {processor} ({device}).", flush=True)

    weights = {name: max(1, round(float(probe(videos / name)["format"]["duration"])))
               for name in sorted(names)}
    separation_weights = {name: max(seconds, 15) for name, seconds in weights.items()}
    pending = {video.name for video in pending_stems(videos, stems)}
    separation_total = sum(separation_weights[name] for name in pending)
    libraries = tuple(dict.fromkeys((library, *remaining_libraries)))
    render_total = sum(
        weights[name] * (int(not ready_file(pictures / name)) +
                         sum(not ready_file(file) for key in libraries for file in mixes(cache / key, name)))
        for name in active
    )
    print(f"CUTSCENE\tplan\t{separation_total}\t{render_total}\t{device}", flush=True)

    dsp = native(pack / "RomScoreDSP.dll")
    render_done = 0

    def render_one(name):
        def report():
            print(f"CUTSCENE\trender\t{render_done}\t{render_total}\t"
                  f"Rendering {library} cutscenes · {Path(name).stem}", flush=True)

        def completed():
            nonlocal render_done
            render_done += weights[name]
            report()

        if ready_file(pictures / name) and mixes_ready(output, [name]):
            return
        report()
        render_scene(name, videos, pictures, stems, output, cues, video_specs[name], profiles, dsp, completed)

    sample = min((name for name in active if not mixes_ready(output, [name])),
                 key=lambda name: (name in pending, weights[name]), default=None)
    if sample is not None and sample not in pending:
        render_one(sample)

    separation_done = 0
    def separation_progress(video, finished):
        nonlocal separation_done
        if finished:
            separation_done += separation_weights[video.name]
        print(f"CUTSCENE\tseparate\t{separation_done}\t{separation_total}\t"
              f"Separating dialogue and effects from original music · {video.stem}", flush=True)
        if finished and video.name == sample and render_done == 0:
            render_one(sample)

    try:
        separate(videos, stems, checkpoint, device, 4.0, separation_progress, first=sample)
    except RuntimeError as error:
        if device != "cuda" or not any(word in str(error).lower() for word in ("cuda", "cudnn", "cublas")):
            raise
        if not automatic:
            print(f"CUDA failed: {error}", flush=True)
            raise SystemExit(3) from error
        progress(0, 0, "Graphics processor unavailable. Preparing cutscenes on the CPU.")
        torch.cuda.empty_cache()
        print(f"CUTSCENE\tplan\t{separation_total - separation_done}\t{render_total - render_done}\tcpu", flush=True)
        separation_total -= separation_done
        render_total -= render_done
        separation_done = 0
        render_done = 0
        separate(videos, stems, checkpoint, "cpu", 4.0, separation_progress, first=sample)

    for name in active:
        render_one(name)
    if not mixes_ready(output, active):
        raise OSError("The generated cutscene mixes are incomplete.")
    pending = output / ".ready.part"
    pending.write_text("1\n", encoding="utf-8")
    pending.replace(marker)
    progress(len(active), len(active), "Cutscenes are ready")


def heartbeat(stopped):
    while not stopped.wait(15):
        print("HEARTBEAT", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("game", type=Path)
    parser.add_argument("pack", type=Path)
    parser.add_argument("cache", type=Path)
    parser.add_argument("library", choices=("dark-synthwave", "cinematic-sci-fi", "noir-jazz"))
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    parser.add_argument("--remaining-libraries", nargs="*", default=())
    args = parser.parse_args()
    stopped = Event()
    Thread(target=heartbeat, args=(stopped,), daemon=True).start()
    try:
        generate(args.game, args.pack, args.cache, args.library, args.checkpoint, args.device, args.remaining_libraries)
    finally:
        stopped.set()
