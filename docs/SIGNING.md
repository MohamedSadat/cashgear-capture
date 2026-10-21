# Signing a release

Windows trusts a downloaded program by its publisher. An unsigned Setup.exe gets SmartScreen's "Windows protected your PC" page, and antivirus heuristics look harder at it. CashGear Capture installs global keyboard hooks and captures the screen, which is also what spyware does, so every release should be signed. Each signature should be timestamped, so it stays valid after the certificate expires.

`tools\package.ps1` signs everything when given a way to sign:
- the app's own executable;
- every DLL next to it: WPF's and SkiaSharp's native libraries, FFmpeg, RNNoise and whisper.cpp;
- Velopack's updater and the stub of the portable zip;
- Setup.exe.

Files someone else has already signed, such as Microsoft's WPF libraries, keep their own signatures. Afterwards `tools\verify-signatures.ps1` opens Setup.exe, the full package and the portable zip and checks every `.exe` and `.dll` in them. The build fails if one isn't signed or isn't timestamped.

## 1. Get a certificate

Pick one:

- **Azure Trusted Signing** (Microsoft's signing service):
  - It needs an Azure account, and the publisher's identity is validated by Microsoft.
  - SmartScreen reputation builds per publisher, not per certificate, so it survives certificate renewals.
  - Put the account, profile and endpoint in a `metadata.json` (see Microsoft's Trusted Signing docs) and pass it with `-AzureTrustedSignFile`.
- **An OV or EV code-signing certificate** from a certificate authority (DigiCert, Sectigo, GlobalSign, SSL.com…):
  - Since 2023 the private key must live on a hardware token or in a cloud HSM, so there's no plain `.pfx` to copy around.
  - Sign through the vendor's tool with `-SignTemplate`, or with signtool and the token's CSP through `-SignParams`.
  - EV used to skip SmartScreen's reputation period. Today both OV and EV build reputation by downloads.

## 2. Package signed

```powershell
# a certificate in the Windows certificate store (a hardware token's, for example), by its thumbprint
.\tools\package.ps1 -UpdateFeedUrl https://downloads.example.com/capture `
    -SignParams '/sha1 <thumbprint> /fd sha256 /tr http://timestamp.digicert.com /td sha256'

# Azure Trusted Signing
.\tools\package.ps1 -UpdateFeedUrl https://downloads.example.com/capture -AzureTrustedSignFile C:\signing\metadata.json

# any other signing tool: {{file}} is the file to sign
.\tools\package.ps1 -SignTemplate 'C:\tools\sign.exe --file "{{file}}"'
```

A build server can set `CASHGEAR_SIGN_PARAMS`, `CASHGEAR_SIGN_TEMPLATE` or `CASHGEAR_AZURE_SIGN_FILE` instead. Velopack uses its own copy of `signtool.exe`, so the Windows SDK isn't needed.

Then check the release yourself:

```powershell
.\tools\verify-signatures.ps1 artifacts\releases\CashGear.Capture-stable-Setup.exe artifacts\releases\CashGear.Capture-1.1.0-full.nupkg artifacts\releases\CashGear.Capture-stable-Portable.zip
.\tools\scan-release.ps1 artifacts\releases artifacts\publish\1.1.0
```

`verify-signatures.ps1` lists every file with its signer and timestamp, and exits with the number that fail. `scan-release.ps1` runs Microsoft Defender's scanner over the release, archives included, and reports any detection.

## 3. Trying it with a test certificate

`tools\new-test-certificate.ps1` makes a self-signed code-signing certificate as a `.pfx` in a folder you choose. It is never added to a certificate store, so nothing trusts what it signs: Windows shows it as signed by an unknown publisher. It proves that every file gets signed and timestamped:

```powershell
.\tools\new-test-certificate.ps1 -OutputDir $env:TEMP\cashgear-signing
# prints the thumbprint and the -SignParams to use, e.g.:
.\tools\package.ps1 -SignerThumbprint <thumbprint> -SignParams '/fd sha256 /f "C:\...\cashgear-test-signing.pfx" /p <password> /tr http://timestamp.digicert.com /td sha256'
```

`-SignerThumbprint` tells the check which certificate to expect, since the test one isn't trusted. Delete the folder afterwards.

## 4. SmartScreen and antivirus after release

- **SmartScreen reputation** builds as people download and run a signed Setup.exe. A new publisher sees the warning for the first few hundred or thousand installs. Keep signing with the same identity so the reputation carries over.
- **If Defender or another antivirus flags a release:**
  1. Don't publish it.
  2. Submit the files at Microsoft's Security Intelligence portal (<https://www.microsoft.com/wdsi/filesubmission>) as a **software developer**, choosing "Incorrectly detected as malware/malicious". Include Setup.exe and the flagged file, and say what the app does: screen capture, global hotkeys and a low-level keyboard hook used only while recording keystroke overlays.
  3. Microsoft usually answers within a few days, and a cleared detection is removed from their definitions.
  4. For other vendors, use their false-positive forms. VirusTotal lists which engines flag a file, but uploading a file there makes it public.
- **Before each release**, run `scan-release.ps1` on the signed build. On the development PC the test-signed 1.1.0 build had no detections (docs\TESTING.md › Phase 19).
