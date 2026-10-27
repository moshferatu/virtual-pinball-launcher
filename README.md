# Virtual Pinball Launcher

Windows launcher for Visual Pinball X tables with looping playfield previews, optional backglass previews, per-table play stats, and built-in preview recording.

## Features

- Scans a folder of `.vpx` tables and builds a fullscreen launcher UI.
- Plays matching preview videos for the selected table.
- Plays matching backglass videos on a secondary display when available.
- Launches tables directly in Visual Pinball X.
- Tracks plays, total play time, and last played date in `table-stats.csv`.
- Records new playfield and backglass preview videos through `ffmpeg`.
- Every key is configurable in one `KeyBindings` list, including global chords such as `F12+1` that start other programs.

## Requirements

- Windows
- .NET 8 SDK or runtime
- Visual Pinball X
- `ffmpeg` available on `PATH`, or configured with an absolute path in `config.json` if you want recording support

## Project Layout

- `VirtualPinballLauncher/` contains the WPF application.
- `VirtualPinballLauncher.Input/` is a small library shared by the launcher and the checks: key chord parsing and the global keyboard hook.
- `VirtualPinballLauncher/example_config.json` is the sample configuration.
- `tests/LauncherChecks/` holds the regression checks.
- `tools/Publish-Launcher.ps1` publishes the launcher into one folder.
- `virtual-pinball-launcher.sln` is the solution file.

## Build

```bash
dotnet build
```

The app targets `net8.0-windows` and uses WPF.

Run the regression checks with:

```bash
dotnet run --project tests/LauncherChecks
```

These cover stats, DMD formatting, queued playback, native VLC looping and shutdown, preview-file replacement failures, key binding parsing, chord handling, config round-trips, and the process-tree wait used by the Run action (which briefly starts hidden `cmd.exe` processes). Playback checks use silent audio with dummy outputs; they do not launch VPX or record the desktop.

To also check startup keyboard focus, cabinet-button navigation through the native window input route, and the topmost handling around a launched program, run `dotnet run --project tests/LauncherChecks -- --window-focus`. This briefly opens small test windows without loading your cabinet configuration or launching VPX.

To publish a self-contained copy outside the repository:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Publish-Launcher.ps1 -Destination C:\vPinball\Launcher
```

Add `-SingleFile` to pack the launcher into one exe, and `-FrameworkDependent` to use an installed .NET 8 Desktop Runtime (`winget install Microsoft.DotNet.DesktopRuntime.8`) instead of bundling it. Approximate sizes:

| Options | Size |
|---|---|
| none (self-contained folder) | 203 MB |
| `-SingleFile` (compressed) | 111 MB |
| `-FrameworkDependent` | 44 MB |
| `-SingleFile -FrameworkDependent` | 44 MB |

The `libvlc` folder, `ScreenRecorderLib.dll` and `flipper.wav` always sit next to the launcher, because VLC loads its plugins from disk and ScreenRecorderLib can't load from inside a single-file exe. Only the VLC plugins needed for local preview files are shipped (see the list in `VirtualPinballLauncher.csproj`). If a preview format stops playing, add the missing plugin there.

## Configuration

On first launch, the app creates `config.json` next to the executable if it does not already exist. Update that file before using the launcher.

Example:

```json
{
  "TablesDirectory": "D:\\Pinball\\Tables",
  "VideosDirectory": "D:\\Pinball\\Videos",
  "BackglassVideosDirectory": "D:\\Pinball\\BackglassVideos",
  "VpxExePath": "C:\\Visual Pinball\\VPinballX64.exe",
  "VideoExtension": ".mp4",
  "RecordSeconds": 30,
  "RecordDelaySeconds": 10,
  "FfmpegPath": "ffmpeg",
  "KeyBindings": [ ... ]
}
```

See `VirtualPinballLauncher/example_config.json` for the full file.

### Config Fields

- `TablesDirectory`: Folder containing `.vpx` tables. Only the top level is scanned.
- `VideosDirectory`: Folder containing playfield preview videos.
- `BackglassVideosDirectory`: Folder containing backglass preview videos. Leave empty if you do not use backglass previews.
- `VpxExePath`: Full path to the Visual Pinball X executable.
- `VideoExtension`: Extension used when matching preview files, such as `.mp4`.
- `RecordSeconds`: Length of recorded preview videos.
- `RecordDelaySeconds`: Delay after launching a table before recording starts.
- `FfmpegPath`: `ffmpeg` executable name or full path.
- `KeyBindings`: Every key the launcher reacts to. See Key Bindings below.

Relative `Command` paths are resolved next to the launcher executable. Fields this version does not know about are preserved when the file is rewritten. A `config.json` written by an older launcher gets the new sections added with their defaults on first load so they are visible and editable.

## File Matching

Tables and videos are matched by base filename.

Example:

- `TablesDirectory\Attack From Mars.vpx`
- `VideosDirectory\Attack From Mars.mp4`
- `BackglassVideosDirectory\Attack From Mars.mp4`

If a matching preview or backglass video is missing, the launcher simply shows no video for that display.

## Running

From the repo:

```bash
dotnet run --project VirtualPinballLauncher
```

Or run the built executable directly. Only one launcher runs at a time; a second copy exits quietly.

## Key Bindings

All keys live in the `KeyBindings` list in `config.json`. The defaults reproduce the original controls plus three program chords:

```json
"KeyBindings": [
  { "Keys": ["Down", "Right", "RightShift", "PageDown"], "Action": "NextTable" },
  { "Keys": ["Up", "Left", "LeftShift", "PageUp"],       "Action": "PreviousTable" },
  { "Keys": ["Enter", "Space", "1", "NumPad1"],          "Action": "LaunchTable" },
  { "Keys": "F12",                                       "Action": "RandomTable" },
  { "Keys": ["LeftCtrl", "RightCtrl"],                   "Action": "ToggleSort" },
  { "Keys": "R",                                         "Action": "RecordPreview" },
  { "Keys": "Escape",                                    "Action": "Exit" },
  { "Keys": "5+Left",                                    "Action": "VolumeDown" },
  { "Keys": "5+Right",                                   "Action": "VolumeUp" },
  { "Keys": "F12+1", "Action": "Run",
    "Command": "C:\\Devil's Crush\\devils-crush.exe",
    "Arguments": "--cabinet --music-voice-overlay",
    "WorkingDirectory": "C:\\Devil's Crush" },
  { "Keys": "F12+5", "Action": "Run", "Command": "C:\\Alien Crush\\Launch-Cabinet.cmd" },
  { "Keys": "F12+Escape", "Action": "Run", "Command": "C:\\Tetris\\TetrisOLED\\bin\\Debug\\net8.0-windows\\TetrisOLED.exe" }
]
```

Mouse wheel navigation is always on.

### Keys

- `Keys` is one key, a chord, or a list of keys that share the action.
- Key names follow the WPF `Key` enumeration (`RightShift`, `LeftCtrl`, `PageDown`, `NumPad1`, `F12`, ...). Digits may be written `1` or `D1`. `Esc`, `Return`, `PgUp`, `PgDn`, `Del`, `Ins` are accepted aliases. Case does not matter.
- A chord is two keys joined with `+`. The first is held as a modifier while the second is pressed, for example `F12+Escape`.
- Single keys work only while the launcher has focus. Chords work everywhere, including while a table or another program is running, so they behave like global hotkeys. The chorded key is swallowed so nothing else reacts to it.
- A modifier key keeps its own single-key action: a tap of `F12` on its own still picks a random table. That single action fires on release, only if no chord was pressed while the key was held. While a modifier is held, its partner keys are not treated as launcher keys, so `F12+Enter` never launches a table.
- A key may be a chord modifier and complete another chord at the same time. `5` starts `5+Left` and also completes `F12+5`.
- Problems such as an unknown key or action, a key bound twice, or a `Run` entry with no `Command` are listed in one message at startup. Valid entries still load. The first binding of a key wins. A `Run` command whose file is missing is reported when its key is pressed.

### Actions

- `NextTable`, `PreviousTable`, `LaunchTable`, `RandomTable`, `ToggleSort`, `RecordPreview`: the launcher's own functions.
- `Exit`: closes the launcher.
- `Run`: starts a program the same way a table is started. The launcher drops out of the way, waits for the program's main window (its largest visible window, so splash and helper windows are ignored), gives it a second to settle, brings it to the front, and keeps re-checking for up to 10 seconds until the program verifiably holds the foreground. It then waits for the program to finish and restores itself. Fields: `Command` (required), `Arguments`, `WorkingDirectory`, `WaitForProcess`, `SingleInstance`, `ShowConsole`.
  - The launched process is placed in a job object, so a batch file or wrapper that exits immediately still counts as running until everything it started has finished. Nothing needs to be configured for `.cmd` launchers.
  - `WaitForProcess`: wait on and activate this process name instead, for programs that deliberately detach from their parent.
  - `SingleInstance`: if the program is already running, bring it to the front instead of starting another copy.
  - `ShowConsole`: console windows are hidden by default, both the one Windows opens for a console-subsystem program or a batch file and any the program opens later. Set this to `true` for a program that needs its console visible.
- `KillTable`: ends Visual Pinball X. Works while a table is running.
- `VolumeUp`, `VolumeDown`: master volume, one percent per press, with the on-screen overlay. Hold the key to keep going; the change speeds up the longer it is held. Work while a table is running.
- `ShutdownPc`, `RestartPc`: power off or restart the PC. Work while a table is running.

While a table or a `Run` program is on screen, only `KillTable`, the volume actions, and the power actions respond.

## Sorting and Stats

The launcher starts in popularity sort mode.

- Popularity sort orders tables by total recorded play time, then by name.
- Alternate sort orders tables alphabetically.
- Stats are stored in `table-stats.csv` inside `TablesDirectory`.

Each table tracks:

- Number of times played
- Total time spent playing
- Last played date

## Recording Preview Videos

Press `R` on a selected table to record new preview media. The launcher starts a separate recorder process that:

1. Launches the selected VPX table.
2. Waits for `RecordDelaySeconds`.
3. Records the primary display as the playfield preview.
4. Records the detected backglass display in parallel if `BackglassVideosDirectory` is configured.
5. Stops VPX and common PinUp Player processes after recording completes.

You can also call record mode directly:

```bash
VirtualPinballLauncher.exe --record "D:\Pinball\Tables\Example Table.vpx"
```

Recorded files are written to:

- `VideosDirectory\<TableName><VideoExtension>`
- `BackglassVideosDirectory\<TableName><VideoExtension>` when backglass recording is enabled

## Display Behavior

The current implementation assumes a cabinet-style multi-monitor setup:

- The primary monitor is treated as the playfield for preview recording.
- The rightmost monitor is used for the DMD/info window.
- The second-rightmost monitor is used for the backglass window.

---
