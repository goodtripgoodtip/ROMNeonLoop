# Installer source

If you want to play, [download the Windows installer](../../README.md#start-playing).

<details>
<summary>How to update the shared installer code</summary>

NeonLoop compiles the Core sources and shared WinForms controls from [Game Setup Kit 0.8.1](https://github.com/goodtripgoodtip/GameSetupKit/releases/tag/v0.8.1). The kit supplies Steam discovery, verified downloads, and common setup controls. NeonLoop owns its wizard, game preparation, package format, and installed-file records.

The [source archive](https://github.com/goodtripgoodtip/GameSetupKit/releases/download/v0.8.1/GameSetupKit-Source-v0.8.1.zip) has SHA-256 `7a6e7baef54e0286263e3db98e3da648c9dc8a8c086ec748dce58e825c650974`. Import `src/GameInstaller.Core/*.cs` and `InstallerChoices.cs`, `InstallerPathPicker.cs`, `InstallerTheme.cs`, and `InstallerWindow.cs` from `src/GameInstaller.WinForms`.

Keep the [kit’s MIT license](LICENSE) with the source and the installer. Keep NeonLoop’s `HOBBY-TERMS.txt` embedded as `GameInstaller.HobbyTerms.txt`. Those product terms also name the GPL-covered DSP and preserve the separate music licenses.

</details>
