# Where the music comes from

If you’re here to play, use the [Windows installer](../../README.md#download-and-play). Add a pack through [optional music packs](../../README.md#optional-music-packs) and choose it in **F10 → Music**.

[Music credits](../Effects/Vendor/Notices/Soundtracks.md)

<details>
<summary>Soundtrack rebuild instructions for contributors</summary>
NeonLoop targets Steam build **23592892**. Keep the Steam installation read-only. Use the launcher build in [Build NeonLoop](../BUILD.md) and prepare replacement media in a separate working directory.

- **Identify the music sources.** The three `Sources/<library>/sources.json` catalogs preserve track titles, source URLs, moods, and input filenames. `CueCredits.tsv` beside each catalog records the track, edit, offset, and frame count for all 79 mapped cues. [Source input sizes and SHA-256 checksums](SourceInputs.tsv)
- **Recover the audio inputs.** For Dark Synthwave, `Tools/CutWhiteBat.py` records the two compilation URLs and the slice recipe. Supply their audio and timestamp pages under the filenames in the script. For Cinematic Sci-Fi and Noir Jazz, `Tools/AcquireAlternates.py` records the catalog and download routines. Use the saved catalogs for cue selection and compare downloaded or regenerated inputs with `SourceInputs.tsv`. The checksums describe the historical local files, including the FLAC slices produced from the compilations. [White Bat Audio usage terms](https://whitebataudio.com/pages/faq) · [Scott Buckley library](https://www.scottbuckley.com.au/library/) · [Kevin MacLeod licensing](https://incompetech.com/music/royalty-free/licenses/)
- **Generate gameplay music.** Run `Tools/BuildCues.py` with the saved cue inventory, a source directory containing the three catalog folders and audio inputs, and a distinct output directory. It produces stereo 44.1 kHz PCM at the cue lengths and applies the original filters, loop edits, and levels. Run `Tools/CompactMedia.py` in `audio` mode with that output as its source and another directory as its destination. It encodes 160 kbps MP3s. Use `verify` mode with the same folders and the compiled `RomScoreDSP.dll` in the MP3 output directory to check playable frame counts and write `CueFrames.tsv`. [Cue inventory](../Effects/Music/Cues.tsv)
- **Review dialogue matching locally.** Run `Tools/BuildStoryScore.py` with the installed game's `ROM_Data`, the current `Effects/Music` directory, and two private output folders. The tool can extract script metadata for comparison. Keep its output outside the build and source snapshot. NeonLoop reads the active dialogue from the player's game at runtime. [Story beats](../Effects/Music/StoryBeats.tsv)
- **Prepare the cutscenes.** Use the current launcher’s **Prepare Cutscenes** action for each installed library. Its dependency manifest records the download URLs, versions, sizes, and hashes for the CPU and NVIDIA paths. `Tools/pin-downloads.py` records how the manifest was generated. The cutscene recipe uses the installed Steam videos, the Bandit dialogue and effects separation, and the generated MP3 cues. [Pinned downloads](../Effects/Music/DownloadAssets.json) · [Cutscene recipe](../../AGENTS.md#cutscene-recipe)

</details>

