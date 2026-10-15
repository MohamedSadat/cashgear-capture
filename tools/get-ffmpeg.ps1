<#
.SYNOPSIS
    Downloads the ffmpeg build used for screen recording into tools\ffmpeg (ffmpeg.exe, ffprobe.exe, LICENSE).

.DESCRIPTION
    The build copies tools\ffmpeg next to the app (and the App tests) as ffmpeg\. The download is a pinned
    "essentials" build from https://github.com/GyanD/codexffmpeg (the Windows builds linked from ffmpeg.org) and is
    checked against its SHA-256 before anything is extracted. It is a GPL build (it includes x264); how ffmpeg is
    shipped with the app is decided with the installer (Phase 9).
#>
param(
    [string]$Version = "9.0.2",
    [string]$Sha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$destination = Join-Path $PSScriptRoot "ffmpeg"
$url = "https://github.com/GyanD/codexffmpeg/releases/download/$Version/ffmpeg-$Version-essentials_build.zip"
$zip = Join-Path ([IO.Path]::GetTempPath()) "ffmpeg-$Version-essentials_build.zip"

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
            if ($entry.FullName -match '/bin/(ffmpeg|ffprobe)\.exe$' -or $entry.FullName -match '/LICENSE$') {
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

& (Join-Path $destination "ffmpeg.exe") -hide_banner -version | Select-Object -First 1
Write-Host "ffmpeg is in $destination; rebuild to copy it next to the app."
