# The video editor

Tray › **Video Editor** › **Open a Recording or Project…**, or **Edit the Last Recording**. A recording opens as a project with the screen, the webcam and a sound track per source. Nothing ever changes the recording: **Export** makes a new file.

## The window

- **The preview:** drag pictures, annotations and zooms on it. The arrows move the selected one (Shift: 10 px).
- **The transport:** to the start, a frame back, play or pause (Space), a frame on, to the end.
- **The timeline:** a header per track (mute, solo, shown, lock, volume) and the clips. Click or drag the ruler to move the playhead.
- **The settings panel** for the selected item.

## Editing

| | |
|---|---|
| **Split** | `S` splits every clip at the playhead |
| **Mark a range** | `I` and `O`, Shift+drag on the ruler, or drag over an empty part of a track |
| **Cut** | `Delete` takes the marked range out of every track and closes the gap; on a selected clip it deletes the clip, and `Shift+Delete` its time too |
| **Move and trim** | drag clips and their edges (Alt: no snapping). From the keyboard: Tab selects a clip, `Alt+←/→` moves it a frame (`Shift+Alt`: a second), `Ctrl+[` and `Ctrl+]` trim its start or end to the playhead |
| **Undo** | `Ctrl+Z`, `Ctrl+Y` |
| **Save the project** | `Ctrl+S` writes `name.cgcap`. **Save with Media…** puts the recordings inside |

## Adding things

- **Music:** File › Add Music…
- **Annotations** (Insert menu): text, arrows, lines, shapes, highlights, blurred or pixelated areas, with fades and keyframes.
- **Speed:** 0.25× to 8×, or a speed that changes along the clip. Also **Freeze the Frame at the Playhead** and **Crossfade into the Next Clip** (Edit menu).
- **Effects from the recording's metadata:** click highlights, keystrokes, pointer effects.
- **Zooms:** Insert › Zoom, or **Auto-Zoom** from your clicks and typing.
- **Captions:**
  - Insert › **Captions from Speech…** listens with Whisper on this PC, in English or Arabic, and nothing is sent anywhere;
  - or type them, or import SRT/WebVTT files.

## Export

`Ctrl+E`:
- **Formats:** MP4 (H.264 or HEVC), WebM (VP9 or AV1), an animated GIF, or just the sound as M4A or MP3.
- **Options:** four qualities, the marked range only, and captions burned in or saved beside it.
