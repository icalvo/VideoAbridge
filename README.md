# VideoAbridge

Windows WPF app for making abridged versions of existing videos: markers (including scene-cut detection), delete/restore ranges, per-deletion hard cut or crossfade, and optional end fade-to-black.

## Requirements

- .NET 11 SDK
- FFmpeg and ffprobe on `PATH` (or place `ffmpeg.exe` / `ffprobe.exe` under `tools/ffmpeg/` and copy them next to the app output)
- Windows (LibVLC via `VideoLAN.LibVLC.Windows`)

## Build & run

```bash
dotnet build VideoAbridge.slnx
dotnet run --project src/VideoAbridge/VideoAbridge.csproj
```

```bash
dotnet test VideoAbridge.slnx
```

## Usage

1. **File → Open Video** — probes duration, restores cached edit state for that file (markers, cuts, fades, joins), and resumes scene detection.
2. Timeline markers can be added/removed/dragged. Ctrl+Wheel zooms.
3. Select markers, then **Delete Section** / **Undelete Section** (or from beginning / to end).
4. Click a **Cut** / **XF** label under a deleted section to configure that join (hard cut or crossfade seconds, capped by adjacent kept lengths).
5. Optional start/end fades in the side panel (duration `0` = off).
6. **File → Export** writes an abridged MP4 via FFmpeg.

## Solution layout

- `VideoAbridge.Core` — immutable timeline domain (`Result<T>`, no domain exceptions)
- `VideoAbridge.FFmpeg` — probe, scene detect, marker thumbnails, export
- `VideoAbridge` — WPF UI + LibVLC playback
- `VideoAbridge.Core.Tests` — domain unit tests
