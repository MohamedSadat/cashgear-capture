# Troubleshooting

| Problem | What to do |
|---|---|
| "Windows protected your PC" when installing | The build isn't signed (or is new and has no reputation yet). Choose **More info › Run anyway**, if you got Setup.exe from CashGear |
| No tray icon | Look under **^** in the notification area. If Windows refused the icon, the app says so in a window; its hotkeys still work. Restarting Explorer or Windows usually helps |
| A hotkey doesn't work | Settings › Hotkeys shows which ones another app owns. Choose other keys, or close that app |
| A capture of a video is black | Streaming services and some players protect their video, and Windows shows it black to every capture app |
| "Windows is showing a secure screen" | A UAC prompt or the lock screen is up: no app can capture it |
| Recording says FFmpeg is missing | Install CashGear Capture again from Setup.exe; a development build needs `tools\get-ffmpeg.ps1` |
| No sound in a recording | Settings › Recording: check system audio, the output device and the microphone; the toolbar's meters show what is heard |
| A yellow border while recording (Windows 10) | Windows draws it around what Windows.Graphics.Capture reads; it isn't in the video |
| The app is in the wrong language | Settings › General › Language, then restart the app |
| Something else | Settings › General › **Export diagnostics…**, and send the zip with a description |
