using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoAbridge.FFmpeg;
using VideoAbridge.Services;
using NamePromptDialog = VideoAbridge.NamePromptDialog;

namespace VideoAbridge.ViewModels;

internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly VideoPlayerService _player = new();
    private readonly MediaProbe _probe = new();
    private readonly SceneDetector _sceneDetector = new();
    private readonly ThumbnailExtractor _thumbnails = new();
    private readonly VideoExporter _exporter = new();
    private readonly MarkerCacheService _markerCache = CreateMarkerCache();
    private readonly EditHistory _editHistory = new();
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _sceneDetectCts;
    private bool _disposed;
    private int _thumbnailRefreshSuspendCount;
    private AbridgedVersionSet? _versionSet;
    private DateTime _lastCachePersistUtc;
    private CancellationTokenSource? _thumbnailCts;
    private FrameVideoPlayer? _secondaryPlayer;
    private ExportPlan? _previewPlan;
    private PreviewTimeMap? _previewMap;
    private AbridgedTimelineLayout? _abridgedLayout;
    private TimeSpan? _seekGateTarget;
    private DateTime _seekGateUntilUtc;
    private bool _inCrossfade;

    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private Timeline? _timeline;
    [ObservableProperty] private TimeSpan _playhead;
    [ObservableProperty] private TimeSpan _abridgedPlayhead;
    [ObservableProperty] private string _statusBarText = "Open a video to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private Guid? _selectedDeletedRunId;
    [ObservableProperty] private double _selectedCrossfadeSeconds;
    [ObservableProperty] private double _selectedCrossfadeCap;
    [ObservableProperty] private double _fadeFromBlackSeconds;
    [ObservableProperty] private double _startFadeCap;
    [ObservableProperty] private double _fadeToBlackSeconds;
    [ObservableProperty] private double _endFadeCap;
    private bool _syncingSidebarUi;
    [ObservableProperty] private bool _isPreviewMode;
    [ObservableProperty] private double _previewBlackoutOpacity;
    [ObservableProperty] private double _previewSecondaryOpacity;
    [ObservableProperty] private ImageSource? _previewSecondaryFrame;

    public string PreviewModeToggleLabel => IsPreviewMode ? "Edit" : "Preview";

    public bool ShowEditChrome => !IsPreviewMode;

    public bool HasSelectedJoin =>
        SelectedDeletedRunId is Guid id
        && Timeline?.GetDeletedRuns().FirstOrDefault(r => r.Id == id) is { IsInterior: true, Join: not null };

    public AbridgedTimelineLayout? AbridgedLayout => _abridgedLayout;

    /// <summary>Timeline needle: abridged clock in Preview, source clock in Edit.</summary>
    public TimeSpan DisplayPlayhead => IsPreviewMode ? AbridgedPlayhead : Playhead;

    [ObservableProperty] private double _minPixelsPerSecond = 4;
    [ObservableProperty] private double _maxPixelsPerSecond = 400;
    [ObservableProperty] private double _pixelsPerSecond = 40;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isDetectingScenes;

    public VideoPlayerService Player => _player;

    public ObservableCollection<MarkerThumbViewModel> MarkerThumbs { get; } = [];
    public ObservableCollection<Guid> SelectedMarkerIds { get; } = [];
    public ObservableCollection<LongTaskItem> Tasks { get; } = [];
    public ObservableCollection<VersionTabViewModel> VersionTabs { get; } = [];

    [ObservableProperty] private bool _isTasksPaneOpen;

    public bool ShowTasksPaneExpandButton => !IsTasksPaneOpen && HasTasksWorthShowing();

    public bool ShowVersionTabs => VersionTabs.Count > 1;

    public bool HasVideoLoaded => VideoPath is not null && _versionSet is not null;

    public MainViewModel()
    {
        _player.TimeChanged += (_, time) =>
        {
            // BeginInvoke (not Invoke): LibVLC callbacks + UI-thread Seek otherwise deadlock.
            Application.Current?.Dispatcher.BeginInvoke(() => HandlePlayerTime(time));
        };
        _player.EndReached += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!IsPlaying)
                    return;
                _player.Pause();
                IsPlaying = false;
            });
        };
        SelectedMarkerIds.CollectionChanged += OnSelectedMarkerIdsChanged;
        RefreshStatusBar();
    }

    private void OnSelectedMarkerIdsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyTimelineActionCommands();
        RefreshStatusBar();
    }

    partial void OnPlayheadChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(DisplayPlayhead));
        NotifyTimelineActionCommands();
    }

    partial void OnAbridgedPlayheadChanged(TimeSpan value) => OnPropertyChanged(nameof(DisplayPlayhead));

    partial void OnIsDetectingScenesChanged(bool value)
    {
        NotifyTimelineActionCommands();
        NotifyVersionCommands();
    }

    private void NotifyTimelineActionCommands()
    {
        AddMarkerAtPlayheadCommand.NotifyCanExecuteChanged();
        DeleteMarkerCommand.NotifyCanExecuteChanged();
        DeleteSectionCommand.NotifyCanExecuteChanged();
        UndeleteSectionCommand.NotifyCanExecuteChanged();
        DeleteSectionFromBeginningCommand.NotifyCanExecuteChanged();
        DeleteSectionToEndCommand.NotifyCanExecuteChanged();
        DetectScenesCommand.NotifyCanExecuteChanged();
        NotifyUndoRedoCommands();
    }

    private void NotifyUndoRedoCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    partial void OnTimelineChanged(Timeline? value)
    {
        if (value is null)
        {
            StartFadeCap = 0;
            EndFadeCap = 0;
            RefreshStatusBar();
            return;
        }

        _syncingSidebarUi = true;
        try
        {
            FadeFromBlackSeconds = value.Settings.FadeFromBlackSeconds ?? 0;
            StartFadeCap = value.GetStartFadeCap();
            FadeToBlackSeconds = value.Settings.FadeToBlackSeconds ?? 0;
            EndFadeCap = value.GetEndFadeCap();
        }
        finally
        {
            _syncingSidebarUi = false;
        }

        if (IsPreviewMode)
            RefreshRealtimePreviewModel(seekToPlayhead: true);

        if (_thumbnailRefreshSuspendCount == 0)
            _ = RefreshThumbnailsAsync();
        RefreshSelectionFromTimeline();
        RefreshStatusBar();
    }

    partial void OnIsPreviewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewModeToggleLabel));
        OnPropertyChanged(nameof(ShowEditChrome));
        OnPropertyChanged(nameof(DisplayPlayhead));
        if (value)
            EnterRealtimePreview();
        else
            ExitRealtimePreview();
        RefreshStatusBar();
    }

    partial void OnSelectedDeletedRunIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(HasSelectedJoin));
        UndeleteSectionCommand.NotifyCanExecuteChanged();
        RefreshSelectionFromTimeline();
        RefreshStatusBar();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Timeline is null || !_editHistory.TryUndo(Timeline, out var previous))
            return;

        SetTimelineFromHistory(previous);
    }

    private bool CanUndo() =>
        Timeline is not null && _editHistory.CanUndo && !IsDetectingScenes;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (Timeline is null || !_editHistory.TryRedo(Timeline, out var next))
            return;

        SetTimelineFromHistory(next);
    }

    private bool CanRedo() =>
        Timeline is not null && _editHistory.CanRedo && !IsDetectingScenes;

    [RelayCommand]
    private void ExpandTasksPane() => IsTasksPaneOpen = true;

    [RelayCommand]
    private void CollapseTasksPane() => IsTasksPaneOpen = false;

    partial void OnIsTasksPaneOpenChanged(bool value) =>
        OnPropertyChanged(nameof(ShowTasksPaneExpandButton));

    [RelayCommand]
    private async Task OpenVideoAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|All files|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        await LoadVideoAsync(dialog.FileName, runSceneDetect: true).ConfigureAwait(true);
    }

    public Task OpenVideoPathAsync(string path) =>
        LoadVideoAsync(path, runSceneDetect: !IsSkipSceneDetectEnabled());

    private static MarkerCacheService CreateMarkerCache()
    {
        var dir = Environment.GetEnvironmentVariable("VIDEOABRIDGE_MARKER_CACHE");
        return string.IsNullOrWhiteSpace(dir)
            ? new MarkerCacheService()
            : new MarkerCacheService(dir);
    }

    private static bool IsSkipSceneDetectEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("VIDEOABRIDGE_SKIP_SCENE_DETECT"),
            "1",
            StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanManageVersions))]
    private void CreateVersion()
    {
        if (_versionSet is null)
            return;

        if (!TryPromptVersionName("New Version", "Name for the new version:", suggested: "", out var name))
            return;

        ApplyVersionResult(_versionSet.CreateVersionFromScenes(name));
    }

    [RelayCommand(CanExecute = nameof(CanManageVersions))]
    private void DuplicateVersion()
    {
        if (_versionSet is null)
            return;

        var suggested = SuggestCopyName(_versionSet.Active.Name);
        if (!TryPromptVersionName("Duplicate Version", "Name for the duplicated version:", suggested, out var name))
            return;

        ApplyVersionResult(_versionSet.DuplicateActive(name));
    }

    [RelayCommand(CanExecute = nameof(CanManageVersions))]
    private void RenameVersion()
    {
        if (_versionSet is null)
            return;

        if (!TryPromptVersionName(
                "Rename Version",
                "New name:",
                suggested: _versionSet.Active.Name,
                out var name))
            return;

        _versionSet.RenameActive(name).Match(
            next =>
            {
                _versionSet = next;
                SyncVersionTabs();
                PersistMarkerCache(force: true);
                RefreshStatusBar();
            },
            error => ReportError(error.Message));
    }

    [RelayCommand(CanExecute = nameof(CanSelectVersion))]
    private void SelectVersion(Guid versionId)
    {
        if (_versionSet is null || versionId == _versionSet.ActiveVersionId)
            return;

        PersistMarkerCache(force: true);
        ApplyVersionResult(_versionSet.SetActive(versionId));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteVersion))]
    private void DeleteVersion(Guid versionId)
    {
        if (_versionSet is null)
            return;

        var version = _versionSet.Versions.FirstOrDefault(v => v.Id == versionId);
        if (version is null)
            return;

        var confirm = MessageBox.Show(
            $"Delete version \"{version.Name}\"?",
            "Delete Version",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        ApplyVersionResult(_versionSet.Remove(versionId));
    }

    private bool CanManageVersions() => HasVideoLoaded && !IsDetectingScenes;

    private bool CanSelectVersion() => HasVideoLoaded && !IsDetectingScenes;

    private bool CanDeleteVersion() =>
        HasVideoLoaded && !IsDetectingScenes && _versionSet is { Versions.Count: > 1 };

    private static bool TryPromptVersionName(string title, string prompt, string suggested, out string name)
    {
        name = "";
        var dialog = new NamePromptDialog(title, prompt, suggested)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return false;

        name = dialog.ResultName;
        return true;
    }

    private string SuggestCopyName(string baseName)
    {
        if (_versionSet is null)
            return baseName + " Copy";

        var candidate = baseName + " Copy";
        var suffix = 2;
        while (_versionSet.Versions.Any(v =>
                   string.Equals(v.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} Copy {suffix}";
            suffix++;
        }

        return candidate;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (Timeline is null)
            return;

        // Prefer our flag: LibVLC IsPlaying can stay true after the first-frame pause.
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            return;
        }

        if (IsPreviewMode)
        {
            if (_previewPlan is null || _previewMap is null)
                return;

            var abridged = _previewMap.ToAbridged(Playhead);
            if (_previewMap.AbridgedDuration > TimeSpan.Zero
                && abridged >= _previewMap.AbridgedDuration - Timeline.MarkerEpsilon)
            {
                var kept = Timeline.GetKeptRuns();
                var firstKept = kept.Count > 0 ? kept[0].Start : TimeSpan.Zero;
                SeekPlayhead(firstKept);
            }
            else if (!Timeline.IsKeptTime(Playhead))
            {
                SeekPlayhead(Timeline.SnapToNearestKept(Playhead));
            }
        }

        _player.Play();
        IsPlaying = true;
    }

    [RelayCommand]
    private void TogglePreviewMode() => IsPreviewMode = !IsPreviewMode;

    [RelayCommand(CanExecute = nameof(CanAddMarkerAtPlayhead))]
    private void AddMarkerAtPlayhead()
    {
        if (Timeline is null)
            return;

        Apply(Timeline.AddMarker(Playhead, MarkerSource.User));
    }

    private bool CanAddMarkerAtPlayhead() =>
        Timeline is not null
        && Playhead > TimeSpan.Zero
        && Playhead < Timeline.Duration
        && !Timeline.Markers.Any(m => AbsDiff(m.Time, Playhead) <= Timeline.MarkerEpsilon);

    public void ToggleMarkerSelection(Guid markerId)
    {
        SelectedDeletedRunId = null;

        if (SelectedMarkerIds.Remove(markerId))
            return;

        if (SelectedMarkerIds.Count >= 2)
        {
            if (Timeline is null)
                return;

            var clicked = Timeline.Markers.FirstOrDefault(m => m.Id == markerId);
            var selected = SelectedMarkerIds
                .Select(id => Timeline.Markers.FirstOrDefault(m => m.Id == id))
                .OfType<Marker>()
                .OrderBy(m => m.Time)
                .ToList();

            if (clicked is null || selected.Count != 2)
            {
                SelectedMarkerIds.Clear();
                SelectedMarkerIds.Add(markerId);
                return;
            }

            var left = selected[0];
            var right = selected[1];

            if (clicked.Time > left.Time && clicked.Time < right.Time)
            {
                // Between the two: keep only the clicked marker.
                SelectedMarkerIds.Clear();
                SelectedMarkerIds.Add(markerId);
                return;
            }

            // Outside: drop the nearest selected marker, keep the far one, add clicked.
            var nearestId = AbsDiff(clicked.Time, left.Time) <= AbsDiff(clicked.Time, right.Time)
                ? left.Id
                : right.Id;
            SelectedMarkerIds.Remove(nearestId);
            SelectedMarkerIds.Add(markerId);
            return;
        }

        SelectedMarkerIds.Add(markerId);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteMarker))]
    private void DeleteMarker()
    {
        if (Timeline is null || SelectedMarkerIds.Count != 1)
            return;

        var id = SelectedMarkerIds[0];
        Apply(Timeline.RemoveMarker(id));
        SelectedMarkerIds.Remove(id);
    }

    private bool CanDeleteMarker()
    {
        if (Timeline is null || SelectedMarkerIds.Count != 1)
            return false;

        var marker = Timeline.Markers.FirstOrDefault(m => m.Id == SelectedMarkerIds[0]);
        return marker is not null && marker.Source != MarkerSource.SceneDetect;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSection))]
    private void DeleteSection()
    {
        if (Timeline is null || !TryGetOrderedSelectedMarkerIds(out var left, out var right))
            return;

        Apply(Timeline.DeleteBetween(left, right));
    }

    private bool CanDeleteSection() => Timeline is not null && SelectedMarkerIds.Count == 2;

    [RelayCommand(CanExecute = nameof(CanUndeleteSection))]
    private void UndeleteSection()
    {
        if (Timeline is null)
            return;

        if (SelectedDeletedRunId is Guid runId)
        {
            Apply(Timeline.RestoreDeletedRun(runId));
            SelectedDeletedRunId = null;
            return;
        }

        if (!TryGetOrderedSelectedMarkerIds(out var left, out var right))
            return;

        Apply(Timeline.RestoreBetween(left, right));
    }

    private bool CanUndeleteSection() =>
        Timeline is not null
        && (SelectedDeletedRunId is not null || SelectedMarkerIds.Count == 2);

    [RelayCommand(CanExecute = nameof(CanDeleteSectionFromBeginning))]
    private void DeleteSectionFromBeginning()
    {
        if (Timeline is null || !TryGetLeftmostSelectedMarkerId(out var left))
            return;

        Apply(Timeline.DeleteBetween(Timeline.StartBoundId, left));
    }

    private bool CanDeleteSectionFromBeginning() =>
        Timeline is not null && SelectedMarkerIds.Count is 1 or 2;

    [RelayCommand(CanExecute = nameof(CanDeleteSectionToEnd))]
    private void DeleteSectionToEnd()
    {
        if (Timeline is null || !TryGetRightmostSelectedMarkerId(out var right))
            return;

        Apply(Timeline.DeleteBetween(right, Timeline.EndBoundId));
    }

    private bool CanDeleteSectionToEnd() =>
        Timeline is not null && SelectedMarkerIds.Count is 1 or 2;

    private bool TryGetOrderedSelectedMarkerIds(out Guid left, out Guid right)
    {
        left = default;
        right = default;
        if (Timeline is null || SelectedMarkerIds.Count != 2)
            return false;

        var markers = SelectedMarkerIds
            .Select(id => Timeline.Markers.FirstOrDefault(m => m.Id == id))
            .Where(m => m is not null)
            .Cast<Marker>()
            .OrderBy(m => m.Time)
            .ToList();
        if (markers.Count != 2)
            return false;

        left = markers[0].Id;
        right = markers[1].Id;
        return true;
    }

    private bool TryGetLeftmostSelectedMarkerId(out Guid id)
    {
        id = default;
        if (Timeline is null || SelectedMarkerIds.Count == 0)
            return false;

        var marker = SelectedMarkerIds
            .Select(x => Timeline.Markers.FirstOrDefault(m => m.Id == x))
            .Where(m => m is not null)
            .Cast<Marker>()
            .OrderBy(m => m.Time)
            .FirstOrDefault();
        if (marker is null)
            return false;
        id = marker.Id;
        return true;
    }

    private bool TryGetRightmostSelectedMarkerId(out Guid id)
    {
        id = default;
        if (Timeline is null || SelectedMarkerIds.Count == 0)
            return false;

        var marker = SelectedMarkerIds
            .Select(x => Timeline.Markers.FirstOrDefault(m => m.Id == x))
            .Where(m => m is not null)
            .Cast<Marker>()
            .OrderByDescending(m => m.Time)
            .FirstOrDefault();
        if (marker is null)
            return false;
        id = marker.Id;
        return true;
    }

    private static TimeSpan AbsDiff(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;

    [RelayCommand]
    private void SelectDeletedRun(Guid runId)
    {
        SelectedDeletedRunId = runId;
        SelectedMarkerIds.Clear();
        RefreshSelectionFromTimeline();
    }

    partial void OnSelectedCrossfadeSecondsChanged(double value) => CommitJoinCrossfade(value);

    partial void OnFadeFromBlackSecondsChanged(double value) => CommitStartFade(value);

    partial void OnFadeToBlackSecondsChanged(double value) => CommitEndFade(value);

    private void CommitJoinCrossfade(double value)
    {
        if (_syncingSidebarUi || Timeline is null || SelectedDeletedRunId is null)
            return;

        var seconds = Math.Max(0, value);
        var cap = SelectedCrossfadeCap;
        if (cap <= 0)
            seconds = 0;
        else if (seconds > cap)
            seconds = cap;

        if (Math.Abs(seconds - value) > 1e-9)
        {
            _syncingSidebarUi = true;
            try { SelectedCrossfadeSeconds = seconds; }
            finally { _syncingSidebarUi = false; }
        }

        var runId = SelectedDeletedRunId.Value;
        var join = Timeline.Joins.FirstOrDefault(j => j.DeletedRunId == runId);
        if (join is null)
            return;

        if (seconds <= 1e-9)
        {
            if (join.Mode == TransitionMode.HardCut && join.CrossfadeSeconds <= 1e-9)
                return;
            Apply(Timeline.SetJoinHardCut(runId));
            return;
        }

        if (join.Mode == TransitionMode.Crossfade
            && Math.Abs(join.CrossfadeSeconds - seconds) < 1e-9)
            return;

        Apply(Timeline.SetJoinCrossfade(runId, seconds));
    }

    private void CommitStartFade(double value)
    {
        if (_syncingSidebarUi || Timeline is null)
            return;

        var seconds = Math.Max(0, value);
        var cap = Timeline.GetStartFadeCap();
        if (seconds > cap)
            seconds = cap;

        if (Math.Abs(seconds - value) > 1e-9)
        {
            _syncingSidebarUi = true;
            try { FadeFromBlackSeconds = seconds; }
            finally { _syncingSidebarUi = false; }
        }

        double? fade = seconds > 1e-9 ? seconds : null;
        if (Timeline.Settings.FadeFromBlackSeconds == fade)
            return;

        Apply(Timeline.SetStartFade(fade));
    }

    private void CommitEndFade(double value)
    {
        if (_syncingSidebarUi || Timeline is null)
            return;

        var seconds = Math.Max(0, value);
        var cap = Timeline.GetEndFadeCap();
        if (seconds > cap)
            seconds = cap;

        if (Math.Abs(seconds - value) > 1e-9)
        {
            _syncingSidebarUi = true;
            try { FadeToBlackSeconds = seconds; }
            finally { _syncingSidebarUi = false; }
        }

        double? fade = seconds > 1e-9 ? seconds : null;
        if (Timeline.Settings.FadeToBlackSeconds == fade)
            return;

        Apply(Timeline.SetEndFade(fade));
    }

    [RelayCommand(CanExecute = nameof(CanDetectScenes))]
    private async Task DetectScenesAsync()
    {
        if (VideoPath is null || Timeline is null || IsDetectingScenes)
            return;

        await RunSceneDetectInBackgroundAsync(VideoPath, Timeline.Duration).ConfigureAwait(true);
    }

    private bool CanDetectScenes() =>
        VideoPath is not null && Timeline is not null && !IsDetectingScenes;

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Timeline is null || VideoPath is null)
            return;

        ExportPlan? plan = null;
        Timeline.TryBuildExportPlan().Match(
            value => plan = value,
            error => ReportError(error.Message));
        if (plan is null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileNameWithoutExtension(VideoPath) + "_abridged.mp4"
        };
        if (dialog.ShowDialog() != true)
            return;

        if (_exportCts is not null)
        {
            await _exportCts.CancelAsync().ConfigureAwait(true);
            _exportCts.Dispose();
        }

        _exportCts = new CancellationTokenSource();
        IsExporting = true;

        var outputPath = dialog.FileName;
        var task = CreateTask($"Export: {Path.GetFileName(outputPath)}");
        task.ResultPath = outputPath;
        task.PrimaryActionLabel = "Open";
        task.PrimaryActionCommand = new RelayCommand(() => OpenExportedFile(outputPath));
        task.CanCancel = true;
        task.CancelCommand = new AsyncRelayCommand(async () =>
        {
            if (_exportCts is not null)
                await _exportCts.CancelAsync().ConfigureAwait(true);
        });
        task.DismissCommand = new RelayCommand(() => DismissTask(task));

        try
        {
            var progress = new Progress<double>(p => task.Progress = p);
            await _exporter.ExportAsync(
                VideoPath,
                outputPath,
                plan,
                progress,
                cancellationToken: _exportCts.Token).ConfigureAwait(true);
            task.Progress = 1;
            task.IsCompleted = true;
            task.CanCancel = false;
            task.HasResultActions = true;
            task.Title = $"Exported: {Path.GetFileName(outputPath)}";
        }
        catch (OperationCanceledException)
        {
            RemoveTask(task);
        }
        catch (InvalidOperationException ex)
        {
            RemoveTask(task);
            ReportError(ex.Message);
        }
        catch (IOException ex)
        {
            RemoveTask(task);
            ReportError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            RemoveTask(task);
            ReportError(ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    public void SeekPlayhead(TimeSpan time)
    {
        if (Timeline is not null && IsPreviewMode)
            time = Timeline.SnapToNearestKept(time);

        Playhead = time;
        SyncAbridgedPlayheadFromSource(time);
        EndCrossfadeVisuals();
        GateSeek(time);
        _player.Seek(time);
        if (IsPreviewMode)
            ApplyPreviewVisuals(time);
    }

    /// <summary>Preview timeline seek: <paramref name="abridged"/> is export-clock time.</summary>
    public void SeekAbridgedPlayhead(TimeSpan abridged)
    {
        if (_previewMap is null || Timeline is null)
            return;

        if (abridged < TimeSpan.Zero)
            abridged = TimeSpan.Zero;
        else if (abridged > _previewMap.AbridgedDuration)
            abridged = _previewMap.AbridgedDuration;

        AbridgedPlayhead = abridged;
        var source = _previewMap.ToSource(abridged);
        SeekPlayhead(source);
    }

    private void SyncAbridgedPlayheadFromSource(TimeSpan sourceTime)
    {
        if (_previewMap is null)
            return;
        AbridgedPlayhead = _previewMap.ToAbridged(sourceTime);
    }

    private void HandlePlayerTime(TimeSpan time)
    {
        if (Timeline is null || !IsPreviewMode || _previewPlan is null || _previewMap is null)
        {
            Playhead = time;
            return;
        }

        var frameEps = TimeSpan.FromSeconds(PreviewVisualState.FrameEpsilonSeconds);

        if (_seekGateTarget is { } gate)
        {
            // Arrived near the jump target — release the gate and continue.
            if (AbsDiff(time, gate) <= frameEps * 2)
            {
                _seekGateTarget = null;
            }
            else if (DateTime.UtcNow < _seekGateUntilUtc)
            {
                // Keep the needle stable while LibVLC finishes the jump; do not re-seek.
                Playhead = gate;
                SyncAbridgedPlayheadFromSource(gate);
                return;
            }
            else
            {
                // Seek timed out and we're still not there — retry once rather than
                // falling through into another Evaluate→Jump loop on stale gap times.
                GateSeek(gate);
                Playhead = gate;
                SyncAbridgedPlayheadFromSource(gate);
                _player.Seek(gate);
                return;
            }
        }

        var state = PreviewVisualState.Evaluate(_previewPlan, _previewMap, time);
        Playhead = state.PlayheadSourceTime;
        SyncAbridgedPlayheadFromSource(state.PlayheadSourceTime);
        PreviewBlackoutOpacity = state.BlackoutOpacity;

        if (state.JumpPrimaryTo is { } jump)
        {
            // Already seeking to (approximately) this target — don't flood LibVLC.
            if (_seekGateTarget is { } active
                && AbsDiff(active, jump) <= frameEps)
            {
                Playhead = active;
                SyncAbridgedPlayheadFromSource(active);
                return;
            }

            EndCrossfadeVisuals();
            GateSeek(jump);
            Playhead = jump;
            SyncAbridgedPlayheadFromSource(jump);
            _player.Seek(jump);
            ApplyVolumes(state.BlackoutOpacity, incomingOpacity: 0);
            if (state.PreviewComplete && IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
            }

            return;
        }

        if (state.PreviewComplete)
        {
            EndCrossfadeVisuals();
            ApplyVolumes(state.BlackoutOpacity, 0);
            if (IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
            }

            return;
        }

        if (state.InCrossfade)
        {
            if (!_inCrossfade)
            {
                // Start the incoming clip once; let it run for the blend window.
                EnsureSecondaryAt(state.IncomingSourceTime, play: IsPlaying);
                _inCrossfade = true;
            }

            PreviewSecondaryOpacity = state.IncomingOpacity;
            ApplyVolumes(state.BlackoutOpacity, state.IncomingOpacity);
        }
        else
        {
            if (_inCrossfade)
                EndCrossfadeVisuals();
            ApplyVolumes(state.BlackoutOpacity, 0);
        }
    }

    private void EnterRealtimePreview()
    {
        if (Timeline is null || VideoPath is null)
        {
            IsPreviewMode = false;
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }

        // Ensure we are on the source file (not a leftover temp render).
        try
        {
            _player.Load(VideoPath);
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex.Message);
            IsPreviewMode = false;
            return;
        }
        catch (ArgumentException ex)
        {
            ReportError(ex.Message);
            IsPreviewMode = false;
            return;
        }
        catch (IOException ex)
        {
            ReportError(ex.Message);
            IsPreviewMode = false;
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportError(ex.Message);
            IsPreviewMode = false;
            return;
        }

        // Rebuild secondary against the current source path.
        _secondaryPlayer?.Dispose();
        _secondaryPlayer = null;
        PreviewSecondaryFrame = null;
        EnsureSecondaryPlayer();
        RefreshRealtimePreviewModel(seekToPlayhead: true);
    }

    private void ExitRealtimePreview()
    {
        EndCrossfadeVisuals();
        _previewPlan = null;
        _previewMap = null;
        _abridgedLayout = null;
        OnPropertyChanged(nameof(AbridgedLayout));
        _seekGateTarget = null;
        PreviewBlackoutOpacity = 0;
        PreviewSecondaryOpacity = 0;
        _player.SetVolume(100);
        RefreshStatusBar();
    }

    private void RefreshRealtimePreviewModel(bool seekToPlayhead)
    {
        if (!IsPreviewMode || Timeline is null)
            return;

        ExportPlan? plan = null;
        Timeline.TryBuildExportPlan().Match(
            value => plan = value,
            error => ReportError(error.Message));
        if (plan is null)
        {
            IsPreviewMode = false;
            return;
        }

        _previewPlan = plan;
        _previewMap = PreviewTimeMap.FromPlan(_previewPlan);
        _abridgedLayout = AbridgedTimelineLayout.FromTimeline(Timeline);
        OnPropertyChanged(nameof(AbridgedLayout));
        EndCrossfadeVisuals();

        var snapped = Timeline.SnapToNearestKept(Playhead);
        Playhead = snapped;
        SyncAbridgedPlayheadFromSource(snapped);
        if (seekToPlayhead)
        {
            GateSeek(snapped);
            _player.Seek(snapped);
        }

        ApplyPreviewVisuals(snapped);
    }

    private void ApplyPreviewVisuals(TimeSpan sourceTime)
    {
        if (_previewPlan is null || _previewMap is null)
            return;

        var state = PreviewVisualState.Evaluate(_previewPlan, _previewMap, sourceTime);
        PreviewBlackoutOpacity = state.BlackoutOpacity;
        PreviewSecondaryOpacity = 0;
        ApplyVolumes(state.BlackoutOpacity, 0);
    }

    private void EnsureSecondaryPlayer()
    {
        if (_secondaryPlayer is not null || VideoPath is null)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        _secondaryPlayer = new FrameVideoPlayer(dispatcher);
        _secondaryPlayer.FrameUpdated += (_, _) =>
        {
            PreviewSecondaryFrame = _secondaryPlayer.Bitmap;
        };
        try
        {
            _secondaryPlayer.Load(VideoPath);
            _secondaryPlayer.SetVolume(0);
        }
        catch (InvalidOperationException ex)
        {
            ReportError($"Preview secondary player failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            ReportError($"Preview secondary player failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            ReportError($"Preview secondary player failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportError($"Preview secondary player failed: {ex.Message}");
        }
        catch (COMException ex)
        {
            ReportError($"Preview secondary player failed: {ex.Message}");
        }
    }

    private void EnsureSecondaryAt(TimeSpan sourceTime, bool play)
    {
        EnsureSecondaryPlayer();
        if (_secondaryPlayer is null)
            return;

        PreviewSecondaryFrame ??= _secondaryPlayer.Bitmap;

        _secondaryPlayer.Seek(sourceTime);
        if (play)
            _secondaryPlayer.Play();
        else
            _secondaryPlayer.Pause();
    }

    private void EndCrossfadeVisuals()
    {
        _inCrossfade = false;
        PreviewSecondaryOpacity = 0;
        try
        {
            _secondaryPlayer?.Pause();
            _secondaryPlayer?.SetVolume(0);
        }
        catch (ObjectDisposedException)
        {
            // ignore teardown races
        }
        catch (InvalidOperationException)
        {
            // ignore teardown races
        }
        catch (COMException)
        {
            // ignore teardown races
        }
    }

    private void ApplyVolumes(double blackoutOpacity, double incomingOpacity)
    {
        var audible = Math.Clamp(1.0 - blackoutOpacity, 0, 1);
        var primary = (int)Math.Round(100 * audible * (1.0 - incomingOpacity));
        var secondary = (int)Math.Round(100 * audible * incomingOpacity);
        _player.SetVolume(primary);
        _secondaryPlayer?.SetVolume(secondary);
    }

    private void GateSeek(TimeSpan target)
    {
        _seekGateTarget = target;
        _seekGateUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
    }

    private void Apply(Result<Timeline> result) =>
        result.Match(
            timeline =>
            {
                if (Timeline is not null)
                    _editHistory.RecordBeforeChange(Timeline);
                SetActiveTimeline(timeline);
                PruneSelectedMarkers();
                NotifyTimelineActionCommands();
                RefreshStatusBar();
                PersistMarkerCache(force: true);
            },
            error => ReportError(error.Message));

    private void SetTimelineFromHistory(Timeline timeline)
    {
        SetActiveTimeline(timeline);
        PruneSelectedMarkers();
        if (SelectedDeletedRunId is Guid runId
            && Timeline!.GetDeletedRuns().All(r => r.Id != runId))
        {
            SelectedDeletedRunId = null;
        }

        NotifyTimelineActionCommands();
        RefreshStatusBar();
        PersistMarkerCache(force: true);
    }

    private void SetActiveTimeline(Timeline timeline)
    {
        if (_versionSet is not null)
            _versionSet = _versionSet.ReplaceActiveTimeline(timeline);
        Timeline = timeline;
    }

    private void ApplyVersionResult(Result<AbridgedVersionSet> result) =>
        result.Match(
            set => ApplyVersionSet(set),
            error => ReportError(error.Message));

    private void ApplyVersionSet(AbridgedVersionSet set, bool clearHistory = true)
    {
        _versionSet = set;
        Timeline = set.ActiveTimeline;
        if (clearHistory)
            _editHistory.Clear();
        ClearMarkerSelection();
        SyncVersionTabs();
        NotifyTimelineActionCommands();
        NotifyVersionCommands();
        RefreshStatusBar();
        PersistMarkerCache(force: true);
        if (Timeline.Markers.Count > 0)
            _ = RefreshThumbnailsAsync();
        else
            MarkerThumbs.Clear();
    }

    private void SyncVersionTabs()
    {
        VersionTabs.Clear();
        if (_versionSet is null)
        {
            OnPropertyChanged(nameof(ShowVersionTabs));
            OnPropertyChanged(nameof(HasVideoLoaded));
            return;
        }

        foreach (var version in _versionSet.Versions)
        {
            VersionTabs.Add(new VersionTabViewModel(
                version.Id,
                version.Name,
                version.Id == _versionSet.ActiveVersionId));
        }

        OnPropertyChanged(nameof(ShowVersionTabs));
        OnPropertyChanged(nameof(HasVideoLoaded));
    }

    private void NotifyVersionCommands()
    {
        CreateVersionCommand.NotifyCanExecuteChanged();
        DuplicateVersionCommand.NotifyCanExecuteChanged();
        RenameVersionCommand.NotifyCanExecuteChanged();
        DeleteVersionCommand.NotifyCanExecuteChanged();
        SelectVersionCommand.NotifyCanExecuteChanged();
    }

    private void PruneSelectedMarkers()
    {
        if (Timeline is null)
        {
            SelectedMarkerIds.Clear();
            return;
        }

        var valid = Timeline.Markers.Select(m => m.Id).ToHashSet();
        for (var i = SelectedMarkerIds.Count - 1; i >= 0; i--)
        {
            if (!valid.Contains(SelectedMarkerIds[i]))
                SelectedMarkerIds.RemoveAt(i);
        }
    }

    private void ClearMarkerSelection()
    {
        SelectedMarkerIds.Clear();
        SelectedDeletedRunId = null;
    }

    private void CancelSceneDetect()
    {
        // Cancel only; the in-flight run disposes the CTS in its finally block.
        // Sync path (RelayCommand / Dispose): block on CancelAsync to satisfy CA1849.
        if (_sceneDetectCts is null)
            return;
        _sceneDetectCts.CancelAsync().GetAwaiter().GetResult();
    }

    private void SuspendThumbnailRefresh() => _thumbnailRefreshSuspendCount++;

    private void ResumeThumbnailRefresh()
    {
        if (_thumbnailRefreshSuspendCount > 0)
            _thumbnailRefreshSuspendCount--;
        // Scene-detect thumbs are added incrementally; skip a full rebuild here to avoid flicker.
    }

    private bool TryAddSceneMarker(TimeSpan time, string path, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (_versionSet is null)
            return false;

        // Same skip rules as AddMarker: ignore out-of-range / too-close without failing the run.
        var id = Guid.NewGuid();
        var added = false;
        _versionSet.AddSharedSceneMarker(time, id).Match(
            next =>
            {
                _versionSet = next;
                Timeline = _versionSet.ActiveTimeline;
                PersistMarkerCache(force: false);
                _ = EnsureMarkerThumbnailAsync(id, time, path, duration, cancellationToken);
                added = true;
            },
            _ => { });
        return added;
    }

    private void PersistMarkerCache(bool force)
    {
        if (VideoPath is null || _versionSet is null)
            return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastCachePersistUtc < TimeSpan.FromSeconds(1.5))
            return;

        _lastCachePersistUtc = now;
        try
        {
            _markerCache.SaveVersionSet(VideoPath, _versionSet);
        }
        catch (IOException)
        {
            // Cache persistence is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache persistence is best-effort.
        }
        catch (JsonException)
        {
            // Cache persistence is best-effort.
        }
    }

    private AbridgedVersionSet RestoreVersionSetFromCache(TimeSpan duration, string path) =>
        _markerCache.TryLoadVersionSet(path, duration) ?? AbridgedVersionSet.CreateDefault(duration);

    private async Task EnsureMarkerThumbnailAsync(
        Guid markerId,
        TimeSpan time,
        string path,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        string? before = null;
        string? after = null;
        try
        {
            before = await _thumbnails.ExtractAsync(path, time, ThumbnailSide.Before, duration, cancellationToken)
                .ConfigureAwait(false);
            after = await _thumbnails.ExtractAsync(path, time, ThumbnailSide.After, duration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException)
        {
            // ignore thumb failures
        }
        catch (UnauthorizedAccessException)
        {
            // ignore thumb failures
        }
        catch (InvalidOperationException)
        {
            // ignore thumb failures
        }

        if (cancellationToken.IsCancellationRequested || VideoPath != path)
            return;

        var thumb = new MarkerThumbViewModel
        {
            MarkerId = markerId,
            Time = time,
            BeforeImage = LoadBitmap(before),
            AfterImage = LoadBitmap(after)
        };

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || VideoPath != path)
                return;

            var existing = MarkerThumbs.FirstOrDefault(t => t.MarkerId == markerId);
            if (existing is not null)
                MarkerThumbs.Remove(existing);
            MarkerThumbs.Add(thumb);
        }).Task.ConfigureAwait(false);
    }

    private async Task RunSceneDetectInBackgroundAsync(string path, TimeSpan duration)
    {
        CancelSceneDetect();

        var startAt = _versionSet?.SceneDetectPosition ?? TimeSpan.Zero;
        if (startAt < TimeSpan.Zero)
            startAt = TimeSpan.Zero;
        // Already finished for this file — nothing left to scan.
        if (duration > TimeSpan.Zero && startAt >= duration - TimeSpan.FromMilliseconds(100))
            return;

        var cts = new CancellationTokenSource();
        _sceneDetectCts = cts;
        var markersAdded = 0;
        IsDetectingScenes = true;
        NotifyUndoRedoCommands();
        // Suppress full rebuilds; thumbs for new scene markers are filled in as each cut arrives.
        SuspendThumbnailRefresh();

        var taskTitle = startAt > TimeSpan.Zero
            ? $"Scene detection: {Path.GetFileName(path)} (from {FormatTime(startAt)})"
            : $"Scene detection: {Path.GetFileName(path)}";
        var task = CreateTask(taskTitle);
        task.CanCancel = true;
        task.CancelCommand = new RelayCommand(CancelSceneDetect);
        if (startAt > TimeSpan.Zero && duration > TimeSpan.Zero)
            task.Progress = Math.Clamp(startAt.TotalSeconds / duration.TotalSeconds, 0, 1);

        try
        {
            var progress = new Progress<double>(p => task.Progress = p);
            var scenes = new Progress<TimeSpan>(time =>
            {
                if (cts.IsCancellationRequested || VideoPath != path)
                    return;
                if (TryAddSceneMarker(time, path, duration, cts.Token))
                    markersAdded++;
            });
            var positions = new Progress<TimeSpan>(pos =>
            {
                if (cts.IsCancellationRequested || VideoPath != path || _versionSet is null)
                    return;
                if (pos > _versionSet.SceneDetectPosition)
                    _versionSet = _versionSet.WithSceneDetectPosition(pos);
                PersistMarkerCache(force: false);
            });

            await _sceneDetector.DetectAsync(
                path,
                duration: duration,
                startAt: startAt,
                progress: progress,
                sceneFound: scenes,
                positionReached: positions,
                cancellationToken: cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested || VideoPath != path)
            {
                PersistMarkerCache(force: true);
                RemoveTask(task);
                return;
            }

            if (_versionSet is not null)
                _versionSet = _versionSet.WithSceneDetectPosition(duration);
            PersistMarkerCache(force: true);
            task.Progress = 1;
            // No result actions — remove when finished.
            RemoveTask(task);
        }
        catch (OperationCanceledException)
        {
            PersistMarkerCache(force: true);
            RemoveTask(task);
        }
        catch (InvalidOperationException ex)
        {
            PersistMarkerCache(force: true);
            RemoveTask(task);
            if (!cts.IsCancellationRequested)
                ReportError(ex.Message);
        }
        catch (IOException ex)
        {
            PersistMarkerCache(force: true);
            RemoveTask(task);
            if (!cts.IsCancellationRequested)
                ReportError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            PersistMarkerCache(force: true);
            RemoveTask(task);
            if (!cts.IsCancellationRequested)
                ReportError(ex.Message);
        }
        finally
        {
            // Scene markers are shared across versions and protected from delete; do not
            // leave a timeline-only undo that would desync other versions.
            if (markersAdded > 0)
                _editHistory.Clear();

            ResumeThumbnailRefresh();
            if (ReferenceEquals(_sceneDetectCts, cts))
            {
                _sceneDetectCts.Dispose();
                _sceneDetectCts = null;
            }
            else
            {
                cts.Dispose();
            }

            IsDetectingScenes = _sceneDetectCts is not null;
        }
    }

    private async Task LoadVideoAsync(string path, bool runSceneDetect)
    {
        CancelSceneDetect();
        if (IsPreviewMode)
            IsPreviewMode = false;

        TimeSpan duration = default;
        try
        {
            IsBusy = true;
            var fullPath = Path.GetFullPath(path);
            var info = await _probe.ProbeAsync(fullPath).ConfigureAwait(true);
            duration = info.Duration;
            VideoPath = fullPath;
            _player.Load(fullPath);
            IsPlaying = false;

            var versionSet = RestoreVersionSetFromCache(info.Duration, fullPath);
            // Avoid OnTimelineChanged kicking off a blocking all-marker thumb rebuild mid-load.
            SuspendThumbnailRefresh();
            try
            {
                _versionSet = versionSet;
                Timeline = versionSet.ActiveTimeline;
                SyncVersionTabs();
                NotifyVersionCommands();
            }
            finally
            {
                ResumeThumbnailRefresh();
            }

            Playhead = TimeSpan.Zero;
            ClearMarkerSelection();
            MarkerThumbs.Clear();
            _editHistory.Clear();
            NotifyTimelineActionCommands();
            RefreshStatusBar();
            if (versionSet.ActiveTimeline.Markers.Count > 0)
                _ = RefreshThumbnailsAsync();
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex.Message);
            return;
        }
        catch (IOException ex)
        {
            ReportError(ex.Message);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportError(ex.Message);
            return;
        }
        catch (ArgumentException ex)
        {
            ReportError(ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (runSceneDetect && VideoPath is { } loadedPath)
            _ = RunSceneDetectInBackgroundAsync(loadedPath, duration);
    }

    private LongTaskItem CreateTask(string title)
    {
        var task = new LongTaskItem
        {
            Title = title,
            Progress = 0,
            HasResultActions = false
        };
        Tasks.Add(task);
        IsTasksPaneOpen = true;
        OnPropertyChanged(nameof(ShowTasksPaneExpandButton));
        return task;
    }

    private void RemoveTask(LongTaskItem task)
    {
        void Remove()
        {
            Tasks.Remove(task);
            CollapseTasksPaneIfIdle();
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Remove();
        else
            Application.Current?.Dispatcher.Invoke(Remove);
    }

    private void DismissTask(LongTaskItem task) => RemoveTask(task);

    private static bool IsTaskWorthShowing(LongTaskItem task) =>
        task.CanCancel || task.HasResultActions;

    private bool HasTasksWorthShowing() => Tasks.Any(IsTaskWorthShowing);

    private void CollapseTasksPaneIfIdle()
    {
        OnPropertyChanged(nameof(ShowTasksPaneExpandButton));
        if (HasTasksWorthShowing())
            return;
        IsTasksPaneOpen = false;
    }

    private static void OpenExportedFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"File not found:\n{path}", "VideoAbridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static void ReportError(string message) =>
        MessageBox.Show(message, "VideoAbridge", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void RefreshStatusBar()
    {
        if (Timeline is null)
        {
            StatusBarText = "Open a video to begin.";
            return;
        }

        var parts = new List<string>
        {
            IsPreviewMode ? "Preview" : "Edit",
            IsPreviewMode
                ? $"Abridged: {FormatTime(_previewMap?.AbridgedDuration ?? Timeline.GetAbridgedDuration())}"
                : $"Abridged: {FormatTime(Timeline.GetAbridgedDuration())}"
        };

        if (SelectedMarkerIds.Count >= 1)
        {
            var selected = SelectedMarkerIds
                .Select(id => Timeline.Markers.FirstOrDefault(m => m.Id == id))
                .Where(m => m is not null)
                .Cast<Marker>()
                .OrderBy(m => m.Time)
                .ToList();

            if (selected.Count == 1)
            {
                parts.Add($"Marker: {FormatTime(selected[0].Time)}");
            }
            else if (selected.Count >= 2)
            {
                var start = selected[0].Time;
                var end = selected[^1].Time;
                parts.Add($"Markers: {FormatTime(start)} – {FormatTime(end)} (Δ {FormatTime(end - start)})");
            }
        }

        if (SelectedDeletedRunId is not null)
        {
            var run = Timeline.GetDeletedRuns().FirstOrDefault(r => r.Id == SelectedDeletedRunId);
            if (run is not null)
            {
                var kind = run.IsInterior
                    ? run.Join?.Mode == TransitionMode.Crossfade ? "XF" : "Cut"
                    : run.Range.Covers(Timeline.Duration)
                        ? "All"
                        : run.Range.TouchesStart ? "Begin" : "End";
                parts.Add(
                    $"{kind}: {FormatTime(run.Range.Start)} – {FormatTime(run.Range.End)} (Δ {FormatTime(run.Range.Duration)})");
            }
        }

        StatusBarText = string.Join("   |   ", parts);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
    }

    private void RefreshSelectionFromTimeline()
    {
        if (Timeline is null || SelectedDeletedRunId is null)
        {
            SelectedCrossfadeCap = 0;
            return;
        }

        var run = Timeline.GetDeletedRuns().FirstOrDefault(r => r.Id == SelectedDeletedRunId);
        if (run is null)
        {
            SelectedDeletedRunId = null;
            return;
        }

        if (!run.IsInterior || run.Join is null)
        {
            SelectedCrossfadeCap = 0;
            OnPropertyChanged(nameof(HasSelectedJoin));
            return;
        }

        _syncingSidebarUi = true;
        try
        {
            SelectedCrossfadeSeconds = run.Join.Mode == TransitionMode.Crossfade
                ? run.Join.CrossfadeSeconds
                : 0;
            SelectedCrossfadeCap = Timeline.GetCrossfadeCap(run.Range);
        }
        finally
        {
            _syncingSidebarUi = false;
        }

        OnPropertyChanged(nameof(HasSelectedJoin));
    }

    private async Task RefreshThumbnailsAsync()
    {
        if (Timeline is null || VideoPath is null)
            return;

        if (_thumbnailCts is not null)
        {
            await _thumbnailCts.CancelAsync().ConfigureAwait(false);
            _thumbnailCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;

        var path = VideoPath;
        var duration = Timeline.Duration;
        var markers = Timeline.Markers.ToList();

        await Application.Current.Dispatcher.InvokeAsync(() => MarkerThumbs.Clear()).Task.ConfigureAwait(false);

        const int concurrency = 2;
        const int uiBatchSize = 8;
        using var gate = new SemaphoreSlim(concurrency);
        var pendingUi = new List<MarkerThumbViewModel>(uiBatchSize);

        var tasks = markers.Select(async marker =>
        {
            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                string? before = null;
                string? after = null;
                try
                {
                    before = await _thumbnails.ExtractAsync(path, marker.Time, ThumbnailSide.Before, duration, cts.Token)
                        .ConfigureAwait(false);
                    after = await _thumbnails.ExtractAsync(path, marker.Time, ThumbnailSide.After, duration, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException)
                {
                    // ignore thumb failures
                }
                catch (UnauthorizedAccessException)
                {
                    // ignore thumb failures
                }
                catch (InvalidOperationException)
                {
                    // ignore thumb failures
                }

                if (cts.IsCancellationRequested)
                    return;

                var thumb = new MarkerThumbViewModel
                {
                    MarkerId = marker.Id,
                    Time = marker.Time,
                    BeforeImage = LoadBitmap(before),
                    AfterImage = LoadBitmap(after)
                };

                lock (pendingUi)
                {
                    pendingUi.Add(thumb);
                }

                // Flush outside the lock; concurrent flushers are OK (batch may be empty).
                List<MarkerThumbViewModel>? toFlush = null;
                lock (pendingUi)
                {
                    if (pendingUi.Count >= uiBatchSize)
                    {
                        toFlush = [.. pendingUi];
                        pendingUi.Clear();
                    }
                }

                if (toFlush is { Count: > 0 })
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (cts.IsCancellationRequested || VideoPath != path)
                            return;
                        foreach (var item in toFlush)
                        {
                            var existing = MarkerThumbs.FirstOrDefault(t => t.MarkerId == item.MarkerId);
                            if (existing is not null)
                                MarkerThumbs.Remove(existing);
                            MarkerThumbs.Add(item);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            List<MarkerThumbViewModel> remaining;
            lock (pendingUi)
            {
                remaining = [.. pendingUi];
                pendingUi.Clear();
            }

            if (remaining.Count > 0)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested || VideoPath != path)
                        return;
                    foreach (var item in remaining)
                    {
                        var existing = MarkerThumbs.FirstOrDefault(t => t.MarkerId == item.MarkerId);
                        if (existing is not null)
                            MarkerThumbs.Remove(existing);
                        MarkerThumbs.Add(item);
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer refresh
        }
    }

    private static BitmapImage? LoadBitmap(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = 120;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            CancelSceneDetect();
            try
            {
                _sceneDetectCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // CTS may already be disposed by a finishing scene-detect run.
            }

            _sceneDetectCts = null;

            if (_thumbnailCts is not null)
            {
                _thumbnailCts.CancelAsync().GetAwaiter().GetResult();
                try
                {
                    _thumbnailCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }

                _thumbnailCts = null;
            }

            EndCrossfadeVisuals();
            _secondaryPlayer?.Dispose();
            _secondaryPlayer = null;

            if (_exportCts is not null)
            {
                _exportCts.CancelAsync().GetAwaiter().GetResult();
                try
                {
                    _exportCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // CTS may already be disposed by a finishing export.
                }

                _exportCts = null;
            }

            _player.Dispose();
        }

        _disposed = true;
    }
}
