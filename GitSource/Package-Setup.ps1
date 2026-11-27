param(
    [Parameter(Mandatory)] [string] $Build,
    [Parameter(Mandatory)] [string] $Output,
    [Parameter(Mandatory)] [ValidatePattern('^v[0-9][a-zA-Z0-9.-]+$')] [string] $Version,
    [string] $OfflineAssets
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$buildPath = (Resolve-Path -LiteralPath $Build).Path
$outputPath = [IO.Path]::GetFullPath($Output)
$launcher = Join-Path $buildPath 'Play with Effects.exe'
$credits = Join-Path $PSScriptRoot 'Effects\Vendor\Notices'
$files = @{}
$payloadChecks = [ordered]@{}
$allowed = @{}
foreach ($line in (Import-Csv -LiteralPath (Join-Path $PSScriptRoot 'PackagePayload.tsv') -Delimiter "`t")) {
    if ($allowed.ContainsKey($line.path) -or $line.sha256 -notmatch '^(?:-|[0-9a-f]{64})$') {
        throw "Invalid payload allowlist entry: $($line.path)"
    }
    $allowed[$line.path] = $line.sha256
}

function Add-File($Archive, [string] $Source, [string] $Name) {
    if ($files.ContainsKey($Name)) { throw "Duplicate package entry: $Name" }
    if (!$allowed.ContainsKey($Name)) { throw "Unreviewed package entry: $Name" }
    $hash = (Get-FileHash -LiteralPath $Source).Hash
    if ($allowed[$Name] -ne '-' -and $hash -ne $allowed[$Name]) { throw "Dependency pin mismatch: $Name" }
    $files[$Name] = $true
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Archive, $Source, $Name) | Out-Null
    $payloadChecks[$Name] = @{
        size = (Get-Item -LiteralPath $Source).Length
        sha256 = $hash
    }
}

function Test-MusicArchive([string] $Path, [string] $Key) {
    if (!$Key) { throw 'Missing soundtrack key.' }
    $prefix = "Effects/bin/MusicPack/$Key"
    $cues = Import-Csv -LiteralPath (Join-Path $PSScriptRoot 'Effects/Music/Cues.tsv') -Delimiter "`t"
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($cue in $cues) { [void]$expected.Add("$prefix/Cues/$($cue.cue).mp3") }
    [void]$expected.Add("$prefix/CueCredits.tsv")
    $music = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($item in $music.Entries) {
            if ($item.Length -le 0 -or !$expected.Remove($item.FullName)) {
                throw "Unexpected soundtrack entry: $($item.FullName)"
            }
        }
        if ($expected.Count -gt 0) { throw "Incomplete soundtrack: $Key" }
    } finally { $music.Dispose() }
}

function Add-Text($Archive, [string] $Name, [string] $Value) {
    $writer = [IO.StreamWriter]::new($Archive.CreateEntry($Name).Open())
    try { $writer.Write($Value) } finally { $writer.Dispose() }
}

if (Test-Path -LiteralPath $outputPath) { throw 'Choose a new setup output file.' }
if (!(Test-Path -LiteralPath (Join-Path $buildPath 'Effects\bin\MusicPack\RomScoreDSP.dll'))) { throw 'Build the full mod first.' }
$buildFiles = @(Get-ChildItem -LiteralPath $buildPath -Force -Recurse)
foreach ($item in $buildFiles) {
    $name = [IO.Path]::GetRelativePath($buildPath, $item.FullName).Replace('\', '/')
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (!$item.PSIsContainer -and (!$allowed.ContainsKey($name) -or $name -match '^(DSP Source/|Soundtrack Credits\.md$|DSP Source\.md$)'))) {
        throw "Use a clean build. Unexpected path: $name"
    }
}
$temporary = $outputPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
Copy-Item -LiteralPath $launcher -Destination $temporary
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $stream.Position = $stream.Length
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in 'Play with Effects.exe', 'LICENSE', 'HOBBY-TERMS.txt', 'Start Here.txt') {
                Add-File $archive (Join-Path $buildPath $name) $name
            }
            foreach ($part in 'Effects', 'Third-party notices') {
                $folder = Join-Path $buildPath $part
                foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse) {
                    $name = [IO.Path]::GetRelativePath($buildPath, $file.FullName).Replace('\', '/')
                    Add-File $archive $file.FullName $name
                }
            }
            Add-File $archive (Join-Path $credits 'Soundtracks.md') 'Soundtrack Credits.md'
            Add-File $archive (Join-Path $credits 'DSP-SOURCE.md') 'DSP Source.md'
            $dspPaths = @('Effects/Music/ScoreDsp.cpp','Effects/Music/Mp3Score.cpp')
            foreach ($part in 'rubberband-4.0.0', 'minimp3') {
                $folder = Join-Path $PSScriptRoot "Effects\Vendor\$part"
                $dspPaths += Get-ChildItem -LiteralPath $folder -File -Recurse -Force |
                    Where-Object Name -NotMatch '^CHANGELOG(?:\.[^/]+)?$' | ForEach-Object {
                    [IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName).Replace('\', '/')
                }
            }
            foreach ($name in $dspPaths) { Add-File $archive (Join-Path $PSScriptRoot $name) "DSP Source/$name" }
            $missing = @($allowed.Keys | Where-Object { !$files.ContainsKey($_) })
            if ($missing.Count -gt 0) { throw "Missing reviewed payload files: $($missing -join ', ')" }
            if ($OfflineAssets) {
                $manifestRoot = Join-Path $PSScriptRoot 'Effects/Music'
                $musicAssets = @(Get-Content -LiteralPath "$manifestRoot/ReleaseAssets.json" -Raw | ConvertFrom-Json)
                $assets = $musicAssets
                $assets += @(Get-Content -LiteralPath "$manifestRoot/DownloadAssets.json" -Raw | ConvertFrom-Json |
                    Where-Object group -ne 'cu128')
                foreach ($asset in $assets) {
                    $name = $asset.filename
                    if ([IO.Path]::GetFileName($name) -cne $name) { throw "Invalid offline asset name: $name" }
                    $input = Get-Item -LiteralPath (Join-Path $OfflineAssets $name)
                    if ($input.Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $input.FullName).Hash -ne $asset.sha256) {
                        throw "Offline asset failed verification: $name"
                    }
                    if ($asset.key) { Test-MusicArchive $input.FullName $asset.key }
                    $entry = "OfflineAssets/$name"
                    if ($files.ContainsKey($entry)) { throw "Duplicate package entry: $entry" }
                    $files[$entry] = $true
                    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $input.FullName, $entry,
                        [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
                }
            }
            Add-Text $archive 'NeonLoop.install' $Version
            $markerBytes = [Text.Encoding]::UTF8.GetBytes($Version)
            $payloadChecks['NeonLoop.install'] = @{
                size = $markerBytes.Length
                sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($markerBytes))
            }
            Add-Text $archive 'NeonLoop.payload.json' ($payloadChecks | ConvertTo-Json -Depth 3 -Compress)
        } finally { $archive.Dispose() }
        $stream.Write([Text.Encoding]::ASCII.GetBytes('NLOOPPK1'))
    } finally { $stream.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $outputPath
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Get-Item -LiteralPath $outputPath | Select-Object FullName,Length
Get-FileHash -LiteralPath $outputPath -Algorithm SHA256
