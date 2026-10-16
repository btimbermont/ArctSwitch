# ArctSwitch

Tray app that switches Windows' default audio devices when a SteelSeries Arctis 7 (2017/2019) or
Arctis Pro Wireless headset is powered on or off. It reads the headset's power state from the
wireless dongle over HID, so it works even though the dongle's audio devices stay connected.

## Requirements
- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Use
- Run `ArctSwitch.exe`. On first run the settings window opens. Pick the sound output,
  communications output and microphone for "headset ON" and "headset OFF", then Save.
  The status check interval is configurable there too (default 1500 ms, minimum 500 ms); a switch
  happens after two identical readings, so it takes roughly 1–2 intervals.
- Tray icon: orange headset = headset on, with a vertical battery bar on the right (green above 50%,
  yellow above 20%, red below); grey speaker = headset off. Double-click it to open
  settings; right-click for manual switching, auto-switch on/off, "Start with Windows" and the log folder.
- Settings and log: `%AppData%\ArctSwitch\`.

## Diagnostics
- `ArctSwitch.exe --probe 30` prints the dongle's raw status replies for 30 s (byte 3 of `06-14-..`
  is `03` when the headset is on, `01` when off).
- `ArctSwitch.exe --list` lists audio devices and the current defaults.

## Build
.NET 8 SDK required.

    dotnet publish ArctSwitch -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

## Releasing
GitHub Actions builds every push to `main` (the exe is attached to the run as an artifact).
To publish a release: **Actions → Release → Run workflow**, enter a version such as `1.0.0`.
The workflow builds with that version, creates tag `v1.0.0` and a GitHub release with
`ArctSwitch.exe` attached and auto-generated release notes.

## Credits
The dongle's HID protocol was reverse-engineered by the community, notably
[HeadsetControl](https://github.com/Sapd/HeadsetControl). This project is not affiliated with SteelSeries.
