# The video editor

Tray › **Video Editor** › **Open a Recording or Project…**, or **Edit the Last Recording**. A recording opens as a project with the screen, the webcam and a sound track per source. Nothing ever changes the recording: **Export** makes a new file.

## The window

- **The preview:** drag pictures, annotations and zooms on it. The arrows move the selected one (Shift: 10 px).
- **The transport:** to the start, a frame back, play or pause (Space), a frame on, to the end.
- **The timeline:** a header per track (mute, solo, shown, lock, volume) and the clips. Click or drag the ruler to move the playhead.
- **The settings panel** for the selected item; with nothing selected (`Esc`), the project's look and canvas.

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

## Editing by the transcript

Edit a talk-over by editing its text. View › **Transcript** (`Ctrl+T`, or the Transcript button) opens the panel beside the preview.

- **Make the Transcript…** listens to a sound track's recording on this PC, in English or Arabic, with the time of each word. The transcript belongs to the recording, so it stays right through cuts, speed changes and freezes. "Write down filler words" asks Whisper to keep "um" and "uh". If the project has captions made from speech, the panel offers to use their words instead.
- **Read and go:** paragraphs split at pauses, the word being said is marked as it plays, and a click on a word goes there.
- **Delete cuts:** select words (the mouse, or `Shift` with the arrows) and press `Delete`. That part of the video goes from every unlocked track, cut in the middle of the silence between words, with a crossfade of 16 ms on the sound so it doesn't click. It is an ordinary cut: the timeline shows it, and undo brings it back.
- **Typing corrects:** start typing on a word (or `F2`, or double-click it). The box says the video stays as it is: only the transcript and the captions saying that word change. A corrected word has a dotted line under it.
- **Cut words** are struck through and can be put back: select them and press `Ctrl+R`, or **Restore**. Clear **Show cut words** to hide them. What was cut with them on other tracks, such as an annotation, stays cut; undo brings that back too.
- **Silences…** lists the pauses in the voice longer than 0.75 s (you can change that) and shortens them to 0.3 s, rather than removing them. Play each one, keep it or shorten it, or **Shorten All**.
- **Filler Words…** lists um, uh, er, ah, hmm and اممم, إممم, آه, words said twice in a row ("the the"), and voiced sound between words that has no word (possibly a filler Whisper left out). Play each, keep it or remove it, or **Remove All**. "يعني" counts only when you ask, since it is also a real word.
- **Make Captions** (or Insert › Captions from the Transcript) makes a caption track of the words as this cut plays them.

Where Whisper gives no word times for a stretch, it is cut as a whole sentence, and the panel says so.

**Translate** (Insert › Translate…): English captions into Arabic with Opus-MT (the model, 113 MB, is downloaded the first time), or Arabic speech into English with Whisper. Each makes a caption track of its own, named for its language. When there are several, the export window asks which to burn in, and saves an SRT or WebVTT file for each. Machine translation makes mistakes: read it before you use it.

## The look

A recording looks finished before you edit it: the screen sits on a background with space round it, rounded corners and a shadow. With nothing selected, the panel on the right sets all of it.

- **Look:** None, Soft, Gradient, Dark, or one you saved under **Saved looks**. New recordings open with Soft; View › **Use This Look for New Recordings** changes that (None keeps recordings as they were recorded).
- **Background:** one colour, a gradient across or from the middle, a picture, or the screen itself blurred. **Moves with zooms** lets it move a little in a zoom.
- **The screen:** the space around it, corners, a shadow (how strong, how soft, how far it drops, its colour), an inner line, and a **device** drawn round it (a browser window, a laptop, a monitor, a phone).

A selected picture has its own corners and shadow in its panel.

## Zooms and the pointer

A zoom's panel sets its **movement**, smooth or a spring that goes a little past its place and settles, and its **motion blur** (on for new zooms; nothing blurs while the camera stands still).

When the app draws the pointer (recordings made with "Keep the video clean"), its panel can **draw it as a sharp shape** (the standard arrow, hand and I-beam; a custom pointer stays as recorded), **shrink it at a click**, and **keep its size in a zoom**. The pointer and click rings stay sharp while the pictures blur.

## The webcam

- **Layouts** (Insert › Layout): corner bubble, side by side (one above the other on a tall canvas), camera only, screen only. A layout covers its time on a layout track; the change takes 0.4 s, or what its panel says. While a layout places the pictures, dragging them doesn't move them.
- **Behind the person** (select a webcam clip): blurred, replaced with a colour or a picture, or removed, so the camera sits on what is below it. The person is found on this PC, once for each recording, which the status line shows; until then the camera is shown as it is. The small model is downloaded the first time, and no pictures leave the PC.

## Other shapes

The panel's **Canvas** makes the video 16:9, 9:16 (Shorts, Reels, TikTok), 1:1 or 4:5. **Screen on the canvas** then says how the screen fills it: all of it on the background, filling it from the middle, or filling it and following the pointer. A webcam goes above or below the screen. The panel says at what times the pointer moves too fast to stay in the picture, and without the recording's metadata the picture stays on its middle.

## Export

`Ctrl+E`:
- **Formats:** MP4 (H.264 or HEVC), MP4 for 9:16, 1:1 or 4:5, WebM (VP9 or AV1), an animated GIF, or just the sound as M4A or MP3. A 9:16, 1:1 or 4:5 export of a project of another shape follows the pointer, whatever its canvas is, and says when it couldn't.
- **Options:** four qualities, the marked range only, and captions burned in or saved beside it.
