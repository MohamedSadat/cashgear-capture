<#
.SYNOPSIS
    Builds the Windows installer: a self-contained win-x64 publish of the app, packed by Velopack.

.DESCRIPTION
    Output in artifacts\releases:
      CashGear.Capture-win-Setup.exe     per-user installer (no admin rights), starts the app when done
      CashGear.Capture-win-Portable.zip  the same app as a folder that runs from anywhere
      *.nupkg + releases.win.json        the update feed: copy the folder's contents to the update address

    The app is published self-contained (no .NET install needed) as one executable plus the native libraries
    WPF and SkiaSharp need, with ffmpeg\ (FFmpeg's libraries, for screen recording), rnnoise\ (noise suppression,
    built by tools\get-rnnoise.ps1 if it isn't there yet), runtimes\win-x64 and runtimes\vulkan\win-x64 (whisper.cpp,
    for captions from speech; the other platforms' copies are left out) and THIRD-PARTY-NOTICES.txt.

    Code signing: pass -SignParams (signtool.exe arguments, e.g. '/a /fd sha256 /tr http://timestamp.digicert.com
    /td sha256 /f cert.pfx /p ...'), -SignTemplate (any command, {{file}} is replaced by the file to sign) or
    -AzureTrustedSignFile (metadata.json for Azure Trusted Signing). Each defaults to an environment variable
    (CASHGEAR_SIGN_PARAMS, CASHGEAR_SIGN_TEMPLATE, CASHGEAR_AZURE_SIGN_FILE), so a build server can sign
    without changing the command. Velopack signs the app's own binaries, the updater and Setup.exe.
    Without any of them the output is unsigned (Windows SmartScreen warns about a downloaded Setup.exe).

.PARAMETER Version
    Release version (SemVer). Defaults to <Version> in Directory.Build.props.

.PARAMETER UpdateFeedUrl
    Where installed copies look for updates: an https:// address or a folder holding the contents of
    artifacts\releases. Empty (the default) builds an app without automatic updates.

.PARAMETER NoFfmpeg
    Leaves FFmpeg's libraries out. Recording then explains that FFmpeg is missing.

.PARAMETER NoRnnoise
    Leaves RNNoise out (rnnoise.dll needs Visual Studio's C++ compiler to build). Settings then offers no noise
    suppression.

.EXAMPLE
    powershell -File tools\package.ps1
.EXAMPLE
    powershell -File tools\package.ps1 -Version 1.0.1 -UpdateFeedUrl https://downloads.example.com/capture
#>
param(
    [string]$Version,
    [string]$UpdateFeedUrl = "",
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\artifacts"),
    [string]$SignParams = $env:CASHGEAR_SIGN_PARAMS,
    [string]$SignTemplate = $env:CASHGEAR_SIGN_TEMPLATE,
    [string]$AzureTrustedSignFile = $env:CASHGEAR_AZURE_SIGN_FILE,
    [switch]$NoFfmpeg,

    [switch]$NoRnnoise
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "src\CashGear.Capture.App\CashGear.Capture.App.csproj"

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup |
        ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a SemVer version such as 1.2.3 or 1.2.3-beta.1."
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$publish = Join-Path $OutputDir "publish\$Version"
$releases = Join-Path $OutputDir "releases"

if (-not $NoFfmpeg -and -not (Test-Path (Join-Path $root "tools\ffmpeg\avcodec-63.dll"))) {
    & (Join-Path $PSScriptRoot "get-ffmpeg.ps1")
}

if (-not $NoRnnoise -and -not (Test-Path (Join-Path $root "tools\rnnoise\rnnoise.dll"))) {
    & (Join-Path $PSScriptRoot "get-rnnoise.ps1")
}

Write-Host "Publishing CashGear Capture $Version (win-x64, self-contained, single file)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true --nologo `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version "-p:UpdateFeedUrl=$UpdateFeedUrl" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if ($NoFfmpeg) {
    Remove-Item (Join-Path $publish "ffmpeg") -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    $shipped = Get-ChildItem (Join-Path $publish "ffmpeg") -File | ForEach-Object Name | Sort-Object
    $expected = "COPYING.GPLv3", "LICENSE.txt", "avcodec-63.dll", "avfilter-12.dll", "avformat-63.dll", "avutil-61.dll", "swresample-7.dll", "swscale-10.dll" | Sort-Object
    if (Compare-Object $shipped $expected) {
        throw "ffmpeg\ in the publish folder should hold exactly $($expected -join ', '), not $($shipped -join ', ')."
    }
}
if ($NoRnnoise) {
    Remove-Item (Join-Path $publish "rnnoise") -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    $shipped = Get-ChildItem (Join-Path $publish "rnnoise") -File | ForEach-Object Name | Sort-Object
    if (Compare-Object $shipped ("COPYING", "rnnoise.dll" | Sort-Object)) {
        throw "rnnoise\ in the publish folder should hold exactly COPYING and rnnoise.dll, not $($shipped -join ', ')."
    }
}

# Whisper.net's runtime packages bring whisper.cpp for every platform; the app needs win-x64's, on the processor and
# through Vulkan (docs\LICENSES.md).
$runtimes = Join-Path $publish "runtimes"
Get-ChildItem $runtimes -Directory | Where-Object { $_.Name -notin "win-x64", "vulkan" } | Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $runtimes "vulkan") -Directory | Where-Object { $_.Name -ne "win-x64" } | Remove-Item -Recurse -Force
foreach ($folder in "win-x64", "vulkan\win-x64") {
    $dll = Join-Path $runtimes "$folder\whisper.dll"
    if (-not (Test-Path $dll)) {
        throw "Speech recognition's $dll is missing from the publish folder."
    }
}

Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $publish

dotnet tool restore --tool-manifest (Join-Path $root "dotnet-tools.json") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)." }

$pack = @(
    "vpk", "--skip-updates", "pack",
    "--packId", "CashGear.Capture",
    "--packVersion", $Version,
    "--packDir", $publish,
    "--mainExe", "CashGear.Capture.App.exe",
    "--packTitle", "CashGear Capture",
    "--packAuthors", "CashGear",
    "--icon", (Join-Path $root "src\CashGear.Capture.App\Assets\app.ico"),
    "--runtime", "win-x64",
    "--outputDir", $releases
)

if ($SignParams) { $pack += @("--signParams", $SignParams) }
elseif ($SignTemplate) { $pack += @("--signTemplate", $SignTemplate) }
elseif ($AzureTrustedSignFile) { $pack += @("--azureTrustedSignFile", $AzureTrustedSignFile) }
else { Write-Warning "No signing configured: the installer and app are unsigned." }

Push-Location $root
try {
    dotnet @pack
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Release $Version in $releases"
Get-ChildItem $releases | Where-Object { $_.Name -match [regex]::Escape($Version) -or $_.Name -like "*Setup.exe" -or $_.Name -like "*Portable.zip" -or $_.Name -like "releases.*.json" } |
    ForEach-Object { "  {0,-48} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
