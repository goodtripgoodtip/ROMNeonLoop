# Build NeonLoop

If you just want to play, use the [Windows installer](../README.md#start-playing).

You need 64-bit Windows, the .NET 10 SDK, PowerShell, and Visual Studio with **Desktop development with C++** and a Windows SDK. A full build also needs the Steam Windows version of **2064: Read Only Memories** (build 23592892). The launcher publishes as a self-contained Windows x64 app. The in-game assemblies still use their original compiler and game references.

From PowerShell in this folder, set the path to your copy of the game and build:

~~~powershell
.\Build.ps1 -SteamGame 'C:\Program Files (x86)\Steam\steamapps\common\Read Only Memories'
~~~

Run **out/Play with Effects.exe**. The launcher copies the Steam game privately and applies its assembly patch with the bundled .NET runtime. Easy Install preselects Dark Synthwave, and fresh installs leave cutscene conversion unchecked. After installation, the Gameplay options page applies the recommended settings to a fresh install. Returning players keep their existing settings.

The build includes the launcher, effects add-on, shaders, audio processor, and score patcher. Optional cutscene conversion installs processing tools on demand. A compatible NVIDIA GPU can speed up audio separation with CUDA. Setup asks before using the slower CPU path and reports separation and rendering times separately. Players can start the game or skip unfinished conversion once the game and soundtrack are ready. [Cutscene setup script](Effects/Music/SetupCutscenes.ps1) · [Pinned downloads](Effects/Music/DownloadAssets.json)

For a manual output path, pass **-Output** pointing to a new folder. The build never needs a copy of the original mod’s dist folder or an existing cutscene cache.

For a launcher-only refresh, run `.\Build.ps1 -LauncherOnly -Output '<new folder>'`. It produces the launcher, cutscene setup script, release terms, and runtime notices. Overlay those files on the matching NeonLoop release. When pointed at an existing clean payload, this command also refreshes its soundtrack download links. If the runtime is already cached, pass `-NuGetConfig .\NuGet.Offline.Config` to build without contacting package sources. Use `-LauncherRuntimeVersion` if you need to select a specific cached runtime version.

The launcher compiles the vendored [Game Setup Kit 0.8.1 source](InstallerShared/README.md) directly. A separate kit installation is unnecessary.

The soundtrack provenance records the original cue-generation order, source URLs, local input checksums, and cue credits. [Soundtrack rebuild reference](Provenance/README.md)

## Package the Windows installer

Build to a clean folder that has never been used to play NeonLoop. On PowerShell 7, run:

```powershell
.\Package-Setup.ps1 -Build .\out -Output .\NeonLoop-Setup.exe -Version v0.6.9
```

The standard EXE contains the base mod and DSP source. Add cleared soundtrack ZIPs as separate release assets. Each ZIP contains `Effects/bin/MusicPack/<key>/CueCredits.tsv` and one MP3 per cue under `Cues/`. Update `Effects/Music/ReleaseAssets.json` with the release URL, ZIP byte size, total expanded file size, and SHA-256 before building. The launcher uses this list for verified downloads and imports.

Build in a fresh folder and package only the files in [PackagePayload.tsv](PackagePayload.tsv). Update dependency pins from the reviewed upstream files when upgrading them. The installer generates the patched assembly and remixed cutscenes locally from the supported Steam installation. Keep generated files and gameplay captures outside the installer and source archives.

For the separate offline installer, put the three soundtrack ZIPs and every entry in `DownloadAssets.json` except the `cu128` wheel in a staging folder. Run the same packaging command with `-Output .\NeonLoop-Setup-Offline.exe -OfflineAssets '<staging folder>'`. Packaging validates the byte sizes and checksums. Setup extracts only selected packs and uses bundled CPU tools to build cutscenes locally. Confirm the bundled archives’ redistribution conditions before releasing the offline candidate.
