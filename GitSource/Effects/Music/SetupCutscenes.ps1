$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$game = $env:ROM_PREP_GAME
$cache = $env:ROM_PREP_CACHE
$pack = $env:ROM_PREP_PACK
$library = $env:ROM_PREP_LIBRARY
$compute = $env:ROM_PREP_BACKEND
$localOnly = $env:ROM_PREP_LOCAL_ONLY -eq '1'
if (!$game -or !$cache -or !$pack -or $library -notin 'dark-synthwave', 'cinematic-sci-fi', 'noir-jazz' -or $compute -notin 'cpu', 'cu128') {
    throw 'Open Prepare Cutscenes from Play with Effects.'
}

$tools = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $cache) '..\CutsceneTools'))
$downloads = Join-Path $tools 'Downloads'
[IO.Directory]::CreateDirectory($downloads) | Out-Null
$log = Join-Path $tools 'setup.log'
$env:UV_CACHE_DIR = Join-Path $tools 'PackageCache'
$env:UV_NO_CONFIG = '1'
$env:UV_NO_PROGRESS = '1'
$env:UV_OFFLINE = '1'
$env:UV_NO_PYTHON_DOWNLOADS = '1'
$env:PYTHONNOUSERSITE = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONHOME = $null
$tab = [char]9
$maxAttempts = 5

function Write-SetupLog([string] $Message) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
    [Console]::WriteLine($line)
}

function Show-Progress([int] $Done, [int] $Total, [string] $Label) {
    [Console]::WriteLine(('PROGRESS' + $tab + $Done + $tab + $Total + $tab + $Label))
}

function Format-Size([long] $Bytes) {
    if ($Bytes -ge 1MB) { return ('{0:N1} MiB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KiB' -f ($Bytes / 1KB)) }
    return ('{0} bytes' -f $Bytes)
}

function Allowed-Hosts([uri] $Source) {
    switch ($Source.Host) {
        'github.com' { return @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com') }
        'www.gyan.dev' { return @('www.gyan.dev', 'gyan.dev') }
        'download.pytorch.org' { return @('download.pytorch.org', 'download-r2.pytorch.org') }
        'download-r2.pytorch.org' { return @('download-r2.pytorch.org', 'download.pytorch.org') }
        'files.pythonhosted.org' { return @('files.pythonhosted.org') }
        'zenodo.org' { return @('zenodo.org') }
        default { throw "Unsupported download host $($Source.Host)." }
    }
}

function Test-Asset($Asset, [string] $Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ($Asset.bytes -gt 0 -and (Get-Item -LiteralPath $Path).Length -ne $Asset.bytes) { return $false }
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try {
        return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') -eq $Asset.sha256
    } finally {
        $stream.Dispose()
        $hash.Dispose()
    }
}

function Receive-Asset($Asset, [string[]] $Hosts, [string] $Partial) {
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('NeonLoop-CutsceneSetup/1.0')
    $response = $null
    $inputStream = $null
    $outputStream = $null
    try {
        $location = [uri]$Asset.url
        for ($hop = 0; $hop -le 5; $hop++) {
            if ($location.Scheme -ne 'https' -or $location.Host -notin $Hosts -or !$location.IsDefaultPort -or $location.UserInfo) {
                throw "Redirect to an unapproved origin: $($location.Host). Use the manual option."
            }
            Write-SetupLog ('Connecting to {0} for {1}' -f $location.Host, $Asset.filename)
            $response = $client.GetAsync($location, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $code = [int]$response.StatusCode
            if ($code -notin 301, 302, 303, 307, 308) { break }
            if ($hop -eq 5 -or !$response.Headers.Location) { throw 'The server sent too many redirects.' }
            $location = New-Object Uri($location, $response.Headers.Location)
            $response.Dispose()
            $response = $null
        }
        $response.EnsureSuccessStatusCode() | Out-Null
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [IO.File]::Create($Partial)
        $buffer = New-Object byte[] (1024 * 1024)
        $received = 0L
        $total = if ($response.Content.Headers.ContentLength -gt 0) { $response.Content.Headers.ContentLength } else { $Asset.bytes }
        $last = -1
        $label = 'Downloading ' + $Asset.filename
        Show-Progress 0 0 $label
        while ($true) {
            $transfer = $inputStream.ReadAsync($buffer, 0, $buffer.Length)
            if (!$transfer.Wait(45000)) { throw 'The server sent no data for 45 seconds.' }
            $read = $transfer.GetAwaiter().GetResult()
            if ($read -eq 0) { break }
            $received += $read
            if ($Asset.bytes -gt 0 -and $received -gt $Asset.bytes) {
                throw 'The server sent more data than the approved file size.'
            }
            $outputStream.Write($buffer, 0, $read)
            if ($total -gt 0) {
                $percent = [Math]::Min(100, [int][Math]::Floor(100.0 * $received / $total))
                if ($percent -ne $last) {
                    Show-Progress $percent 100 $label
                    if ($percent -eq 100 -or [Math]::Floor($percent / 10) -gt [Math]::Floor($last / 10)) {
                        Write-SetupLog ('{0} / {1} from {2}' -f (Format-Size $received), (Format-Size $total), $location.Host)
                    }
                    $last = $percent
                }
            }
        }
        Write-SetupLog ('Received {0} from {1}' -f (Format-Size $received), $location.Host)
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }
        $client.Dispose()
    }
}

function Get-Asset($Asset) {
    if ($Asset.filename -ne [IO.Path]::GetFileName($Asset.filename) -or $Asset.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'The pinned download list is invalid.'
    }
    $destination = Join-Path $downloads $Asset.filename
    $partial = "$destination.part"
    if (Test-Path -LiteralPath $destination) {
        Show-Progress 0 0 ('Checking saved ' + $Asset.filename)
        if (Test-Asset $Asset $destination) {
            Write-SetupLog ('Already verified: {0}' -f $Asset.filename)
            return $destination
        }
    }
    $source = [uri]$Asset.url
    $hosts = Allowed-Hosts $source
    $attempt = 1
    $previous = ''
    while ($true) {
        $number = if ($attempt -le $maxAttempts) { [string]$attempt } else { 'manual' }
        Write-SetupLog ('Waiting for approval: {0} from {1}' -f $Asset.filename, $Asset.url)
        [Console]::WriteLine((@('ASSET', $number, $maxAttempts, $Asset.label, $Asset.filename, $Asset.bytes,
            $Asset.url, $Asset.sha256, ($hosts -join ', '), $previous) -join $tab))
        $reply = [Console]::ReadLine()
        if (!$reply -or $reply -eq 'cancel') {
            Write-SetupLog ('Setup stopped before downloading {0}.' -f $Asset.filename)
            exit 2
        }
        try {
            $approved = ($reply -eq 'download' -and !$localOnly) -or ($reply -eq 'download-once' -and $localOnly)
            if ($approved -and $attempt -le $maxAttempts) {
                Write-SetupLog ('Approved attempt {0} of {1}: {2}' -f $attempt, $maxAttempts, $Asset.url)
                $attempt++
                Receive-Asset $Asset $hosts $partial
            } elseif ($reply.StartsWith('file' + $tab, [StringComparison]::Ordinal)) {
                $file = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($reply.Substring(5)))
                Write-SetupLog ('Checking manually supplied file: {0}' -f [IO.Path]::GetFileName($file))
                Show-Progress 0 0 ('Checking ' + $Asset.filename)
                if (!(Test-Asset $Asset $file)) { throw 'That file does not match the expected size and SHA-256.' }
                if (![string]::Equals([IO.Path]::GetFullPath($file), $destination, [StringComparison]::OrdinalIgnoreCase)) {
                    if (![string]::Equals([IO.Path]::GetFullPath($file), $partial, [StringComparison]::OrdinalIgnoreCase)) {
                        Copy-Item -LiteralPath $file -Destination $partial -Force
                    }
                } else {
                    Write-SetupLog ('Verified: {0}' -f $Asset.filename)
                    return $destination
                }
            } else {
                throw 'Choose a local file, approve the available download, or skip cutscene setup.'
            }
            Show-Progress 0 0 ('Verifying ' + $Asset.filename)
            if (!(Test-Asset $Asset $partial)) { throw 'The downloaded file does not match its approved size and SHA-256.' }
            Move-Item -LiteralPath $partial -Destination $destination -Force
            Write-SetupLog ('Verified: {0}' -f $Asset.filename)
            return $destination
        } catch {
            $previous = ($_.Exception.GetBaseException().Message -replace '\s+', ' ').Trim()
            Write-SetupLog ('File unavailable: {0}. {1}' -f $Asset.filename, $previous)
            if ($attempt -le $maxAttempts) {
                Write-SetupLog ('Waiting for your choice. Automatic attempt {0} of {1} is available.' -f $attempt, $maxAttempts)
            } else {
                Write-SetupLog 'Automatic attempts used up. You can still supply the file manually or stop and reopen setup.'
            }
        }
    }
}

function Run-Tool([string] $Exe, [string[]] $Arguments, [string] $Failure) {
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Exe @Arguments 2>&1 | ForEach-Object {
            $line = [string]$_
            if ($line.StartsWith('PROGRESS' + $tab, [StringComparison]::Ordinal)) {
                [Console]::WriteLine($line)
            } elseif ($line.Trim()) {
                Write-SetupLog $line
            }
        }
        if ($LASTEXITCODE -ne 0) { throw $Failure }
    } finally {
        $ErrorActionPreference = $savedPreference
    }
}

try {
    Write-SetupLog ('Preparing {0} with the approved {1} backend. Each transfer is bounded by the reviewed manifest.' -f $library, $compute)
    $assets = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'DownloadAssets.json') | ConvertFrom-Json
    $uv = Join-Path $tools 'uv.exe'
    $pythonRoot = Join-Path $tools 'RuntimePython'
    $standalone = Join-Path $pythonRoot 'python\python.exe'
    $ffmpeg = Join-Path $tools 'ffmpeg-9.0.2-essentials_build\bin'
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'

    if (!(Test-Path -LiteralPath $uv)) {
        $archive = Get-Asset ($assets | Where-Object label -eq 'uv setup tool')
        Show-Progress 0 0 'Installing uv'
        Write-SetupLog 'Extracting the verified uv setup tool.'
        Run-Tool $tar @('-xf', $archive, '-C', $tools) 'Could not extract the verified uv setup tool.'
    }
    if (!(Test-Path -LiteralPath $standalone)) {
        $archive = Get-Asset ($assets | Where-Object label -eq 'Python 3.12.13')
        Show-Progress 0 0 'Installing Python'
        Write-SetupLog 'Extracting verified Python into the mod folder.'
        [IO.Directory]::CreateDirectory($pythonRoot) | Out-Null
        Run-Tool $tar @('-xf', $archive, '-C', $pythonRoot) 'Could not extract the verified Python runtime.'
    }
    if (!(Test-Path -LiteralPath (Join-Path $ffmpeg 'ffmpeg.exe')) -or
        !(Test-Path -LiteralPath (Join-Path $ffmpeg 'ffprobe.exe'))) {
        $archive = Get-Asset ($assets | Where-Object label -eq 'FFmpeg video tools')
        Show-Progress 0 0 'Installing FFmpeg'
        Write-SetupLog 'Extracting the verified FFmpeg video tools.'
        Run-Tool $tar @('-xf', $archive, '-C', $tools) 'Could not extract the verified FFmpeg video tools.'
    }
    $env:PATH = "$ffmpeg;$env:PATH"

    $environment = Join-Path $tools ('Environment-v2-' + $compute)
    $python = Join-Path $environment 'Scripts\python.exe'
    $installed = Join-Path $environment '.ready'
    if (!(Test-Path -LiteralPath $python) -or !(Test-Path -LiteralPath $installed)) {
        $wheels = @($assets | Where-Object { $_.group -in 'wheel', $compute } | ForEach-Object { Get-Asset $_ })
        if (!(Test-Path -LiteralPath $python)) {
            Show-Progress 0 0 'Preparing the local Python environment'
            Write-SetupLog 'Creating the local environment. Network access is disabled for package tools.'
            Run-Tool $uv @('venv', '--offline', '--no-python-downloads', '--python', $standalone, $environment) 'Could not create the local Python environment.'
        }
        Show-Progress 0 0 'Installing verified audio packages'
        Write-SetupLog ('Installing {0} verified wheel files locally.' -f $wheels.Count)
        $arguments = @('pip', 'install', '--offline', '--no-index', '--no-deps', '--python', $python) + $wheels
        $backendError = if ($compute -eq 'cu128') { 'CUDA: ' } else { '' }
        Run-Tool $uv $arguments ($backendError + 'The verified audio packages could not be installed.')
        Run-Tool $uv @('pip', 'check', '--offline', '--python', $python) ($backendError + 'The installed packages failed the dependency check.')
        Run-Tool $python @('-c', 'import torch, numpy, soundfile') ($backendError + 'The local audio packages could not start.')
        [IO.File]::WriteAllText($installed, $compute)
    } else {
        Write-SetupLog ('Using the existing local {0} audio environment.' -f $compute)
    }
    $device = 'cpu'
    if ($compute -eq 'cu128') {
        Show-Progress 0 0 'Verifying the approved CUDA runtime with a small local allocation'
        Run-Tool $python @('-c', 'import torch; assert torch.cuda.is_available(); torch.zeros(1, device="cuda").sum().item(); torch.cuda.synchronize()') 'CUDA: the approved GPU runtime could not start.'
        $device = 'cuda'
    }

    $bandit = Join-Path $tools 'BanditSource'
    $env:PYTHONPATH = Join-Path $bandit 'bandit-infer-main\src'
    if (!(Test-Path -LiteralPath (Join-Path $env:PYTHONPATH 'bandit_infer\__init__.py'))) {
        Write-SetupLog 'Extracting the audio separator source bundled with the mod.'
        [IO.Directory]::CreateDirectory($bandit) | Out-Null
        Run-Tool $tar @('-xf', (Join-Path $PSScriptRoot 'bandit-infer.tar.gz'), '-C', $bandit) 'Could not extract the bundled separator.'
    }
    $checkpoint = Get-Asset ($assets | Where-Object label -eq 'Bandit cutscene model')
    Write-SetupLog 'Starting local cutscene preparation.'
    try {
        Run-Tool $python @('-u', (Join-Path $PSScriptRoot 'GenerateCutscenes.py'), $game, $pack, $cache, $library, $checkpoint, '--device', $device) 'Cutscene preparation stopped. The preceding log has the error details.'
    } catch {
        if ($device -eq 'cuda' -and $LASTEXITCODE -eq 3) { throw 'CUDA: GPU processing failed. The installer can use the reviewed CPU fallback.' }
        throw
    }
    Write-SetupLog 'Cutscene preparation finished.'
} catch {
    $message = $_.Exception.GetBaseException().Message
    Write-SetupLog $message
    [Console]::Error.WriteLine('ERROR' + $tab + $message)
    if ($compute -eq 'cu128' -and $message.StartsWith('CUDA:')) { exit 3 }
    exit 1
}
