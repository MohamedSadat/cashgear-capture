# Licensing (for selling the app)

**In this version every feature is free.** The app asks for no licence and shows no licence prompt; Settings › General › Licence says every feature is included. What is described here is ready for the day CashGear Capture is sold with a free and a Pro edition.

## How it works

- **A licence file** (`.cglicense`) is a small JSON document signed with Ed25519 by the seller's private key. It holds:
  - the licence id;
  - the product (`CashGear Capture`);
  - the buyer;
  - the edition (Pro);
  - optionally which features it unlocks (all of Pro's when absent);
  - on how many PCs it may be activated;
  - when it was made and, for a subscription, when it ends;
  - the activation address.

  The app checks it **offline** with the public key built into it. A changed file, another seller's key or another product's licence is refused.
- **Activation** binds the licence to a PC. The app sends the licence and a **machine id** (a salted SHA-256 of Windows' machine GUID, never the GUID itself) to the activation address. The endpoint answers with a **token** signed by the same key: this licence, this PC, refresh after a date (7 days by default).
- **Refreshing:**
  - After the refresh date, the app asks the endpoint again.
  - If it can't reach it, the licence keeps working for a **14-day grace period** after the last good check. Then the app falls back to the free edition until a check succeeds.
  - A revoked licence (a refund, for example) can't be refreshed.
- **The Pro features**, gated in the app through `IFeatureGate`:
  - the video editor's annotations (text, arrows, shapes, blur, pixelate), speed changes, crossfades and freeze frames, and effects from the metadata (clicks, keystrokes, pointer);
  - zooms and Auto-Zoom;
  - captions (typed, imported, from speech);
  - GIF export, in the editor and as a recording step.

  With the feature closed, each entry point says "This is part of CashGear Capture Pro." and does nothing else.

The code:
- `src/CashGear.Capture.Licensing`: signing, the licence and token, the evaluator with the grace period, the activation client, `FeaturePolicy`.
- `src/CashGear.Capture.App/Platform/LicenseService.cs`: `license.json` in the data folder, and the gate.
- `tools/LicenseTool`: the seller's side.

## Selling it

1. **Make the key pair once** and keep the private key safe. Back it up offline; losing it means no new licences can be issued for existing installs.

   ```powershell
   dotnet run --project tools/LicenseTool -- keygen --out D:\secret\cashgear-licensing
   ```

   It prints the public key.
2. **Build the public key in:**

   ```powershell
   .\tools\package.ps1 -LicensePublicKey <public key> ...
   ```

   The build server can set `CASHGEAR_LICENSE_PUBLIC_KEY` instead.
3. **Switch the policy** from `FeaturePolicy.AllFree` to `FeaturePolicy.ProSplit` in `LicenseService`'s constructor. Add the licence UI: a file picker that calls `LicenseService.ActivateAsync`, the edition and the grace date (`LicenseService.Status`), and the periodic `RefreshAsync`. The strings for the free edition and the Pro message are in `CommonText`.
4. **Run an activation endpoint** at the address you put in licences. `LicenseTool serve` is a complete reference, for a small sale or as the specification of a hosted one. It keeps activations in a JSON file and refuses a licence that is forged, expired, revoked or over its PC limit.

   ```powershell
   dotnet run --project tools/LicenseTool -- serve --key D:\secret\cashgear-licensing\license-signing.key --prefix https://+:443/ --store D:\licensing\activations.json
   ```

   The protocol: `POST <activation address>/activate` with `{"license": "<the .cglicense text>", "machineId": "<hex>"}`. The answers:
   - `200` with the signed token;
   - `403` with `{"error": "invalid-license" | "machine-limit" | "revoked" | "expired"}`.
5. **Issue a licence per sale:**

   ```powershell
   dotnet run --project tools/LicenseTool -- issue --key D:\secret\cashgear-licensing\license-signing.key --licensee "Ada Lovelace" --machines 3 --activation https://licences.example.com --out ada.cglicense
   dotnet run --project tools/LicenseTool -- verify --public <public key> ada.cglicense
   dotnet run --project tools/LicenseTool -- revoke --key ... --store D:\licensing\activations.json --id <licence id>
   ```

## Before selling

Licences cover what the app's own code allows. Before charging for the app:
- **Check the codec patent position** for the markets you sell in (docs/LICENSES.md › Codec patents).
- **Host FFmpeg's source** next to the download (docs/LICENSES.md › FFmpeg).
- **Sign the releases** (docs/SIGNING.md).
