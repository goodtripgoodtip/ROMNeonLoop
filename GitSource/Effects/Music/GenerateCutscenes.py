import argparse
import json
import shutil
import subprocess
from pathlib import Path

import numpy as np
import torch

from BuildDynamicVideos import VARIANTS, native, process_video, table
from RemixVideos import score_video
from SplitVideos import separate


def progress(done, total, label):
    print(f"PROGRESS\t{done}\t{total}\t{label}", flush=True)

def mixes_ready(output, names):
    folders = [output / "Videos"] + [output / "DynamicVideos" / mode for mode in VARIANTS]
    return all(file.is_file() and file.stat().st_size > 1024
               for folder in folders for file in (folder / name for name in names))


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


def render_scene(name, videos, pictures, stems, output, cues, spec, profiles, dsp):
    frame_track = pictures / name
    picture(videos / name, frame_track)
    original_mix = output / "Videos" / name
    if not original_mix.is_file() or original_mix.stat().st_size <= 1024:
        with np.load(stems / (Path(name).stem + ".npz")) as stats:
            score_video(
                frame_track, stems / (Path(name).stem + "-dialogue-effects.flac"), stats,
                cues / (spec["cue"] + ".mp3"), original_mix
            )
    process_video(frame_track, spec, profiles, dsp, stems, output, cues)


def generate(game, pack, cache, library, checkpoint, device):
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
    try:
        separate(videos, stems, checkpoint, device, 4.0, progress)
    except RuntimeError as error:
        if device != "cuda" or not any(word in str(error).lower() for word in ("cuda", "cudnn", "cublas")):
            raise
        if not automatic:
            print(f"CUDA failed: {error}", flush=True)
            raise SystemExit(3) from error
        progress(0, 0, "Graphics processor unavailable. Preparing cutscenes on the CPU.")
        torch.cuda.empty_cache()
        separate(videos, stems, checkpoint, "cpu", 4.0, progress)
    dsp = native(pack / "RomScoreDSP.dll")
    progress(0, len(active), "Mixing cutscenes and effects")
    for index, name in enumerate(active, 1):
        render_scene(name, videos, pictures, stems, output, cues, video_specs[name], profiles, dsp)
        progress(index, len(active), "Mixing cutscenes and effects")
    if not mixes_ready(output, active):
        raise OSError("The generated cutscene mixes are incomplete.")
    pending = output / ".ready.part"
    pending.write_text("1\n", encoding="utf-8")
    pending.replace(marker)
    progress(len(active), len(active), "Cutscenes are ready")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("game", type=Path)
    parser.add_argument("pack", type=Path)
    parser.add_argument("cache", type=Path)
    parser.add_argument("library", choices=("dark-synthwave", "cinematic-sci-fi", "noir-jazz"))
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    args = parser.parse_args()
    generate(args.game, args.pack, args.cache, args.library, args.checkpoint, args.device)
