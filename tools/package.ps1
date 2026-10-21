<#
.SYNOPSIS
    Builds the Windows installer: a self-contained win-x64 publish of the app, packed by Velopack.

.DESCRIPTION
    Output in artifacts\releases (per channel, e.g. stable):
      CashGear.Capture-stable-Setup.exe     per-user installer (no admin rights), starts the app when done
      CashGear.Capture-stable-Portable.zip  the same app as a folder that runs from anywhere, keeping its settings and
                                            data in Data\ next to it
      *.nupkg + releases.stable.json        the update feed: copy the folder's contents to the update address

    A stable release also updates releases.win.json, the feed that copies of 1.0.x (packed before channels) check.

    The app is published self-contained (no .NET install needed) and compiled ahead of time (ReadyToRun: it starts
    in about 1 s instead of 1.7 s, for about 120 MB more before compression) as one executable plus the native libraries
    WPF and SkiaSharp need, with ffmpeg\ (FFmpeg's libraries, for screen recording), rnnoise\ (noise suppression,
    built by tools\get-rnnoise.ps1 if it isn't there yet), runtimes\win-x64 and runtimes\vulkan\win-x64 (whisper.cpp,
    for captions from speech; the other platforms' copies are left out), THIRD-PARTY-NOTICES.txt and
    RELEASE-NOTES.md. Debug symbols (.pdb) are left out, the native libraries' too.

    Code signing: pass -SignParams (signtool.exe arguments, e.g. '/fd sha256 /tr http://timestamp.digicert.com
    /td sha256 /f cert.pfx /p ...'), -SignTemplate (any command, {{file}} is replaced by the file to sign) or
    -AzureTrustedSignFile (metadata.json for Azure Trusted Signing). Each defaults to an environment variable
    (CASHGEAR_SIGN_PARAMS, CASHGEAR_SIGN_TEMPLATE, CASHGEAR_AZURE_SIGN_FILE), so a build server can sign
    without changing the command. Velopack signs every executable and DLL of the app (the native ones too), the
    updater and Setup.exe; tools\verify-signatures.ps1 then checks each one in the packages it made and fails the
    build if one isn't signed and timestamped. Without any of them the output is unsigned (Windows SmartScreen warns
    about a downloaded Setup.exe). See docs\SIGNING.md.

.PARAMETER Version
    Release version (SemVer). Defaults to <Version> in Directory.Build.props.

.PARAMETER Channel
    stable or beta. Installed copies follow the channel they were installed from (Settings can switch). Defaults to
    beta for a pre-release version (1.2.0-beta.1) and stable otherwise.

.PARAMETER ReleaseNotes
    Markdown shown in the app before and after updating. Defaults to docs\release-notes\<version>.md when it exists.

.PARAMETER UpdateFeedUrl
    Where installed copies look for updates: an https:// address or a folder holding the contents of
    artifacts\releases. Empty (the default) builds an app without automatic updates.

.PARAMETER SentryDsn
    Where crash reports go (a Sentry DSN). Users are asked before any is sent. Empty (the default): the app has no
    crash reports. Defaults to CASHGEAR_SENTRY_DSN.

.PARAMETER LicensePublicKey
    The public key (base64) licence files are checked against, from tools\LicenseTool keygen. Defaults to
    CASHGEAR_LICENSE_PUBLIC_KEY. Every feature is free in this version either way.

.PARAMETER SignerThumbprint
    The certificate every signature must come from (files someone else already signed, such as Microsoft's, are
    checked for a valid, timestamped signature instead). Without it, every signature must be trusted by this PC.

.PARAMETER Fast
    Publishes without ReadyToRun (a slower start, a quicker build): for test packages.

.PARAMETER NoFfmpeg
    Leaves FFmpeg's libraries out. Recording then explains that FFmpeg is missing.

.PARAMETER NoRnnoise
    Leaves RNNoise out (rnnoise.dll needs Visual Studio's C++ compiler to build). Settings then offers no noise
    suppression.

.EXAMPLE
    powershell -File tools\package.ps1
.EXAMPLE
    powershell -File tools\package.ps1 -Version 1.1.0 -UpdateFeedUrl https://downloads.example.com/capture
.EXAMPLE
    powershell -File tools\package.ps1 -Version 1.2.0-beta.1 -UpdateFeedUrl https://downloads.example.com/capture
#>
param(
    [string]$Version,
    [ValidateSet("", "stable", "beta")]
    [string]$Channel = "",
    [string]$ReleaseNotes = "",
    [string]$UpdateFeedUrl = "",
    [string]$SentryDsn = $env:CASHGEAR_SENTRY_DSN,
    [string]$LicensePublicKey = $env:CASHGEAR_LICENSE_PUBLIC_KEY,
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\artifacts"),
    [string]$SignParams = $env:CASHGEAR_SIGN_PARAMS,
    [string]$SignTemplate = $env:CASHGEAR_SIGN_TEMPLATE,
    [string]$AzureTrustedSignFile = $env:CASHGEAR_AZURE_SIGN_FILE,
    [string]$SignerThumbprint = "",
    [switch]$Fast,
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
if (-not $Channel) {
    $Channel = if ($Version -match '-') { "beta" } else { "stable" }
}
if (-not $ReleaseNotes) {
    $candidate = Join-Path $root "docs\release-notes\$Version.md"
    if (Test-Path $candidate) { $ReleaseNotes = $candidate }
}
elseif (-not (Test-Path $ReleaseNotes)) {
    throw "Release notes '$ReleaseNotes' not found."
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

$readyToRun = if ($Fast) { "false" } else { "true" }
Write-Host "Publishing CashGear Capture $Version, channel $Channel (win-x64, self-contained, single file, ReadyToRun $readyToRun)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true --nologo `
    -p:PublishSingleFile=true -p:PublishReadyToRun=$readyToRun -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version `
    "-p:UpdateFeedUrl=$UpdateFeedUrl" "-p:SentryDsn=$SentryDsn" "-p:LicensePublicKey=$LicensePublicKey" -o $publish
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

# DebugType=None covers the app's own symbols, not the ones native packages bring along (libSkiaSharp.pdb is 96 MB,
# libHarfBuzzSharp.pdb 23 MB): users have no use for them.
Get-ChildItem $publish -Recurse -File -Filter *.pdb | Remove-Item -Force
if (Get-ChildItem $publish -Recurse -File -Filter *.pdb) {
    throw "Debug symbols (.pdb) are still in the publish folder."
}

Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $publish
if ($ReleaseNotes) {
    Copy-Item $ReleaseNotes (Join-Path $publish "RELEASE-NOTES.md") -Force
}
else {
    Write-Warning "No release notes for $Version (docs\release-notes\$Version.md): the app has none to show."
}

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
    "--channel", $Channel,
    "--outputDir", $releases
)
if ($ReleaseNotes) { $pack += @("--releaseNotes", (Resolve-Path $ReleaseNotes).Path) }

$signed = $true
if ($SignParams) { $pack += @("--signParams", $SignParams) }
elseif ($SignTemplate) { $pack += @("--signTemplate", $SignTemplate) }
elseif ($AzureTrustedSignFile) { $pack += @("--azureTrustedSignFile", $AzureTrustedSignFile) }
else {
    $signed = $false
    Write-Warning "No signing configured: the installer and app are unsigned."
}

Push-Location $root
try {
    dotnet @pack
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

# Copies of 1.0.x were packed before channels and check the default channel's feed (releases.win.json): a stable
# release is listed there too, so they update to it (and follow the stable channel from then on).
if ($Channel -eq "stable") {
    $stableFeed = Join-Path $releases "releases.stable.json"
    $legacyFeed = Join-Path $releases "releases.win.json"
    $assets = @((Get-Content $stableFeed -Raw | ConvertFrom-Json).Assets)
    if (Test-Path $legacyFeed) {
        $listed = @($assets | ForEach-Object FileName)
        $assets += @((Get-Content $legacyFeed -Raw | ConvertFrom-Json).Assets | Where-Object { $_.FileName -notin $listed })
    }
    [IO.File]::WriteAllText($legacyFeed, (@{ Assets = $assets } | ConvertTo-Json -Depth 5 -Compress), (New-Object Text.UTF8Encoding $false))
}

$made = Get-ChildItem $releases -File | Where-Object {
    $_.Name -like "CashGear.Capture-$Version-*.nupkg" -or $_.Name -like "CashGear.Capture-$Channel-*" -or $_.Name -like "releases.*.json"
}

if ($signed) {
    $check = @{ Path = @($made | Where-Object { $_.Extension -in ".exe", ".zip" -or $_.Name -like "*-full.nupkg" } | ForEach-Object FullName) }
    if ($SignerThumbprint) { $check.Thumbprint = $SignerThumbprint }
    & (Join-Path $PSScriptRoot "verify-signatures.ps1") @check
    if ($LASTEXITCODE -ne 0) { throw "Some files in the release aren't signed and timestamped (see above)." }
}

Write-Host ""
Write-Host "Release $Version ($Channel) in $releases"
$made | ForEach-Object { "  {0,-48} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
