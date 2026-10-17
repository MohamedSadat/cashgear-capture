<#
.SYNOPSIS
    Downloads the FFmpeg libraries used for screen recording into tools\ffmpeg.

.DESCRIPTION
    Screen recording runs FFmpeg inside the app (FFmpeg.AutoGen), so it needs FFmpeg's shared libraries. The build
    copies five of them (avutil, swresample, swscale, avcodec, avformat) and the licence next to the app as ffmpeg\.
    The tests also use ffmpeg.exe and ffprobe.exe from the same build, which need avfilter and avdevice too.

    The download is an LGPL v3 "shared" build from https://github.com/BtbN/FFmpeg-Builds (no x264/x265; see
    docs\LICENSES.md), pinned to a month-end autobuild, which BtbN keeps for two years. It is checked against its
    SHA-256 before anything is extracted, and the result must offer the encoders recording relies on.

    -Legacy instead downloads the GPL ffmpeg.exe the external-process recorder still uses into tools\ffmpeg\legacy.
    It goes away when the in-process recorder lands.
#>
param(
    [string]$Tag = "autobuild-2026-09-30-13-08",
    [string]$Asset = "ffmpeg-n9.0.2-17-g2a571b6068-win64-lgpl-shared-9.0.zip",
    [string]$Sha256 = "7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646",
    [switch]$Legacy
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$destination = Join-Path $PSScriptRoot "ffmpeg"

if ($Legacy) {
    $destination = Join-Path $destination "legacy"
    $legacyVersion = "9.0.2"
    $url = "https://github.com/GyanD/codexffmpeg/releases/download/$legacyVersion/ffmpeg-$legacyVersion-essentials_build.zip"
    $Sha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"
    $wanted = { param($name) $name -match '/bin/(ffmpeg|ffprobe)\.exe$' -or $name -match '/LICENSE$' }
}
else {
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Tag/$Asset"
    $wanted = { param($name) $name -match '/bin/[^/]+\.dll$' -or $name -match '/bin/(ffmpeg|ffprobe)\.exe$' -or $name -match '^[^/]+/LICENSE\.txt$' }
}

$zip = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetFileName($url))

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
try {
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        throw "SHA-256 mismatch: expected $Sha256, got $hash. Nothing was extracted."
    }

    New-Item -ItemType Directory -Force $destination | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if (& $wanted $entry.FullName) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination $entry.Name), $true)
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    Remove-Item $zip -ErrorAction SilentlyContinue
}

$ffmpeg = Join-Path $destination "ffmpeg.exe"
& $ffmpeg -hide_banner -version | Select-Object -First 1
if (-not $Legacy) {
    $encoders = & $ffmpeg -hide_banner -encoders 2>$null | Out-String
    foreach ($name in "h264_mf", "libopenh264", "aac") {
        if ($encoders -notmatch "\b$name\b") {
            throw "This FFmpeg build has no $name encoder, which recording relies on."
        }
    }
}
Write-Host "FFmpeg is in $destination; rebuild to copy it next to the app."
