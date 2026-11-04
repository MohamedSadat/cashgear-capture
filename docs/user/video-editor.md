# The video editor

Tray › **Video Editor** › **Open a Recording or Project…**, or **Edit the Last Recording**. A recording opens as a project with the screen, the webcam and a sound track per source. Nothing ever changes the recording: **Export** makes a new file.

## The window

- **The preview:** drag pictures, annotations and zooms on it. The arrows move the selected one (Shift: 10 px).
- **The transport:** to the start, a frame back, play or pause (Space), a frame on, to the end.
- **The timeline:** a header per track (mute, solo, shown, lock, volume) and the clips. Click or drag the ruler to move the playhead.
- **The settings panel** for the selected item; with nothing selected (`Esc`), the project's look and canvas.
- **The media panel** (View › Media, `Ctrl+M`, the Media button), beside the preview, with five tabs: **Media**, **Transitions**, **Titles**, **Markers** and **Brand**.

## Editing

| | |
|---|---|
| **Split** | `S` splits every clip at the playhead |
| **Mark a range** | `I` and `O`, Shift+drag on the ruler, or drag over an empty part of a track |
| **Cut** | `Delete` takes the marked range out of every track and closes the gap; on a selected clip it deletes the clip, and `Shift+Delete` its time too |
| **Move and trim** | drag clips and their edges (Alt: no snapping). From the keyboard: Tab selects a clip, `Alt+←/→` moves it a frame (`Shift+Alt`: a second), `Ctrl+[` and `Ctrl+]` trim its start or end to the playhead |
| **Copy and paste** | `Ctrl+C`, `Ctrl+X`, `Ctrl+V`: clips, annotations and templates go at the playhead, where they have room, also into another editor window |
| **Group** | `Ctrl`+click chooses several items; `Ctrl+G` makes them move, cut and take keyframes as one; `Ctrl+Shift+G` undoes it |
| **Markers** | `M` puts a named marker at the playhead; `Ctrl+←` and `Ctrl+→` go between them; the Markers tab renames and moves them. Cuts carry them along |
| **Track order** | drag a track's header, use its right-click menu, or `Alt+↑` and `Alt+↓` on the selected item's track. Sound tracks stay below the others |
| **Ripple mode** | the Ripple button or View › Ripple Mode: deleting or trimming closes the gap on every unlocked track, lengthening makes room, and dragging a clip puts it between the others |
| **Undo** | `Ctrl+Z`, `Ctrl+Y` |
| **Save the project** | `Ctrl+S` writes `name.cgcap`. **Save with Media…** puts the recordings inside |

## Adding things

- **Any media** (the Media tab, Insert › Picture, Video or Sound…, `Ctrl+I`, or dropped on the timeline from Explorer): pictures (PNG, JPEG, WebP, an animated GIF that goes round and round) for as long as you like, and any video FFmpeg reads, with its sound. They go on as many media tracks as they need, over the screen. Zooms and layouts leave them where they are placed. **Add a Colour** adds a plain colour.
- **Music:** File › Add Music…
- **Annotations** (Insert menu): text, arrows, lines, shapes, highlights, blurred or pixelated areas, with fades and keyframes.
- **Speed:** 0.25× to 8×, or a speed that changes along the clip. Also **Freeze the Frame at the Playhead** and **Crossfade into the Next Clip** (Edit menu).
- **Effects from the recording's metadata:** click highlights, keystrokes, pointer effects.
- **Zooms:** Insert › Zoom, or **Auto-Zoom** from your clicks and typing.
- **Captions:**
  - Insert › **Captions from Speech…** listens with Whisper on this PC, in English or Arabic, and nothing is sent anywhere;
  - or type them, or import SRT/WebVTT files.

## Motion

Every picture and annotation has **position, scale, rotation, opacity and crop** in its panel, at the playhead. Off, **Changes make keyframes** sets a value for the whole item. On, each change makes a keyframe at the playhead, and the item moves between its keyframes. So does dragging a picture on the preview.

- **Keyframes** show as diamonds in a lane under the track. Click one to go there, drag it along. The panel adds and removes them and goes to the next or previous one.
- **From this keyframe on** sets how it goes to the next: even, ease in, ease out, both, a spring, or a curve of your own.
- A moving annotation's own keyframes also have a **colour** and an **opacity** there.
- **Moves with zooms** (per track): a webcam, picture or annotation track that the zoom's camera moves, as it does the screen. By default it stays where it is on the canvas, so a title doesn't fly off. A blurred area on such a track follows the pictures it hides.

## Transitions and effects

- **Transitions** (the Transitions tab): dissolve, dip to colour, wipes in four directions, slide, push, zoom, blur and circle. Drag one onto a cut or a clip's edge, or choose a clip and press `Enter` (the edge nearest the playhead). At a cut the clips come to overlap by its length, and what follows moves back. At a free edge the clip comes from or goes to nothing. A picture clip's panel sets its **way in** and **way out**: the kind, length, direction, colour and pace.
- **Fades** have a curve each: even, ease in, ease out, or both.
- **Effects** on a picture clip: brightness, contrast, saturation, temperature, a look-up table (a `.cube` file) and **chroma key** for a green screen (the colour, how near, the edge, and taking its spill out of what stays). The panel's shadow, border and corners are there too; a see-through picture's or keyed clip's shadow follows its shape.
- **Text animations:** how a text (or shape) comes in, what it does while shown, and how it goes out: fade, slide, pop, and for text a typewriter that reveals it letter by letter, Arabic letters staying joined.

## Titles, callouts and your brand

- **Titles** (the Titles tab, Insert › Title or Lower Third): title cards and lower thirds. Drag one onto the timeline or press `Enter`. Its panel has its texts, its length and its place; drag it on the preview. An Arabic lower third goes on the other side.
- **Callouts** (Insert › Callouts): speech bubbles, freehand (draw on the preview; `Esc` cancels), step numbers, spotlights and magnifiers.
- **Brand** (the Brand tab): colours, heading and body fonts, a logo, intro and outro clips and a look. Templates take its colours and fonts, so changing one changes them all. **Keep as My Brand Kit** saves it for other projects, and **Use My Brand Kit** brings it to this one.
- **Your library** (`%AppData%\CashGear\Capture\Library`): **Save Selection…** makes a template of the selected annotations, group or template. **Export a Pack…** saves your templates, their files and your brand kit as one `.cgpack` to share with a team, and **Import a Pack…** adds one.

## Sound

Select a sound clip: its settings are in the panel on the right.

- **Volume over time:** **Volume at the playhead** makes a volume keyframe there, on top of the clip's own volume. **Add a Volume Keyframe** makes one that changes nothing yet, to change later. On the clip, `Shift`+click adds one, and a selected clip's keyframes are dragged in time and level. The waveform follows them. They also show in the lane under the track, as a picture's keyframes do.
- **Even Out** measures how loud the clip is, with its effects on (EBU R128, on this PC), and sets its **Gain** to bring it to −16 LUFS. If that would push its peaks past the top, its limiter goes on.
- **Noise removal** (RNNoise) takes fans, hum and keyboards out of speech; **Strength** mixes some of the original back in. It is for voices: it treats music as noise. The noise gate, compressor and limiter are the ones the microphone has in Settings.
- **Equalizer:** Voice, Warm, Phone, or your own low cut and lows, middle and highs.
- **Sound over the crossfade** (on the clip coming in): **Equal power** stays as loud across it, for two different sounds; **Straight line** is what older projects have, and suits two takes of one sound.
- **Keeps its pitch** (a clip whose speed changes along it): on for new ramps. Older projects' ramps are resampled, so their pitch follows the speed, until you turn it on.

Preview and export play the same samples, wherever you start playing from and however the clip is cut.

**Ducking:** music goes down while the voice speaks. Edit › **Duck the Music Under the Voice** does it for music added with Add Music and for tracks marked **A background track**. Or select a clip: **Goes down by** (−12 dB), **Going down takes** (0.3 s, ending as the voice starts), **Coming back takes** (0.5 s, starting as it stops) and **Duck This Track Under the Voice**. The voice is found on the track a transcript was made from, else the microphone's. The result is ordinary volume keyframes on the music, in place of any it had, so you can change any of them, or duck again with other settings.

**Where you hear it:** View › **Sound Output** plays the preview on another device from now on, without stopping. A device that has been unplugged gives way to Windows' default.

## Large media

View › **Smaller Copies for the Preview** (1080p or 720p): 4K media previews from a smaller copy made once on this PC, with the progress in the status line, and kept in `%LocalAppData%\CashGear\Capture\VideoEditor\proxies`. The export always reads the original.

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
- **Formats:** MP4 (H.264 or HEVC), MP4 for 9:16, 1:1 or 4:5, MKV (H.264), WebM (VP9 or AV1), an animated GIF, or just the sound as M4A, MP3, WAV (24-bit) or FLAC. A 9:16, 1:1 or 4:5 export of a project of another shape follows the pointer, whatever its canvas is, and says when it couldn't.
- **Options:** four qualities, the marked range only, and captions burned in or saved beside it.
- **Loudness:** the whole mix is measured and turned up or down to −16 LUFS (speech online, the default), −14 (YouTube) or −23 (broadcast), with a limiter on its peaks, or left **As mixed**. The preview plays it as mixed.
- **Sound tracks:** one mix; or the mix and each track as a stream of its own in a video file (MP4, MKV, WebM), titled with its name, so an editor or a player can choose; or each track as a 24-bit WAV beside the file (`name.Microphone.wav`). Only the tracks you can hear in the mix go out. Each is what a solo export of it would be.
