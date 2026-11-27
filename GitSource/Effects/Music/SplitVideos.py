import argparse
import pathlib
import subprocess

import numpy as np
import soundfile as sf
from bandit_infer import BanditSession
from bandit_infer._v2.inference import StandardTensorChunkedInferenceHandler

RATE = 48000
WEIGHT_SHA256 = "abcfccf65446752a057f4a302c941479a54b7560ebf8d7bca039d2ea98e64cfc"

def read_audio(video):
    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(video), "-vn", "-ac", "2",
                             "-ar", str(RATE), "-f", "f32le", "-"], check=True, capture_output=True)
    return np.frombuffer(result.stdout, dtype="<f4").reshape(-1, 2)

def rms_track(audio, block):
    count = int(np.ceil(len(audio) / block))
    padded = np.pad(audio, ((0, count * block - len(audio)), (0, 0)))
    return np.sqrt(np.mean(padded.reshape(count, block, 2) ** 2, axis=(1, 2)))

def separate(videos, staging, checkpoint, device, hop, progress=None):
    staging.mkdir(parents=True, exist_ok=True)
    files = sorted(videos.glob("*.webm"), key=lambda p: p.stat().st_size)
    pending = [video for video in files if not (
        (staging / (video.stem + ".npz")).is_file() and
        (staging / (video.stem + "-dialogue-effects.flac")).is_file()
    )]
    if not pending:
        return
    with BanditSession("v2-multi", device=device, checkpoint_path=checkpoint,
                       checkpoint_sha256=WEIGHT_SHA256) as session:
        if hop != 1:
            runtime = session._runtime
            runtime.handler = StandardTensorChunkedInferenceHandler(
                chunk_size_seconds=8.0, hop_size_seconds=hop, inference_batch_size=1, fs=RATE
            ).to(runtime.device).eval()
        for index, video in enumerate(pending, len(files) - len(pending)):
            if progress:
                progress(index, len(files), "Separating dialogue and music")
            result = staging / (video.stem + ".npz")
            original = read_audio(video)
            padded = np.pad(original, ((0, max(0, 15 * RATE - len(original))), (0, 0)))
            stems = session.infer(padded.T, sample_rate=RATE)
            music = np.ascontiguousarray(stems["music"].T[:len(original)])
            speech = np.ascontiguousarray(stems["speech"].T[:len(original)])
            effects = np.ascontiguousarray(stems["effects"].T[:len(original)])
            stem_sum = speech + effects
            block = RATE // 4
            music_rms = rms_track(music, block)
            speech_rms = rms_track(speech, block)
            effects_rms = rms_track(effects, block)
            original_rms = rms_track(original, block)
            subtraction = original - music
            speech_risk = np.maximum(speech_rms, effects_rms * .65)
            ratio = speech_risk / np.maximum(music_rms, 1e-6)
            retain = np.clip((ratio - .45) * .15, 0, .22)
            positions = np.arange(len(original))
            keep = np.interp(positions, (np.arange(len(retain)) + .5) * block, retain)
            dry = subtraction + music * keep[:, None]
            dry = np.where(np.isfinite(dry), dry, stem_sum)
            dry_target = staging / (video.stem + "-dialogue-effects.flac")
            dry_partial = dry_target.with_suffix(".partial.flac")
            sf.write(dry_partial, dry, RATE, subtype="PCM_24")
            dry_partial.replace(dry_target)
            tmp = result.with_suffix(".part")
            with tmp.open("wb") as file:
                np.savez(file, frames=len(original), music_rms=music_rms, speech_rms=speech_rms,
                         effects_rms=effects_rms, original_rms=original_rms, retain=retain, block=block)
            tmp.replace(result)
            print(f"{video.name} {len(original) / RATE:.1f}s "
                  f"music:{np.max(music_rms):.4f} speech:{np.max(speech_rms):.4f}", flush=True)
        if progress:
            progress(len(files), len(files), "Separated dialogue and music")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("videos", type=pathlib.Path)
    parser.add_argument("staging", type=pathlib.Path)
    parser.add_argument("checkpoint", type=pathlib.Path)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--hop", type=float, choices=(1.0, 2.0, 4.0), default=4.0)
    args = parser.parse_args()
    separate(args.videos, args.staging, args.checkpoint, args.device, args.hop)
