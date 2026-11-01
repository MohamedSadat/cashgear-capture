<#
.SYNOPSIS
    Downloads the machine-learning models for the inference tests into tools\inference.

.DESCRIPTION
    The app downloads a model when it first needs it (into %LocalAppData%\CashGear\Capture\Models\Inference). The
    tests use the same files from tools\inference instead, and skip without them. Each file comes from a fixed commit
    and is checked against its SHA-256 (the one the app checks too) before it is kept.

    selfie: MediaPipe Selfie Segmentation (Google, Apache 2.0) as ONNX, from onnx-community on Hugging Face (462 KB).
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
