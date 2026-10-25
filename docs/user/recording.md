# Recording

## Starting and stopping

Press **Shift+PrintScreen** (or tray › **Record Screen…**). Drag an area and release to start, or click a window or a monitor and press **Enter**. The recording toolbar appears at the top of the screen, and it is never in the video:
- the time;
- **Pause**, **Stop**, the pen for drawing on the screen;
- the sound meters, **Mute mic**, and the sound button (volumes, noise suppression).

**Stop**, or the hotkey again, saves an MP4 to the capture folder. A notification says how long and how big it is; click it to show the file.

To use the toolbar from the keyboard, press **Ctrl+Alt+Shift+T**: Tab moves between the buttons and Esc goes back.

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
- **Drawing on the screen:** **Ctrl+Shift+D**, or the pen on the toolbar. Then:
  - 1–4 pick the pen, highlighter, arrow or rectangle;
  - Ctrl+Z undoes, Delete clears, Esc stops drawing.

A **metadata file** (`name.cgmeta.json`) beside each recording keeps the pointer, clicks and keys. The video editor uses it for zooms and effects. With **Keep the video clean**, nothing is drawn while recording, and tray › **Add Overlays to a Recording…** or the video editor adds them later.

## When things happen during a recording

- **The graphics driver resets, or a laptop switches GPU:** the picture holds still for a moment, then the recording carries on.
- **The monitor changes resolution:** the rest is saved as "name (part 2).mp4".
- **The monitor is unplugged:** the recording stops and is saved.
- **The PC goes to sleep:** the recording pauses; resume or stop it from the toolbar.
- **The app or the PC stops:** the recording is kept. At the next start, a notification and tray › **Save Interrupted Recordings** save it.
