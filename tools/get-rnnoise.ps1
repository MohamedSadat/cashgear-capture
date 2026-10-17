<#
.SYNOPSIS
    Builds rnnoise.dll (RNNoise noise suppression for the microphone) from its pinned source into tools\rnnoise.

.DESCRIPTION
    RNNoise (https://github.com/xiph/rnnoise, BSD-3-Clause) has no official Windows binary, so the app's copy is built
    here from the 0.2 release tarball, which includes the trained model (src\rnnoise_data.c). The tarball is checked
    against its SHA-256 before it is extracted.

    The build needs Visual Studio's C++ compiler ("Desktop development with C++", or the Build Tools with "MSVC ...
    x64/x86 build tools"); vswhere finds it. It is an x64 DLL linked with the static C runtime (/MT), so it needs no
    Visual C++ redistributable. The neural network uses SSE2 everywhere and picks SSE4.1 or AVX2 (with FMA) at run
    time when the CPU has them (RNNoise's own x86 run-time detection).

    The result is tools\rnnoise\rnnoise.dll with the licence as tools\rnnoise\COPYING; the build copies both next to
    the app as rnnoise\. Without them the app works, and offers no noise suppression.
#>
param(
    [string]$Version = "0.2",
    [string]$Sha256 = "90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$destination = Join-Path $PSScriptRoot "rnnoise"
$dll = Join-Path $destination "rnnoise.dll"
if ((Test-Path $dll) -and -not $Force) {
    Write-Host "rnnoise.dll is already in $destination (use -Force to build it again)."
    return
}

# --- The source, checked ---------------------------------------------------------------------------------------
$download = Join-Path $destination "download"
$tarball = Join-Path $download "rnnoise-$Version.tar.gz"
New-Item -ItemType Directory -Force $download | Out-Null
if (-not (Test-Path $tarball) -or (Get-FileHash $tarball -Algorithm SHA256).Hash -ne $Sha256) {
    $url = "https://github.com/xiph/rnnoise/releases/download/v$Version/rnnoise-$Version.tar.gz"
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $tarball -UseBasicParsing
}

$actual = (Get-FileHash $tarball -Algorithm SHA256).Hash
if ($actual -ne $Sha256) {
    throw "rnnoise-$Version.tar.gz has SHA-256 $actual, expected $Sha256. Not building from it."
}

$source = Join-Path $destination "source"
if (Test-Path $source) { Remove-Item $source -Recurse -Force }
New-Item -ItemType Directory -Force $source | Out-Null
tar -xzf $tarball -C $source
if ($LASTEXITCODE -ne 0) { throw "Extracting $tarball failed ($LASTEXITCODE)." }
$tree = Join-Path $source "rnnoise-$Version"
if (-not (Test-Path (Join-Path $tree "src\rnnoise_data.c"))) {
    throw "The tarball has no src\rnnoise_data.c (the model)."
}

# --- The compiler ----------------------------------------------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = if (Test-Path $vswhere) {
    & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vs) {
    throw "Visual Studio's C++ compiler isn't installed. In the Visual Studio Installer, add the 'Desktop development with C++' workload (or install the Build Tools with the MSVC x64/x86 build tools), then run this again."
}
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"

# --- The build -------------------------------------------------------------------------------------------------
$build = Join-Path $source "build"
New-Item -ItemType Directory -Force $build | Out-Null
$common = "/nologo /c /O2 /MT /W1 /DNDEBUG /DWIN32 /DRNNOISE_BUILD /DDLL_EXPORT /DRNN_ENABLE_X86_RTCD /D__SSE__ /D__SSE2__ /D_CRT_SECURE_NO_WARNINGS /I`"$tree\include`" /I`"$tree\src`""
$generic = "denoise.c", "rnn.c", "pitch.c", "kiss_fft.c", "celt_lpc.c", "nnet.c", "nnet_default.c", "parse_lpcnet_weights.c", "rnnoise_data.c", "rnnoise_tables.c", "x86\x86_dnn_map.c", "x86\x86cpu.c"
$steps = @()
foreach ($file in $generic) { $steps += "cl $common `"$tree\src\$file`"" }
$steps += "cl $common /D__SSE4_1__ `"$tree\src\x86\nnet_sse4_1.c`""
$steps += "cl $common /arch:AVX2 /D__SSE4_1__ /D__FMA__ `"$tree\src\x86\nnet_avx2.c`""
$steps += "link /nologo /DLL /OUT:rnnoise.dll *.obj"
$script = Join-Path $build "build.cmd"
Set-Content $script -Encoding ascii -Value (@("@echo off", "call `"$vcvars`" >nul || exit /b 1", "cd /d `"$build`" || exit /b 1") + ($steps | ForEach-Object { "$_ || exit /b 1" }))
Write-Host "Building rnnoise.dll with $vs"
& cmd.exe /c $script
if ($LASTEXITCODE -ne 0) { throw "Building rnnoise.dll failed ($LASTEXITCODE)." }

Copy-Item (Join-Path $build "rnnoise.dll") $dll -Force
Copy-Item (Join-Path $tree "COPYING") (Join-Path $destination "COPYING") -Force
Write-Host ("rnnoise.dll ({0:N1} MB) is in {1}; rebuild to copy it next to the app." -f ((Get-Item $dll).Length / 1MB), $destination)
