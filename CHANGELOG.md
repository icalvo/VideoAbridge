# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.5...HEAD
[2026.11.5]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.4...v2026.11.5
[2026.11.4]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.3...v2026.11.4
[2026.11.3]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.2...v2026.11.3
[2026.11.2]: https://github.com/icalvo/VideoAbridge/compare/v2026.11.1...v2026.11.2
[2026.11.1]: https://github.com/icalvo/VideoAbridge/releases/tag/v2026.11.1
