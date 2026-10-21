<#
.SYNOPSIS
    Downloads Whisper speech-recognition models for the transcription tests into tools\whisper.

.DESCRIPTION
    The app downloads the model the user picks when it first needs it (into %LocalAppData%\CashGear\Capture\Models\
    Whisper). The tests use the same files from tools\whisper instead, and skip without them. The models are
    whisper.cpp's quantized ggml files from https://huggingface.co/ggerganov/whisper.cpp (OpenAI's Whisper weights,
    MIT), each checked against its SHA-256 (the one the app checks too) before it is kept.

    -Models base,small (the default) is about 250 MB: base (57 MB) for the English tests, small (190 MB) because base
    handles Arabic poorly. medium (539 MB) is offered too.
#>
param(
    [string[]]$Models = @("base", "small"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$catalog = @{
    base   = @{ File = "ggml-base-q5_1.bin";   Bytes = 59707625;  Sha256 = "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898" }
    small  = @{ File = "ggml-small-q5_1.bin";  Bytes = 190085487; Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb" }
    medium = @{ File = "ggml-medium-q5_0.bin"; Bytes = 539212467; Sha256 = "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f" }
}
$destination = Join-Path $PSScriptRoot "whisper"
New-Item -ItemType Directory -Force $destination | Out-Null

foreach ($name in $Models) {
    $model = $catalog[$name.ToLowerInvariant()]
    if ($null -eq $model) {
        throw "Unknown model '$name' (base, small or medium)."
    }

    $path = Join-Path $destination $model.File
    if ((Test-Path $path) -and (Get-Item $path).Length -eq $model.Bytes -and -not $Force) {
        Write-Host "$($model.File) is already in $destination (use -Force to download it again)."
        continue
    }

    $url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$($model.File)"
    $partial = "$path.partial"
    Write-Host "Downloading $url ($([math]::Round($model.Bytes / 1MB)) MB)"
    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
    $hash = (Get-FileHash $partial -Algorithm SHA256).Hash
    if ($hash -ne $model.Sha256) {
        Remove-Item $partial
        throw "SHA-256 mismatch for $($model.File): expected $($model.Sha256), got $hash. Nothing was kept."
    }

    Move-Item -Force $partial $path
    Write-Host "Saved $path"
}
