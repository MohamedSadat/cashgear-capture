# Recording

## Starting and stopping

Press **Shift+PrintScreen** (or tray › **Record Screen…**). Drag an area and release to start, or click a window or a monitor and press **Enter**. The recording toolbar appears at the top of the screen, and it is never in the video:
- the time;
- **Pause**, **Stop**, the pen for drawing on the screen;
- the sound meters, **Mute mic**, and the sound button (volumes, noise suppression).

**Stop**, or the hotkey again, saves an MP4 to the capture folder. A notification says how long and how big it is; click it to show the file.

To use the toolbar from the keyboard, press **Ctrl+Alt+Shift+T**: Tab moves between the buttons and Esc goes back.

## A window, or every monitor

- **A window:** tray › **More Recording** › **Record a Window…**, or click a window after Shift+PrintScreen. Only that window is in the video, even when it is moved, covered or partly off the screen.
  - **Resized:** its pictures are fitted into the size the recording started at, with black bars, in the one file.
  - **Minimized:** its last picture holds, and the saved notification says from when to when.
  - **Closed:** the recording stops and is saved.
  - It has no pen on the toolbar: another window isn't in its pictures.
- **Every monitor:** tray › **More Recording** › **Record All Monitors**. Settings › Recording says what that makes:
  - **one picture**, the monitors in their layout, at their own size or a height you choose;
  - **a video track for each**, which the video editor cuts between (Edit › **Cut to Screen**). The other monitors' tracks are hidden until you do. Overlays are drawn on the first monitor only.

## When it starts and stops by itself

Tray › **More Recording** › **Schedule a Recording…**: what to record (a monitor, every monitor, or a window), a profile, the time it starts, and when it stops: when you stop it, after a length, at a time, or when a program closes. The app and the PC have to be on at that time, and a recording set for later is kept if the app restarts. One found more than a minute late isn't made, and a notification says so. The same menu cancels it.

From the command line, nobody is there to pick an area, so these record the primary monitor unless they say otherwise:

```bash
CashGear.Capture.exe --record --duration 00:30:00
```

| Option | What it does |
|---|---|
| `--duration 00:30:00` | Stops after this length (`30:00` and `90` work too); the video has exactly this length |
| `--start-at 14:30` | Starts then: the next 14:30 to come |
| `--stop-at 15:00` | Stops then |
| `--until-closes notepad.exe` | Stops when that program has run and closed |
| `--profile Tutorial` | With a profile's settings, and what it records |
| `--all-monitors`, `--monitor 2`, `--window "Title"` | What to record |

## Profiles

A profile is a named set of recording settings: what it records, the frame rate and quality, the sound, overlays, the webcam, and the look the video editor opens it with. Right-click the record button on the capture toolbar (or Shift+F10 on it), or tray › **Record with a Profile**. Tutorial, Meeting and Bug report are there to start with; **Recording Profiles…** changes them and adds your own. What a profile doesn't name stays as Settings has it.

## Sound

- **System audio** (what you hear) is recorded by default: everything, only one app, or everything except one app.
- **A microphone** can be added (Settings › Recording). It can be cleaned up while it is recorded:
  - noise suppression for fans, hum and typing;
  - a noise gate between words;
  - a compressor, and a limiter.
- **Tracks:** one mixed track, a track per source (for editing later), or both.
- **Push-to-talk:** with a key set (Settings › Hotkeys), the microphone is only recorded while the key is held.
- **Even out the loudness** saves every recording at −16 LUFS.
- **Devices** are followed: when the Windows default changes, or a device is unplugged and plugged back in, the recording goes on.

## Overlays

Settings › **Overlays** chooses what recordings show of what you do:
- **Clicks:** a ring at every press, and optionally a click sound.
- **The pointer:** a halo or a spotlight, bigger, or hidden while it stands still.
- **Keystrokes:** shortcuts, or every key. Keys typed into password fields, and into the apps listed (password managers), are never shown or kept.
- **Your webcam:** a circle or a rounded box, placed by dragging in Settings. It is also kept as its own track for the video editor.
  - **Behind the person:** blurred or removed while recording, as long as the graphics card keeps up. If it can't, the webcam shows as it is, a notification says so, and the video editor can still do it, as the track stays as the camera saw it.
  - **A second camera** is recorded as a track of its own, for the video editor.
- **Drawing on the screen:** **Ctrl+Shift+D**, or the pen on the toolbar. Then:
  - 1–4 pick the pen, highlighter, arrow or rectangle;
  - Ctrl+Z undoes, Delete clears, Esc stops drawing.

A **metadata file** (`name.cgmeta.json`) beside each recording keeps the pointer, clicks and keys. The video editor uses it for zooms and effects. With **Keep the video clean**, nothing is drawn while recording, and tray › **Add Overlays to a Recording…** or the video editor adds them later.

## HDR

A monitor in HDR mode is tone-mapped to SDR, unless Settings › Recording › **Record a monitor in HDR mode as HDR** is on and the graphics card has a 10-bit encoder (the notification says when it hasn't). Then the video is HEVC Main10 with PQ, for a monitor or an area of one. Overlays are kept for the video editor, as they are drawn in SDR. The editor shows it tone-mapped, and exports it either way: any SDR preset tone-maps it, and **MP4 HEVC HDR** or **MP4 AV1 HDR** keep its light.

## When things happen during a recording

- **The graphics driver resets, or a laptop switches GPU:** the picture holds still for a moment, then the recording carries on.
- **The monitor changes resolution:** the rest is saved as "name (part 2).mp4".
- **The monitor is unplugged:** the recording stops and is saved.
- **The PC goes to sleep:** the recording pauses; resume or stop it from the toolbar.
- **The app or the PC stops:** the recording is kept. At the next start, a notification and tray › **Save Interrupted Recordings** save it.
