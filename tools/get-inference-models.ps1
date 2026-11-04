<#
.SYNOPSIS
    Downloads the machine-learning models for the inference tests into tools\inference.

.DESCRIPTION
    The app downloads a model when it first needs it (into %LocalAppData%\CashGear\Capture\Models\Inference). The
    tests use the same files from tools\inference instead, and skip without them. Each file comes from a fixed commit
    and is checked against its SHA-256 (the one the app checks too) before it is kept.

    selfie: MediaPipe Selfie Segmentation (Google, Apache 2.0) as ONNX, from onnx-community on Hugging Face (462 KB).
    opus-mt-en-ar: Opus-MT English to Arabic (Helsinki-NLP, CC-BY 4.0) as quantized ONNX, from Xenova on Hugging Face (113 MB).
#>
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$catalog = @(
    @{
        File   = "mediapipe-selfie-segmentation.onnx"
        Bytes  = 462352
        Sha256 = "3241ac4ad8aa35bdaf33946776db29f7c283a413aa0b0dacb9483594b4531aad"
        Url    = "https://huggingface.co/onnx-community/mediapipe_selfie_segmentation/resolve/be49485c8e027524be38591817fc5cd31bd9d00e/onnx/model.onnx"
    },
    @{
        File   = "opus-mt-en-ar-encoder.onnx"
        Bytes  = 51773854
        Sha256 = "53c354d87ced44b39085fe8a46a25f09ea4578d442dbce9663f3977228f8a04e"
        Url    = "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/034a684356c19021a187cda4d7f823298b913921/onnx/encoder_model_quantized.onnx"
    },
    @{
        File   = "opus-mt-en-ar-decoder.onnx"
        Bytes  = 58707418
        Sha256 = "94e6bdf90866151787d014b7b77be0adb0f036418ee04d235ec01238aa8426b9"
        Url    = "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/034a684356c19021a187cda4d7f823298b913921/onnx/decoder_model_quantized.onnx"
    },
    @{
        File   = "opus-mt-en-ar-source.spm"
        Bytes  = 801074
        Sha256 = "81ac8f4d4ee271f4109eac67560a31f9c2d555463ba5eb95c32c6deee8ae819d"
        Url    = "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/034a684356c19021a187cda4d7f823298b913921/source.spm"
    },
    @{
        File   = "opus-mt-en-ar-vocab.json"
        Bytes  = 2243581
        Sha256 = "8b74af9e4c1ec122b8d06362db6c38795e10ca2b0156b2110cf08d4716744b76"
        Url    = "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/034a684356c19021a187cda4d7f823298b913921/vocab.json"
    }
)
$destination = Join-Path $PSScriptRoot "inference"
New-Item -ItemType Directory -Force $destination | Out-Null

foreach ($model in $catalog) {
    $path = Join-Path $destination $model.File
    if ((Test-Path $path) -and (Get-Item $path).Length -eq $model.Bytes -and -not $Force) {
        Write-Host "$($model.File) is already in $destination (use -Force to download it again)."
        continue
    }

    $partial = "$path.partial"
    Write-Host "Downloading $($model.Url)"
    Invoke-WebRequest -Uri $model.Url -OutFile $partial -UseBasicParsing
    $hash = (Get-FileHash $partial -Algorithm SHA256).Hash
    if ($hash -ne $model.Sha256) {
        Remove-Item $partial
        throw "SHA-256 mismatch for $($model.File): expected $($model.Sha256), got $hash. Nothing was kept."
    }

    Move-Item $partial $path -Force
    Write-Host "$($model.File) checked and kept."
}
