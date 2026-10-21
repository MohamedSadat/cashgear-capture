<#
.SYNOPSIS
    Checks that every executable and DLL of a release is signed (Authenticode) and timestamped.

.DESCRIPTION
    Takes files and folders: Setup.exe, a Velopack package (.nupkg), a portable .zip, or a folder (e.g. a publish
    folder). Packages and zips are opened, and every .exe and .dll in them is checked, the native libraries too.

    A file passes when:
      - its signature is intact (the file wasn't changed after signing),
      - the signature is timestamped (it stays valid after the certificate expires), and
      - it was signed by -Thumbprint's certificate, or by a certificate this PC trusts. A file someone else signed
        (Microsoft's WPF libraries) passes with a valid, timestamped signature of its own.

    Prints a table of every file, and exits with the number of files that fail (0: all signed).

.PARAMETER Path
    Files and folders to check.

.PARAMETER Thumbprint
    The certificate the release was signed with. Needed for a certificate this PC doesn't trust (a test one).

.EXAMPLE
    powershell -File tools\verify-signatures.ps1 artifacts\releases\CashGear.Capture-stable-Setup.exe artifacts\releases\CashGear.Capture-1.1.0-full.nupkg
#>
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,
    [string]$Thumbprint = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$work = Join-Path ([IO.Path]::GetTempPath()) ("cashgear-signatures-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null

function Get-Binaries([string]$item) {
    $full = (Resolve-Path $item).Path
    if (Test-Path $full -PathType Container) {
        return Get-ChildItem $full -Recurse -File | Where-Object { $_.Extension -in ".exe", ".dll" } |
            ForEach-Object { [pscustomobject]@{ File = $_.FullName; Shown = $_.FullName.Substring($full.Length).TrimStart('\') ; From = Split-Path $full -Leaf } }
    }

    $extension = [IO.Path]::GetExtension($full).ToLowerInvariant()
    if ($extension -in ".nupkg", ".zip") {
        $into = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($full))
        [IO.Compression.ZipFile]::ExtractToDirectory($full, $into)
        return Get-ChildItem $into -Recurse -File | Where-Object { $_.Extension -in ".exe", ".dll" } |
            ForEach-Object { [pscustomobject]@{ File = $_.FullName; Shown = $_.FullName.Substring($into.Length).TrimStart('\'); From = Split-Path $full -Leaf } }
    }

    return [pscustomobject]@{ File = $full; Shown = Split-Path $full -Leaf; From = Split-Path (Split-Path $full -Parent) -Leaf }
}

$results = foreach ($item in $Path) {
    foreach ($binary in Get-Binaries $item) {
        $signature = Get-AuthenticodeSignature -LiteralPath $binary.File
        $signer = $signature.SignerCertificate
        $problem = if ($signature.Status -eq "NotSigned" -or -not $signer) { "not signed" }
            elseif ($signature.Status -eq "HashMismatch") { "changed after signing" }
            elseif (-not $signature.TimeStamperCertificate) { "no timestamp" }
            elseif ($Thumbprint -and $signer.Thumbprint -eq $Thumbprint) {
                # The expected certificate: a test certificate isn't trusted here (UnknownError), but the file is intact.
                if ($signature.Status -in "Valid", "UnknownError") { "" } else { "$($signature.Status): $($signature.StatusMessage)" }
            }
            elseif ($signature.Status -ne "Valid") { "$($signature.Status): $($signature.StatusMessage)" }
            elseif ($Thumbprint -and $signer.Subject -notmatch "Microsoft") { "signed by $($signer.Subject), not $Thumbprint" }
            else { "" }

        [pscustomobject]@{
            From = $binary.From
            File = $binary.Shown
            Signer = if ($signer) { ($signer.Subject -replace '^CN=([^,]+).*', '$1') } else { "-" }
            Timestamp = if ($signature.TimeStamperCertificate) { "yes" } else { "no" }
            Result = if ($problem) { "FAIL: $problem" } else { "ok" }
        }
    }
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
$failed = @($results | Where-Object { $_.Result -ne "ok" }).Count
Write-Host ("{0} file(s) checked, {1} failed" -f @($results).Count, $failed)
exit $failed
