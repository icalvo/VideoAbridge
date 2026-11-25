# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Marker labels: edit-mode timeline shows left-aligned labels in a top band (marker lines stay below it; width capped by the next labeled marker); sidebar field when one marker is selected; Enter focuses it; persisted with the marker cache.
- Ctrl+Left/Right nudges the selection by the sticky move mode (1 frame / 1s / 10s): selected manual markers, or a cut’s delimiting manual markers. Disabled for Marker and Cut modes; warns with the reason when the nudge is blocked.

### Changed

- Playhead movement uses sticky modes: `5`–`9` select Marker / Cut / 1 frame / 1s / 10s; Left/Right move (seek-and-pause). Shift+7/8/9 and Shift+Left/Right removed; mode label overlays the timeline pane (`Move: Marker`, etc.).
- Esc in sidebar duration fields (and version rename) discards the edit and returns focus to the timeline.
- Edit timeline: `S` is Shift-click (extend selection) under the playhead; `Shift+S` is click (replace selection).

### Fixed

- Preview Mode: dual bitmap compositor (two layers with per-frame opacities) drives crossfades and hard cuts—no HWND seek across the deleted gap—so dissolves scrub/play without flashing the cut frame; blackout/crossfade opacities update every source frame.
- Timeline pane chrome uses one background: top space is the marker-label band; surplus under the track is the overlay scrollbar band (no darker strips).
- Edit sidebar selection frames no longer flicker when the selection changes: previous frames stay until the new ones are ready, then replace in one update.

## [2026.11.9] - 2026-11-24

### Added

- Drop Section / Restore Section work with no markers selected: they target the section between the previous and next boundaries around the playhead (playhead must not sit on a marker); the red focus triangle sits at that section’s visible center.
- Edit timeline: Enter focuses the crossfade field when a cut is selected; Shift+Enter focuses the start-fade field.
- Preview timeline: Left/Right jump to 2 seconds before the previous/next cut or non-zero fade range (capped at the start), seek-and-pause.
- Edit sidebar Selection strip: on-demand left/right frames for 1 marker, 2 markers, or a selected cut; click a tile to seek there.

### Changed

- Playhead nudge (`7` / `8` / `9` and Shift variants) scrolls the playhead into view, matching marker/cut jump keys.
- Timeline labels and focus marks placed at a span’s center (cut labels, keep-duration labels, section/cut focus triangle) use the center of the **visible** portion of that span when it is partially off-screen.
- Section removal actions are named **Drop Section** (and from Start / to End) instead of Delete Section, so they do not share a verb with Delete Marker.
- Edit timeline keys are playhead-driven: Left/Right jump to markers (seek-and-pause); Shift+Left/Right jump to cut centers; S/Shift+S select the item under the playhead (red triangle); Space plays/pauses (P still works); Up/Down removed; 2/3 zoom on the playhead.
- Keyboard shortcuts dialog: dropped the redundant in-body title; section headers (File, Edit, etc.) use that title's font style; timeline keys section is titled **Timeline navigation**.
- README hero sample clip is composed from CC0 stills (see `tools/readme-sample/`) instead of SMPTE color bars.

## [2026.11.8] - 2026-11-23

### Added

- Edit → Move Marker to Playhead (`Ctrl+M`): with 1–2 markers selected, moves the nearest manual marker to the playhead when playback is stopped and the move stays legal (including existing crossfade / edge-fade caps).
- File Explorer **Abridge...** context menu for video files (Chocolatey registers it by default; `/NoExplorerContextMenu` skips). Help → Add/Remove File Explorer context menu toggles it for the current user.
- Chocolatey package `videoabridge` (downloads the GitHub Release zip; depends on `ffmpeg`); publish separately via Release `workflow_dispatch` **chocolatey_only_version** (`CHOCOLATEY_API_KEY`). Normal GitHub Releases no longer push Chocolatey.
- MIT `LICENSE`.
- Edit mode: darker green shading on kept sections that participate in a crossfade or edge fade.

### Changed

- Timeline mouse-wheel zoom steps more gently (about 5% per notch instead of 10%).
- Timeline waveform is cached under LocalAppData so reopening the same video skips FFmpeg peak extraction.
- Timeline waveform normalizes to the 99th-percentile peak (isolated spikes clamp) so quiet material stays visible.
- Keep-segment duration labels under the edit timeline cover start→first marker and last marker→end (as well as between markers), and hide when the gap is too narrow to fit the text.
- Timeline horizontal scrollbar overlays the timeline (no viewport shrink / zoom jump when it appears). Scrollbars and text boxes use dark styling to match the app.

## [2026.11.7] - 2026-11-21

### Added

- Help → Keyboard shortcuts modal (also `?`); Esc closes. Shortcuts listed in two compact columns by category.
- Playhead nudge: `7` / `8` / `9` forward by 1 frame / 1s / 10s; `Shift+7` / `8` / `9` backward (frame step from probed FPS; disabled while playing).
- `2` / `3` zoom in / out around the focused marker or cut (playhead if none).
- Version → Delete Version (disabled when only one version remains); Esc closes the task log window.
- View → Play / Pause; View zoom-in/out-on-focus menu items.

### Changed

- Toggle Edit / Preview shortcut is `E` (Tab no longer toggles mode).
- Zoom to abridged / fit is `1` / `0` (was Ctrl+1 / Ctrl+0); Tasks pane is `Ctrl+T` (was Ctrl+Shift+T).
- Menu AccessKeys filled in for Edit / View / Version items.
- Version tab `+` opens the same name dialog as Ctrl+N / New Version (no auto-invented name).

## [2026.11.6] - 2026-11-20

### Added

- Edit-mode timeline keyboard focus: Left/Right within markers or deleted sections, Up/Down to the other lane (nearest in time), Space/Shift+Space as click/Shift-click; red focus triangle (bright when timeline focused, dim when blurred); timeline scrolls just enough to keep the triangle in view when scroll room allows (may clip at the true ends).
- Ctrl+Tab / Ctrl+Shift+Tab cycle to the next / previous version (wraps).

### Changed

- Core domain validation (`Timeline` / `AbridgedVersionSet` factories, `FileContentId.Compute`) returns `Result` instead of throwing (null-argument checks unchanged).
- Preview side panel lists start/end fade controls above the selected Cut/XF crossfade controls.
- Marker click selection: plain click replaces a single selection; with two selected, Shift+click on another marker always swaps out the nearest (including markers between them).
- Keyboard shortcuts: Export Ctrl+S; Delete / Shift+Delete for delete/restore section (Delete still deletes a single marker); Detect Scenes Ctrl+D; Ctrl+Home/End delete from start/to end; Ctrl+Shift+Home/End restore from start/to end; Play/Pause is P (Space activates timeline focus in Edit).

## [2026.11.5] - 2026-11-20

### Added

- File → Recent Files submenu listing recently opened videos (persisted locally; missing entries drop on reopen).
- Main window title shows the opened video file name (`VideoAbridge - FILENAME`).
- Version tab bar (with a `+` tab) while a video is open; drag tabs to reorder; double-click or F2 renames inline on the tab.

### Changed

- Version → Rename Version uses the inline tab editor instead of a dialog.
- Active version tab is bold and flush with the content area (no separator under the tab bar).
- Adding a marker clears the current selection and selects the new marker.
- Timeline waveform is peak-normalized and drawn at 80% of the keep-bar height.
- Edit timeline shows grey `m:ss` duration labels between consecutive markers when the span is kept.
- Selecting a marker moves the playhead to that marker.

## [2026.11.4] - 2026-11-19

### Added

- Timeline audio waveform overlay in Edit and Preview (peak envelope from FFmpeg, fades/crossfades reflected in Preview).
- Waveform extraction as a cancelable long task with progress.
- Tooltips on disabled commands explaining why the action is unavailable.
- Keyboard shortcuts for common file, edit, view, version, and tasks actions (see README).
- Go to Beginning action (Home) to move the playhead to the start of the Edit or Preview timeline.
- Restore Section from Start / to End actions; section commands rename Undelete→Restore and Beginning→Start, with CanExecute when the range would actually change.
- Per-video cache restores Edit/Preview mode, per-mode zoom/scroll/playhead, Tasks pane open/closed, and main window placement (active version was already cached).
- Edit↔Preview no longer force-resets zoom/scroll/playhead when the abridged duration is unchanged; each mode remembers its own view.
- Long-task `...` control opens a live log window; export, scene detection, and waveform extraction stream ffmpeg stderr into it.

### Fixed

- Export/scene/waveform progress no longer stalls: stderr is drained on ffmpeg `\r` progress updates as well as newlines.

### Changed

- Tasks pane opens automatically for user-started jobs (export, manual scene detect); stays collapsed for background waveform extraction and auto scene detect on open. Collapsed state is a thin strip labeled `Tasks (N)`.
- Delete Marker, Export, and entering Preview are disabled when invalid instead of showing warning dialogs.
- GitHub Releases are driven by the latest versioned changelog entry (no `[release]` commit label or calendar/build version tags).

## [2026.11.3] - 2026-11-18

### Fixed

- README screenshot capture now uses the window’s extended frame bounds so desktop chrome outside the app is not included.

## [2026.11.2] - 2026-11-18

### Added

- Automated README hero screenshot capture (FlaUI + sample clip with seeded markers/cuts) and a pre-push hook to keep `docs/readme-screenshot.png` current.

## [2026.11.1] - 2026-11-18

### Added

- Initial GitHub Release of VideoAbridge: open a single finished video, mark and delete sections, scene detection, multiple abridged versions, Edit/Preview modes, joins/fades, undo/redo, marker cache, and FFmpeg export.
- CI release workflow publishing a self-contained Windows x64 build zip.

[Unreleased]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.9...HEAD
[2026.11.9]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.8...v2026.11.9
[2026.11.8]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.7...v2026.11.8
[2026.11.7]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.6...v2026.11.7
[2026.11.6]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.5...v2026.11.6
[2026.11.5]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.4...v2026.11.5
[2026.11.4]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.3...v2026.11.4
[2026.11.3]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.2...v2026.11.3
[2026.11.2]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.1...v2026.11.2
[2026.11.1]: https://github.com/icalvo/VideoAbridge/releases/tag/v2026.11.1
