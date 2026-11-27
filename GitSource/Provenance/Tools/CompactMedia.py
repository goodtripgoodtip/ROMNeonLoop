import concurrent.futures
import ctypes
import json
import pathlib
import subprocess
import sys

pack, output = (pathlib.Path(path) for path in sys.argv[1:3])
ffmpeg = pathlib.Path(r"C:\ffmpeg\bin\ffmpeg.exe")
ffprobe = ffmpeg.with_name("ffprobe.exe")


def encode(target, args):
    if target.is_file():
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.stem + ".partial" + target.suffix)
    subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-y", *args, partial], check=True)
    partial.replace(target)


def cue(source):
    dest = output / source.relative_to(pack).with_suffix(".mp3")
    encode(dest, ["-f", "s16le", "-ar", "44100", "-ac", "2", "-i", source,
                  "-c:a", "libmp3lame", "-b:a", "160k", "-write_xing", "1"])
    if dest.stat().st_size >= source.stat().st_size:
        raise ValueError(f"Cue did not get smaller: {source}")


def validate_cues(inputs):
    decoder = ctypes.CDLL(str(output / "RomScoreDSP.dll"))
    decoder.RomMp3Open.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_int)]
    decoder.RomMp3Open.restype = ctypes.c_void_p
    decoder.RomMp3Read.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_float),
                                  ctypes.c_int, ctypes.c_int]
    decoder.RomMp3Read.restype = ctypes.c_int
    decoder.RomMp3Close.argtypes = [ctypes.c_void_p]
    decoder.RomMp3Close.restype = None
    lengths = {}
    for source in inputs:
        dest = output / source.relative_to(pack).with_suffix(".mp3")
        frames = ctypes.c_int()
        state = decoder.RomMp3Open(str(dest), ctypes.byref(frames))
        if not state:
            raise ValueError(f"Could not open the compressed cue: {dest}")
        try:
            expected = source.stat().st_size // 4
            if not 0 <= frames.value - expected <= 64:
                raise ValueError(f"Compressed cue length changed: {source}")
            if lengths.setdefault(source.stem, expected) != expected:
                raise ValueError(f"Cue lengths differ across libraries: {source}")
            length = min(expected, 2048)
            samples = (ctypes.c_float * (length * 2))()
            for start in (0, (expected - length) // 2, expected - length, 0):
                if decoder.RomMp3Read(state, start, samples, 0, length) != length * 2:
                    raise ValueError(f"Unable to decode frames at {start}: {source}")
        finally:
            decoder.RomMp3Close(state)
    target = output / "CueFrames.tsv"
    target.write_text("cue\tframes\n" + "".join(f"{cue}\t{count}\n" for cue, count in sorted(lengths.items())),
                      encoding="utf-8")
    print(f"Validated the original playable frame count and seek points for {len(inputs)} cues.", flush=True)


def info(path):
    result = subprocess.check_output([ffprobe, "-v", "error", "-show_entries",
        "stream=codec_type,codec_name,width,height,start_time:format=duration", "-of", "json", path])
    data = json.loads(result)
    return (float(data["format"]["duration"]), data["streams"])


def video(sources):
    reference = sources[0]
    _, streams = info(reference)
    width = next(stream["width"] for stream in streams if stream["codec_type"] == "video")
    height = next(stream["height"] for stream in streams if stream["codec_type"] == "video")
    scale = min(1, 1280 / width, 720 / height)
    w, h = (max(2, int(edge * scale / 2) * 2) for edge in (width, height))
    frame_track = output / "Pictures" / reference.name
    encode(frame_track, ["-i", reference, "-map", "0:v:0", "-an", "-vf", f"scale={w}:{h}",
                         "-c:v", "libvpx", "-pix_fmt", "yuv420p", "-threads", "4",
                         "-deadline", "good", "-cpu-used", "6", "-b:v", "950k", "-crf", "31"])
    for source in sources:
        dest = output / source.relative_to(pack)
        encode(dest, ["-i", frame_track, "-i", source, "-map", "0:v:0", "-map", "1:a:0",
                      "-map_metadata", "1", "-c", "copy"])
        before, old = info(source)
        after, new = info(dest)
        if (abs(before - after) > .085 or
                [s["codec_name"] for s in old] != [s["codec_name"] for s in new] or
                (new[0]["width"], new[0]["height"]) != (w, h)):
            raise ValueError(f"Codec or timing mismatch: {source}")
        if dest.stat().st_size >= source.stat().st_size:
            dest.unlink()
            encode(dest, ["-i", source, "-map", "0", "-c", "copy"])
    print("Completed", reference.name, flush=True)


if sys.argv[3] == "verify":
    validate_cues(list(pack.glob("*/Cues/*.pcm")))
elif sys.argv[3] == "audio":
    inputs = list(pack.glob("*/Cues/*.pcm"))
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(cue, inputs))
    print(f"Encoded {len(inputs)} cues.", flush=True)
else:
    groups = {}
    for source in sorted(pack.glob("*/*/**/*.webm"), key=lambda path: (len(path.parts), path.name)):
        groups.setdefault(source.name, []).append(source)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(video, sorted(groups.values(), key=lambda paths: paths[0].stat().st_size, reverse=True)))
