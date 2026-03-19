# Virtual Pinball Launcher

Windows launcher for Visual Pinball X tables with looping playfield previews, optional backglass previews, per-table play stats, and built-in preview recording.

## Features

- Scans a folder of `.vpx` tables and builds a fullscreen launcher UI.
- Plays matching preview videos for the selected table.
- Plays matching backglass videos on a secondary display when available.
- Launches tables directly in Visual Pinball X.
- Tracks plays, total play time, and last played date in `table-stats.csv`.
- Records new playfield and backglass preview videos through `ffmpeg`.

## Requirements

- Windows
- .NET 8 SDK or runtime
- Visual Pinball X
- `ffmpeg` available on `PATH`, or configured with an absolute path in `config.json` if you want recording support

## Project Layout

- `VirtualPinballLauncher/` contains the WPF application.
- `VirtualPinballLauncher/example_config.json` is the sample configuration.
- `virtual-pinball-launcher.sln` is the solution file.

## Build

```bash
dotnet build
```

The app targets `net8.0-windows` and uses WPF.

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
  "FfmpegPath": "ffmpeg"
}
```

### Config Fields

- `TablesDirectory`: Folder containing `.vpx` tables. Only the top level is scanned.
- `VideosDirectory`: Folder containing playfield preview videos.
- `BackglassVideosDirectory`: Folder containing backglass preview videos. Leave empty if you do not use backglass previews.
- `VpxExePath`: Full path to the Visual Pinball X executable.
- `VideoExtension`: Extension used when matching preview files, such as `.mp4`.
- `RecordSeconds`: Length of recorded preview videos.
- `RecordDelaySeconds`: Delay after launching a table before recording starts.
- `FfmpegPath`: `ffmpeg` executable name or full path.

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

Or run the built executable directly.

## Controls

- `Down`, `Right`, `RightShift`, `PageDown`: Next table
- `Up`, `Left`, `LeftShift`, `PageUp`: Previous table
- Mouse wheel: Navigate tables
- `Enter` or `Space`: Launch selected table
- `R`: Record preview video for the selected table
- `1` or `NumPad1`: Jump to a random table
- `LeftCtrl` or `RightCtrl`: Toggle sort mode
- `Escape`: Exit the launcher

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