<#
.SYNOPSIS
    Scans a release with Microsoft Defender Antivirus before it is published.

.DESCRIPTION
    Runs Defender's command-line scanner (MpCmdRun.exe -Scan -ScanType 3) on each file or folder given (Setup.exe,
    the .nupkg packages and the portable zip are scanned inside too), without removing anything it finds, and prints
    one line per item. Exits with the number of items with a detection (0: nothing found).

    A detection on our own build is almost always a false positive: global keyboard hooks and screen capture look
    like spyware to heuristics. Submit the file to Microsoft as a software developer (docs\SIGNING.md) rather than
    publishing it.

.EXAMPLE
    powershell -File tools\scan-release.ps1 artifacts\releases artifacts\publish\1.1.0
#>
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path
)

$ErrorActionPreference = "Stop"
$scanner = Join-Path $env:ProgramFiles "Windows Defender\MpCmdRun.exe"
if (-not (Test-Path $scanner)) {
    throw "Microsoft Defender's MpCmdRun.exe was not found ($scanner)."
}

$found = 0
foreach ($item in $Path) {
    $full = (Resolve-Path $item).Path
    $output = & $scanner -Scan -ScanType 3 -File $full -DisableRemediation 2>&1 | Out-String
    $code = $LASTEXITCODE
    $result = switch ($code) {
        0 { "no threats found" }
        2 { $found++; "DETECTED: " + (($output -split "`r?`n" | Where-Object { $_ -match "Threat|found" }) -join "; ") }
        default { $found++; "scan failed ($code): " + $output.Trim() }
    }
    "{0,-70} {1}" -f $full, $result | Write-Host
}

exit $found
