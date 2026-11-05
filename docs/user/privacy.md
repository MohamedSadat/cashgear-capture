# Privacy

## What stays on your PC

| What | Where (installed copy) |
|---|---|
| Settings and presets | `%AppData%\CashGear\Capture\` |
| Logs (14 days), history, recordings in progress, the video editor's caches, speech models | `%LocalAppData%\CashGear\Capture\` |
| Passwords, sign-in tokens and upload sessions of destinations (encrypted for your Windows account) | `%LocalAppData%\CashGear\Capture\secrets.json` |
| Uploads waiting or failed: the file's path and where it goes | `%LocalAppData%\CashGear\Capture\uploads.json`, with copies of unsaved captures in `upload-queue` |
| Captures and recordings | Pictures\CashGear Capture (or the folder you chose) |

A portable copy keeps all of these but captures in its **Data** folder.

- **The keyboard is only watched while you record with keystrokes on.** Keys go into the recording and its metadata file, and nowhere else. Password fields and the apps you list are never shown.
- **Captions and transcripts from speech** are made on this PC; nothing is uploaded.
- **Translation** runs on this PC too. The first time English is translated into Arabic, its model (113 MB) is downloaded from Hugging Face and checked.
- **The webcam's background** is worked out on this PC; no pictures leave it. The first time, a small model is downloaded from Hugging Face and checked. ONNX Runtime's Windows telemetry is turned off.
- **Uploads** happen only to the servers and services you set up as destinations, and only what you send: nothing goes anywhere until a workflow's step, or the export window, says so.
- **Signing in** happens in your own browser, on the service's page. The app never sees your password. It keeps a token the service gives it, encrypted for your Windows account, and Sign Out forgets it.
- **What you upload to YouTube is private** unless you choose otherwise, and **a link from Google Drive, OneDrive or Dropbox opens only for you** unless you tick that anyone with the link can open it.

## Crash reports

When CashGear Capture crashes, it can send a report so the problem can be fixed. It is **off until you agree**: the first start that has somewhere to send reports asks, and Settings › General › Privacy changes your answer. A copy without a report address never sends any.

A report has:
- the error and where in the app's code it happened;
- the versions of the app, of Windows and of .NET;
- the language, and memory figures;
- an id for the app's run, and for the recording, capture or export it happened in;
- the app's last log lines, as their templates only ("Captured window {Title}"), never the values.

A report never has your user name, the PC's name, your time zone, screenshots, recordings, keystrokes, window titles, file names or passwords. Your user folder is shown as `%USERPROFILE%`. Reports that couldn't be sent wait in the data folder and go at the next start.

## Diagnostics for a bug report

Settings › General › **Export diagnostics…** (or tray › Export Diagnostics) saves a zip **on your PC**:
- the last week's logs;
- the settings without server addresses, user names, URLs, or what you said about your videos and folders;
- facts about this PC: Windows, graphics cards, monitors, sound devices.

Nothing is sent. Look inside before you share it.
