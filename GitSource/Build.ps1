param(
    [string] $SteamGame,
    [string] $Output = (Join-Path $PSScriptRoot 'out'),
    [switch] $LauncherOnly,
    [string] $NuGetConfig,
    [string] $LauncherRuntimeVersion
)

$ErrorActionPreference = 'Stop'
$effects = Join-Path $PSScriptRoot 'Effects'
$vendor = Join-Path $effects 'Vendor'
$music = Join-Path $effects 'Music'
$gameplay = Join-Path $effects 'Gameplay'
$outputRoot = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath (Join-Path $outputRoot 'Effects\bin\Game')) {
    throw 'Choose a fresh build folder. This folder already contains a local game copy.'
}
if (!$LauncherOnly -and (Test-Path -LiteralPath $outputRoot) -and
    (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw 'Choose an empty output folder for a full build.'
}
$target = Join-Path $outputRoot 'Effects'
$runtime = Join-Path $target 'bin\Runtime'
$pack = Join-Path $target 'bin\MusicPack'
$setup = Join-Path $target 'CutsceneSetup'
$work = Join-Path $PSScriptRoot '.build'

function Publish-Launcher {
    $project = Join-Path $PSScriptRoot 'NeonLoop.Launcher.csproj'
    $arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:NuGetAudit=false', '-o', $outputRoot)
    if ($NuGetConfig) { $arguments += @('--configfile', [IO.Path]::GetFullPath($NuGetConfig)) }
    if ($LauncherRuntimeVersion) { $arguments += "-p:RuntimeFrameworkVersion=$LauncherRuntimeVersion" }
    & dotnet @arguments
    if ($LASTEXITCODE) { throw 'The .NET 10 launcher could not be published.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'InstallerShared\LICENSE'), (Join-Path $PSScriptRoot 'InstallerShared\HOBBY-TERMS.txt'),
        (Join-Path $PSScriptRoot 'Start Here.txt') -Destination $outputRoot -Force
    [IO.Directory]::CreateDirectory($setup) | Out-Null
    Copy-Item -LiteralPath (Join-Path $music 'SetupCutscenes.ps1') -Destination $setup -Force
    if (Test-Path -LiteralPath (Join-Path $setup 'ReleaseAssets.json')) {
        Copy-Item -LiteralPath (Join-Path $music 'ReleaseAssets.json') -Destination $setup -Force
    }
    $assets = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'obj\project.assets.json') | ConvertFrom-Json
    $notices = Join-Path $outputRoot 'Third-party notices'
    [IO.Directory]::CreateDirectory($notices) | Out-Null
    Copy-Item -LiteralPath (Join-Path $vendor 'Notices\Components.md') -Destination $notices -Force
    foreach ($name in 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
        $library = $assets.libraries.PSObject.Properties | Where-Object Name -like "$name/*" | Select-Object -First 1
        $packagePath = if ($library) { $library.Value.path } else {
            $download = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
                Where-Object name -eq $name | Select-Object -First 1
            if (!$download) { throw "Runtime package notices could not be located: $name." }
            $version = $download.version.Trim('[',']').Split(',')[0].Trim()
            Join-Path $name.ToLowerInvariant() $version
        }
        $package = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $packagePath } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (!$package) { throw "Runtime package directory could not be located: $name." }
        Get-ChildItem -LiteralPath $package -File | Where-Object Name -match '^(LICENSE(?:\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices "$name $($_.BaseName).txt") -Force }
    }
}

if ($LauncherOnly) {
    Publish-Launcher
    Write-Output "Built launcher update in $outputRoot"
    exit 0
}

if (!$SteamGame) { throw 'Provide -SteamGame for a full source build, or choose -LauncherOnly.' }
$managed = Join-Path (Resolve-Path -LiteralPath $SteamGame).Path 'ROM_Data\Managed'
$runtime, $pack, $setup, $work | ForEach-Object { [IO.Directory]::CreateDirectory($_) | Out-Null }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with Desktop development with C++.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'The Visual Studio x64 C++ build tools are missing.' }
$developer = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$crtNames = 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll',
    'msvcp140_codecvt_ids.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'concrt140.dll'
$crt = Get-ChildItem -Path (Join-Path $vs 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory |
    Sort-Object { [version]$_.Parent.Parent.Name } -Descending | Select-Object -First 1
if (!$crt) { throw 'Install the Visual Studio x64 C++ Redistributable build component to bundle the private audio runtime DLLs.' }
$audioRuntime = Join-Path $setup 'NativeRuntime'
[IO.Directory]::CreateDirectory($audioRuntime) | Out-Null
foreach ($name in $crtNames) {
    Copy-Item -LiteralPath (Join-Path $crt.FullName $name) -Destination $audioRuntime -Force
}
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$cecil = Join-Path $vendor 'Mono.Cecil\Mono.Cecil.dll'
$imgui = Join-Path $vendor 'imgui-3912b3d9a9c1b3f17431aebafd86d2f40ee6e59c'
$reshade = Join-Path $vendor 'reshade-6.8.0\include'
$rubberBand = Join-Path $vendor 'rubberband-4.0.0'
$minimp3 = Join-Path $vendor 'minimp3'

$batch = @"
@echo off
call "$developer" > nul
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /LD /MT /O2 /W4 /DNOMINMAX /I"$imgui" /I"$reshade" /I"$vendor" "$effects\Overlay.cpp" "$effects\Profile.cpp" "$effects\GameplayMenu.cpp" "$effects\Presentation.cpp" /link user32.lib shell32.lib /OUT:"$runtime\ROMEffects.addon64"
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /LD /MT /O2 /DNOMINMAX /I"$rubberBand" /I"$minimp3" "$music\ScoreDsp.cpp" "$music\Mp3Score.cpp" "$rubberBand\single\RubberBandSingle.cpp" /link /OUT:"$pack\RomScoreDSP.dll"
"@
$batchFile = Join-Path $work 'compile.cmd'
[IO.File]::WriteAllText($batchFile, $batch)
Push-Location $work
try {
    & $batchFile
    if ($LASTEXITCODE) { throw 'A native build failed.' }
} finally {
    Pop-Location
}

Publish-Launcher

$scoreSources = 'ReplacementScore.cs', 'OverlayInput.cs', 'ScoreControls.cs', 'NativeScore.cs',
    'CueDynamics.cs', 'PcmScoreVoice.cs', 'ScoreDirector.cs', 'StoryScore.cs', 'StoryCueMatcher.cs' | ForEach-Object { Join-Path $music $_ }
$scoreSources += 'GameAccess.cs', 'GameplayHooks.cs', 'GameplaySaves.cs', 'GameplayExplore.cs', 'GameplayHud.cs',
    'GameplayProgress.cs', 'GameplayHudArt.cs', 'GameplayFont.cs', 'GameplayFontAtlas.cs', 'WindowsFontRaster.cs' |
    ForEach-Object { Join-Path $gameplay $_ }
$references = 'mscorlib.dll', 'netstandard.dll', 'System.Core.dll', 'System.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.AudioModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.Physics2DModule.dll' |
    ForEach-Object { "/r:$managed\$_" }
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ "/out:$pack\ReplacementScore.dll" @references @scoreSources
if ($LASTEXITCODE) { throw 'The score library did not compile. Check the Steam game path.' }

Copy-Item -LiteralPath "$effects\Profiles.json", "$effects\ReShade.ini", "$effects\Shaders" -Destination $target -Recurse -Force
$sourceLogo = [Drawing.Bitmap]::new((Join-Path $effects 'ProfileLogo.png'))
$logo = [Drawing.Bitmap]::new($sourceLogo, 256, 256)
try {
    $rgba = [byte[]]::new(256 * 256 * 4)
    $offset = 0
    for ($y = 0; $y -lt 256; ++$y) {
        for ($x = 0; $x -lt 256; ++$x) {
            $pixel = $logo.GetPixel($x, $y)
            $rgba[$offset++] = $pixel.R
            $rgba[$offset++] = $pixel.G
            $rgba[$offset++] = $pixel.B
            $rgba[$offset++] = $pixel.A
        }
    }
    [IO.File]::WriteAllBytes((Join-Path $target 'ProfileLogo.rgba'), $rgba)
} finally {
    $logo.Dispose()
    $sourceLogo.Dispose()
}
Get-ChildItem -LiteralPath "$effects\Runtime" | Copy-Item -Destination $runtime -Recurse -Force
foreach ($name in 'Cues.tsv', 'CueDynamics.tsv', 'CueFrames.tsv', 'StoryBeats.tsv', 'Ready.txt') {
    Copy-Item -LiteralPath (Join-Path $music $name) -Destination $pack -Force
}
foreach ($name in 'SetupCutscenes.ps1', 'DownloadAssets.json', 'ReleaseAssets.json', 'GenerateCutscenes.py', 'SplitVideos.py', 'RemixVideos.py', 'BuildDynamicVideos.py',
    'VideoDynamics.tsv', 'Videos.tsv') {
    Copy-Item -LiteralPath (Join-Path $music $name) -Destination $setup -Force
}
Copy-Item -LiteralPath "$vendor\bandit-infer.tar.gz" -Destination $setup -Force
$notices = Join-Path $outputRoot 'Third-party notices'
[IO.Directory]::CreateDirectory($notices) | Out-Null
Get-ChildItem -LiteralPath "$vendor\Notices" -File | Copy-Item -Destination $notices -Force
Write-Output "Built $outputRoot"
