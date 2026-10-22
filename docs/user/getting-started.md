# Getting started

## Installing

Run **CashGear.Capture-stable-Setup.exe**.
- It installs for your Windows account only, so no administrator rights are needed. The app goes into `%LocalAppData%\CashGear.Capture`, and you get Start menu and desktop shortcuts.
- The app brings everything it needs: .NET, FFmpeg for recordings, and speech recognition for captions.
- Windows SmartScreen may show "Windows protected your PC" for a build that isn't signed yet: choose **More info › Run anyway**.

To remove it, go to Windows Settings › Apps › CashGear Capture › Uninstall. A running copy is closed first. Your settings, captures and recordings are kept.

### The portable copy

**CashGear.Capture-stable-Portable.zip** is the same app as a folder. It runs from anywhere: a USB stick, or a folder of its own.
- It keeps its settings, logs and data in a **Data** folder next to itself, not in your Windows profile. Captures still go to Pictures, unless Settings says otherwise.
- The first time it starts, it takes the settings this Windows account already has, if any.
- It runs as a separate copy, so it can run beside an installed one (but their hotkeys will clash).
- Saved passwords (for FTP or uploads) are encrypted for one Windows account on one PC. On another PC, the destinations ask for them again.

## The tray icon

After installing, CashGear Capture starts and says where it is: in the notification area at the end of the taskbar. Look under **^** if Windows hides it.
- **Click the icon** for the menu: every kind of capture, recording, the tools, the video editor, history, Settings and Exit.
- **From the keyboard:** press **Win+B** to reach the notification area, arrow to the icon, and press **Enter** or **Shift+F10**.

## Your first capture

Press **PrintScreen**. The screen freezes and dims:
- **Drag** a rectangle, or **click** a window or a part of one.
- Use **Enter** to capture and **Esc** to cancel. The arrow keys move the pointer by a pixel.

The capture opens in the [image editor](image-editor.md). Annotate it, then save (Ctrl+S) or copy it (Ctrl+C). What happens after a capture is up to you: see [Settings › Output](settings.md#output).

Other hotkeys:

| Key | Capture |
|---|---|
| `Alt+PrintScreen` | The active window |
| `Ctrl+PrintScreen` | Full screen |
| `Ctrl+Shift+PrintScreen` | A scrolling window |
| `Ctrl+Alt+PrintScreen` | Text (OCR) to the clipboard |
| `Shift+PrintScreen` | Record the screen (again to stop) |

## Your first recording

Press **Shift+PrintScreen**. Drag the area to record, or click a window or a monitor, and press **Enter**.
- A small toolbar at the top of the screen shows the time and has **Pause** and **Stop**. It is never in the video.
- Stop (or the hotkey again) saves an MP4 to the capture folder.

System audio is recorded by default. Add your microphone in [Settings › Recording](settings.md#recording).

## Language

The app follows Windows' display language: Arabic when Windows is in Arabic, English otherwise. **Settings › General › Language** chooses one. The app then offers to restart in it. In Arabic, every window is laid out right to left.

## Updates

The installed app checks for updates once a day and downloads them in the background. When one is ready:
- the tray menu shows **Restart to Update**;
- otherwise it is installed the next time the app starts.

**Settings › General › Updates** can check now, turn automatic checks off, or switch between the **stable** and **beta** channels, and **What's new** shows the release notes.
