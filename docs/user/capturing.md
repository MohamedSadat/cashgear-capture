# Capturing

## Kinds of capture

Every kind is in the tray menu (some under **More Captures**) and on the [capture toolbar](#the-capture-toolbar). Any of them can have a hotkey (Settings › Hotkeys).

| Capture | How |
|---|---|
| **Region** (`PrintScreen`) | Drag a rectangle, or click a window, a part of a window or a monitor; Enter captures, Esc cancels |
| **Window** | Click a window: its own content, even where other windows cover it |
| **Active window** (`Alt+PrintScreen`) | The window in front |
| **Full screen** (`Ctrl+PrintScreen`) | Every monitor, as one image, one per monitor, or both (Settings › Capture) |
| **Scrolling** (`Ctrl+Shift+PrintScreen`) | Click a window at the part that scrolls: it is scrolled to the bottom and stitched into one tall image. Scroll to the top first. **Finish now** keeps what was captured |
| **Fixed-size region** | A frame of a set size follows the pointer; Tab switches sizes, arrows nudge it, Enter captures |
| **Freehand region** | Draw around what you want with the button held |
| **Polygon region** | Click the corners; Enter or a click on the first corner captures; Backspace takes a corner back |
| **Rounded region** | A region with rounded corners; the mouse wheel sets the radius |
| **Object** | The part of a window under the pointer (a button, a panel), as Windows reports it; the wheel goes out and in |
| **Open menu** | Open a menu during the countdown: just the menus are captured |
| **Capture Text** (`Ctrl+Alt+PrintScreen`) | Select an area: its text is copied to the clipboard (Windows OCR, English and Arabic in one area) |

Freehand, polygon, rounded and menu captures are transparent outside their shape and are saved as PNG.

**Repeat Last Capture** (`Ctrl+Alt+Shift+PrintScreen`) takes the last capture again without selecting: the same pixels for a region, or the same window.

## Delays

Tray › **Capture Delay** waits 3, 5 or 10 seconds before every capture, so you can open a menu or hover a tooltip. A small box in the corner counts down; press **Esc** or click **Cancel** to stop. Settings › Capture gives each kind of capture its own delay.

## The capture toolbar

Tray › **Capture Toolbar** shows a small bar with a button for every kind of capture, repeat, recording, the tools, the delay, what happens after a capture, and Settings.
- **Never in the way:** it never takes the focus, so "active window" still means your window, and it is never in a capture.
- **Placing it:** drag it by its grip; dropped at the edge of a screen, it docks there.
- **From the keyboard:** press **Ctrl+Alt+Shift+T** (Settings › Hotkeys). Tab moves between its buttons, Enter presses one, and Esc gives the focus back.

## Screen tools

From tray › **Tools**, the toolbar, or a hotkey:
- **Magnifier.** A lens follows the pointer, 2× to 8× (wheel or + and −). It shows the pixel's position and colour. Arrows move a pixel; Esc closes it.
- **Colour picker.** Click any pixel and its colour is copied (HEX by default). The window shows it as HEX, RGB, HSL and C#, and keeps the last 16.
- **Screen ruler.** Measures in pixels, inches or centimetres. Drag it, drag its end to change the length, double-click to turn it. Arrows nudge it; Esc closes it.
- **Crosshair.** Lines through the pointer across every monitor, with its position.

None of them is ever in a capture or a recording.

## Capture history

The last 20 captures are kept, even ones you only copied.
- Tray › **Recent Captures** shows them with thumbnails.
- Tray › **Capture History…** can open, copy, show, save and remove them.

The number kept is set in Settings › General.

## When Windows doesn't allow a capture

- **A UAC prompt or the lock screen:** no app can capture these, and the app says so.
- **Protected video** (streaming services, some players) and windows that keep themselves out of captures come out black. The app tells you which window it was.
- **Minimized windows**, and windows on another virtual desktop or off every monitor, are explained with how to bring them back.
