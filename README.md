# VideoAbridge

![VideoAbridge](docs/readme-screenshot.png)

**Mission:** make it easy to produce short versions of a **single, already edited** video.

VideoAbridge is not a general [NLE](https://en.wikipedia.org/wiki/Non-linear_editing) tool and not a multi-clip editor. You open one finished cut, mark what to drop, and export a tighter cut—with optional alternate abridgements of the same source—without rebuilding the timeline from scratch.

## Features

### Editing the abridgement

- **Markers** — place user markers at the playhead; select one or more to define section boundaries
- **Delete / undelete sections** — remove the range between selected markers, or restore it; also delete from the start or through the end of the video
- **Scene detection** — FFmpeg-based scene cuts become shared scene markers (protected from casual delete); detection resumes when you reopen a file
- **Per-join transitions** — each interior deletion is a hard cut or a crossfade; crossfade length is capped by the adjacent kept segments
- **Start / end fades** — optional fade from black and fade to black on the abridged output
- **Undo / redo** — full edit history for the active version (Ctrl+Z / Ctrl+Y)

### Versions

- **Multiple abridged versions** of the same source video (tab bar with `+` while a video is open)
- **New version from scenes** — start a version that keeps only shared scene-detect markers
- **Duplicate / rename / delete / reorder** versions (drag tabs; double-click or F2 renames inline); scene markers stay shared across versions

### Preview & playback

- **LibVLC** playback of the source file with playhead sync to the timeline
- **Edit vs Preview mode** — Edit shows the full source with deleted ranges; Preview shows the abridged timeline (kept sections only) with blackout / crossfade visuals approximating the export
- Marker **thumbnails** along the timeline

### Timeline navigation

- Click timeline to seek; Ctrl+Wheel to zoom
- **Zoom to abridged content** — fit all undeleted sections in view
- **Zoom to fit** — full zoom out over the whole displayed timeline

### Persistence & export

- **Automatic cache** of versions, markers, cuts, joins, fades, Edit/Preview mode, per-mode zoom/scroll/playhead, Tasks pane state, and window placement keyed by file content fingerprint (reopen the same file → restore work)
- **Recent Files** — File menu submenu of recently opened videos (persisted under LocalAppData)
- **Export** — FFmpeg writes an abridged MP4 (kept clips + joins + fades)
- **Tasks pane** — sidebar for long jobs (opens for export/scene detect; waveform runs quietly), with cancel / open-result actions

### Keyboard shortcuts

Shortcuts are ignored while typing in a text box.

| Shortcut              | Action                                    |
| --------------------- | ----------------------------------------- |
| Ctrl+O                | Open Video                                |
| Ctrl+S                | Export                                    |
| P                     | Play / Pause                              |
| Home                  | Go to Beginning                           |
| Ctrl+Z / Ctrl+Y       | Undo / Redo                               |
| M                     | Add Marker                                |
| Delete                | Delete Marker / Delete Section            |
| Shift+Delete          | Restore Section                           |
| Ctrl+Home / End       | Delete Section from Start / to End        |
| Ctrl+Shift+Home / End | Restore Section from Start / to End       |
| Ctrl+D                | Detect Scenes                             |
| Tab                   | Toggle Edit / Preview                     |
| Left / Right          | Focus previous / next marker or cut (Edit)|
| Up / Down             | Focus nearest item in the other lane (Edit)|
| Space / Shift+Space   | Click / Shift-click focused item (Edit)   |
| Ctrl+1 / Ctrl+0       | Zoom to abridged content / Zoom to fit    |
| Ctrl+N / Ctrl+Shift+N | New / Duplicate Version                   |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / Previous Version               |
| F2                    | Rename Version (inline on the active tab) |
| Ctrl+Shift+T          | Expand / collapse Tasks pane              |

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
dotnet test tests/VideoAbridge.Tests/VideoAbridge.Tests.csproj
dotnet test tests/VideoAbridge.FFmpeg.Tests/VideoAbridge.FFmpeg.Tests.csproj
```

## README screenshot

The hero image is captured automatically on **Windows** before `git push` (markers + one deleted section on a generated sample clip).

```powershell
pwsh tools/install-hooks.ps1          # once per clone: core.hooksPath=.githooks
pwsh tools/capture-readme-screenshot.ps1   # manual refresh
```

If the PNG changes, the pre-push hook asks you to commit it and push again. Skip with `SKIP_README_SCREENSHOT=1 git push`.

## Releases

Notable changes live in [CHANGELOG.md](CHANGELOG.md) ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/)). While working, append bullets under **Unreleased**. To publish, rename that section to a version (`## [x.y.z] - YYYY-MM-DD`) and push to `main`.

[.github/workflows/release.yml](.github/workflows/release.yml) reads the latest versioned changelog entry; if that GitHub Release does not exist yet, it runs tests, publishes a self-contained `win-x64` build, and creates the Release (`v` + version) with a zip asset. FFmpeg/ffprobe are not bundled—install them separately (see Requirements).

## Typical workflow

1. **File → Open Video** (or **Recent Files**) — probes duration, restores cached versions/edits if present, resumes scene detection when needed.
2. Add markers (manually and/or **Detect Scenes**), select boundaries, **Delete** / **Restore Section** (or from start / to end).
3. Click a **Cut** / **XF** under a deleted interior run to set hard cut vs crossfade; set start/end fades in the side panel (`0` = off).
4. Toggle **Preview** to scrub the abridged result; use **View → Zoom to abridged content / Zoom to fit** as needed.
5. Optionally create more versions (**Version** menu) for alternate short cuts of the same file.
6. **File → Export…** writes the active version’s abridged MP4.

## Solution layout

- `VideoAbridge.Core` — immutable timeline / versions / preview / export-plan domain (`Result<T>` monad)
- `VideoAbridge.FFmpeg` — probe, scene detect, marker thumbnails, export
- `VideoAbridge` — WPF UI, LibVLC playback, project/marker cache
- `VideoAbridge.Core.Tests` / `VideoAbridge.Tests` / `VideoAbridge.FFmpeg.Tests` — unit tests
