# VideoAbridge

[![Chocolatey](https://img.shields.io/chocolatey/v/videoabridge)](https://community.chocolatey.org/packages/videoabridge)

![VideoAbridge](docs/readme-screenshot.png)

**Mission:** make it easy to produce short versions of a **single, already edited** video.

VideoAbridge is not a general [NLE](https://en.wikipedia.org/wiki/Non-linear_editing) tool and not a multi-clip editor. You open one finished cut, mark what to drop, and export a tighter cut—with optional alternate abridgements of the same source—without rebuilding the timeline from scratch.

## Features

### Editing the abridgement

- **Markers** — place user markers at the playhead; select one or more to define section boundaries
- **Drop / restore sections** — remove the range between selected markers (or the section under the playhead when none are selected), or restore it; also drop from the start or through the end of the video
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

Open **Help → Keyboard shortcuts** (or press `?`) for a categorized list. Shortcuts are ignored while typing in a text box.

| Shortcut              | Action                                    |
| --------------------- | ----------------------------------------- |
| Ctrl+O                | Open Video                                |
| Ctrl+S                | Export                                    |
| Space / P             | Play / Pause                              |
| Home                  | Go to Beginning                           |
| 5 / 6 / 7 / 8 / 9     | Move mode: Marker / Cut / 1 frame / 1s / 10s |
| Left / Right          | Move playhead by the sticky mode          |
| Ctrl+Left / Right     | Nudge selection by move mode (not Marker/Cut) |
| Ctrl+Z / Ctrl+Y       | Undo / Redo                               |
| M                     | Add Marker                                |
| Delete                | Delete Marker / Drop Section              |
| Shift+Delete          | Restore Section                           |
| Ctrl+Home / End       | Drop Section from Start / to End          |
| Ctrl+Shift+Home / End | Restore Section from Start / to End       |
| Ctrl+D                | Detect Scenes                             |
| E                     | Toggle Edit / Preview                     |
| S / Shift+S           | Shift-click / Click item under playhead (Edit)|
| Enter                 | Edit crossfade (selected cut, Edit)       |
| Shift+Enter           | Edit start fade (Edit)                    |
| 2 / 3                 | Zoom in / out on playhead                 |
| 1 / 0                 | Zoom to abridged content / Zoom to fit    |
| Ctrl+N / Ctrl+Shift+N | New / Duplicate Version                   |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / Previous Version               |
| F2                    | Rename Version (inline on the active tab) |
| Ctrl+T                | Expand / collapse Tasks pane              |

## Requirements

- Windows (LibVLC via `VideoLAN.LibVLC.Windows`)
- FFmpeg and ffprobe on `PATH` (Chocolatey package pulls in `ffmpeg`; for local/dev builds place `ffmpeg.exe` / `ffprobe.exe` under `tools/ffmpeg/` or on `PATH`)
- .NET 11 SDK (to build from source)

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

The hero image is captured automatically on **Windows** before `git push` (markers + one deleted section on a sample clip composed from [CC0 stills](tools/readme-sample/ATTRIBUTION.md)).

```powershell
pwsh tools/install-hooks.ps1          # once per clone: core.hooksPath=.githooks
pwsh tools/capture-readme-screenshot.ps1   # manual refresh
```

If the PNG changes, the pre-push hook asks you to commit it and push again. Skip with `SKIP_README_SCREENSHOT=1 git push`.

## Install (Windows)

```bash
choco install videoabridge
```

Requires [Chocolatey](https://chocolatey.org/). The package depends on the Chocolatey `ffmpeg` package.

## Releases

Notable changes live in [CHANGELOG.md](CHANGELOG.md) ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/)). While working, append bullets under **Unreleased**. To publish, rename that section to a version (`## [x.y.z] - YYYY-MM-DD`) and push to `main`.

[.github/workflows/release.yml](.github/workflows/release.yml) reads the latest versioned changelog entry; if that GitHub Release does not exist yet, it runs tests, publishes a self-contained `win-x64` build, and creates the Release (`v` + version) with a zip asset. Normal releases do **not** push Chocolatey.

To publish the [Chocolatey](https://community.chocolatey.org/) package `videoabridge` for an existing GitHub Release, run the Release workflow manually (`workflow_dispatch`) and set **chocolatey_only_version** (e.g. `2026.11.8`). That path needs repo secret `CHOCOLATEY_API_KEY`. The first Chocolatey submission is moderated; later versions publish automatically after that.

Package sources live under [`packaging/chocolatey/`](packaging/chocolatey/).

## Typical workflow

1. **File → Open Video** (or **Recent Files**) — probes duration, restores cached versions/edits if present, resumes scene detection when needed.
2. Add markers (manually and/or **Detect Scenes**), select boundaries, **Drop** / **Restore Section** (or from start / to end).
3. Click a **Cut** / **XF** under a deleted interior run to set hard cut vs crossfade; set start/end fades in the side panel (`0` = off).
4. Toggle **Preview** to scrub the abridged result; use **View → Zoom to abridged content / Zoom to fit** as needed.
5. Optionally create more versions (**Version** menu) for alternate short cuts of the same file.
6. **File → Export…** writes the active version’s abridged MP4.

## Solution layout

- `VideoAbridge.Core` — immutable timeline / versions / preview / export-plan domain (`Result<T>` monad)
- `VideoAbridge.FFmpeg` — probe, scene detect, marker thumbnails, export
- `VideoAbridge` — WPF UI, LibVLC playback, project/marker cache
- `VideoAbridge.Core.Tests` / `VideoAbridge.Tests` / `VideoAbridge.FFmpeg.Tests` — unit tests
