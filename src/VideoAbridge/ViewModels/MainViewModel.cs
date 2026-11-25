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
using System.Windows.Threading;
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
    private readonly AudioPeakExtractor _audioPeaks = new();
    private readonly AudioPeakCacheService _audioPeakCache = CreateAudioPeakCache();
    private readonly MarkerCacheService _markerCache = CreateMarkerCache();
    private readonly RecentFilesService _recentFiles = CreateRecentFiles();
    private readonly EditHistory _editHistory = new();
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _sceneDetectCts;
    private CancellationTokenSource? _audioPeaksCts;
    private bool _disposed;
    private int _thumbnailRefreshSuspendCount;
    private AbridgedVersionSet? _versionSet;
    private DateTime _lastCachePersistUtc;
    private WindowPlacementDto? _windowPlacement;
    private TimelineViewStateDto _editView = new();
    private TimelineViewStateDto? _previewView;
    private double _visibleStartSeconds;
    private bool _restoringUiFromCache;
    private bool _suppressViewCapture;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _selectionPreviewCts;
    private TimeSpan _selectionPreviewLeftSeek;
    private TimeSpan _selectionPreviewRightSeek;
    private FrameVideoPlayer? _previewLayerA;
    private FrameVideoPlayer? _previewLayerB;
    private ExportPlan? _previewPlan;
    private PreviewTimeMap? _previewMap;
    private AbridgedTimelineLayout? _abridgedLayout;
    private TimeSpan? _seekGateTarget;
    private DateTime _seekGateUntilUtc;
    /// <summary>When true, layer A is the outgoing/active solo layer; B is incoming during XF.</summary>
    private bool _outgoingIsLayerA = true;
    private bool _compositorInCrossfade;
    private TimeSpan _lastCompositorOutgoingSeek = TimeSpan.MinValue;
    private DispatcherTimer? _previewClockTimer;
    private DateTime _compositorPlayOriginUtc;
    private TimeSpan _compositorPlayOriginAbridged;
    private bool _committingVersionRename;
    private double _frameRate = 30;
    private PlayheadMoveMode _editMoveMode = PlayheadMoveMode.Marker;

    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private Timeline? _timeline;
    [ObservableProperty] private TimeSpan _playhead;
    [ObservableProperty] private TimeSpan _abridgedPlayhead;
    [ObservableProperty] private string _statusBarText = "Open a video to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private DeletedRunId? _selectedDeletedRunId;
    [ObservableProperty] private TimelineItemId? _focusedTimelineItemId;
    [ObservableProperty] private double _selectedCrossfadeSeconds;
    [ObservableProperty] private double _selectedCrossfadeCap;
    [ObservableProperty] private double _fadeFromBlackSeconds;
    [ObservableProperty] private double _startFadeCap;
    [ObservableProperty] private double _fadeToBlackSeconds;
    [ObservableProperty] private double _endFadeCap;
    [ObservableProperty] private string _selectedMarkerLabel = "";
    private bool _syncingSidebarUi;
    [ObservableProperty] private bool _isPreviewMode;
    [ObservableProperty] private double _previewBackdropOpacity;
    [ObservableProperty] private double _previewBlackoutOpacity;
    [ObservableProperty] private double _previewLayerAOpacity;
    [ObservableProperty] private ImageSource? _previewLayerAFrame;
    [ObservableProperty] private double _previewLayerBOpacity;
    [ObservableProperty] private ImageSource? _previewLayerBFrame;
    [ObservableProperty] private bool _hasSelectionPreview;
    [ObservableProperty] private ImageSource? _selectionPreviewLeftImage;
    [ObservableProperty] private ImageSource? _selectionPreviewRightImage;
    [ObservableProperty] private PlayheadMoveMode _playheadMoveMode = PlayheadMoveMode.Marker;

    public string PreviewModeToggleLabel => IsPreviewMode ? "Edit" : "Preview";

    public bool ShowEditChrome => !IsPreviewMode;

    public string PlayheadMoveModeLabel => PlayheadMoveMode switch
    {
        PlayheadMoveMode.Marker => "Move: Marker",
        PlayheadMoveMode.Cut => "Move: Cut",
        PlayheadMoveMode.Frame => "Move: 1 frame",
        PlayheadMoveMode.Second => "Move: 1 second",
        PlayheadMoveMode.TenSeconds => "Move: 10 seconds",
        _ => "Move:",
    };

    public bool HasSelectedJoin =>
        SelectedDeletedRunId is { } id
        && Timeline?.GetDeletedRuns().FirstOrDefault(r => r.Id == id) is { IsInterior: true, Join: not null };

    public bool HasSingleSelectedMarker => SelectedMarkerIds.Count == 1;

    public AbridgedTimelineLayout? AbridgedLayout => _abridgedLayout;

    public ExportPlan? PreviewExportPlan => _previewPlan;

    public PreviewTimeMap? PreviewTimeMap => _previewMap;

    /// <summary>Timeline needle: abridged clock in Preview, source clock in Edit.</summary>
    public TimeSpan DisplayPlayhead => IsPreviewMode ? AbridgedPlayhead : Playhead;

    [ObservableProperty] private AudioPeakEnvelope? _audioPeakEnvelope;

    [ObservableProperty] private double _minPixelsPerSecond = 4;
    [ObservableProperty] private double _maxPixelsPerSecond = 400;
    [ObservableProperty] private double _pixelsPerSecond = 40;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isDetectingScenes;

    public VideoPlayerService Player => _player;

    public ObservableCollection<MarkerThumbViewModel> MarkerThumbs { get; } = [];
    public ObservableCollection<MarkerId> SelectedMarkerIds { get; } = [];
    public ObservableCollection<LongTaskItem> Tasks { get; } = [];
    public ObservableCollection<VersionTabViewModel> VersionTabs { get; } = [];
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    [ObservableProperty] private bool _isTasksPaneOpen;

    public bool ShowTasksPaneCollapsed => !IsTasksPaneOpen;

    public int RunningTaskCount => Tasks.Count(t => t.CanCancel);

    public bool HasRunningTasks => RunningTaskCount > 0;

    public string TasksPaneCollapsedLabel => $"Tasks ({RunningTaskCount})";

    public bool ShowVersionTabs => HasVideoLoaded;

    public bool HasVideoLoaded => VideoPath is not null && _versionSet is not null;

    public string WindowTitle =>
        string.IsNullOrEmpty(VideoPath)
            ? "VideoAbridge"
            : $"VideoAbridge - {Path.GetFileName(VideoPath)}";

    /// <summary>Latest window placement restored from cache (MainWindow applies it).</summary>
    public WindowPlacementDto? PendingWindowPlacement { get; private set; }

    /// <summary>Timeline scroll restore request (seconds); MainWindow applies it on the control.</summary>
    public double? PendingVisibleStartSeconds { get; private set; }

    public MainViewModel()
    {
        _player.TimeChanged += (_, time) =>
        {
            // BeginInvoke (not Invoke): LibVLC callbacks + UI-thread Seek otherwise deadlock.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Preview uses the dual-layer compositor clock; HWND time is Edit-only.
                if (IsPreviewMode)
                    return;
                HandlePlayerTime(time);
            });
        };
        _player.EndReached += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!IsPlaying)
                    return;
                PausePlayback();
            });
        };
        SelectedMarkerIds.CollectionChanged += OnSelectedMarkerIdsChanged;
        RefreshRecentFiles();
        RefreshStatusBar();
    }

    private void OnSelectedMarkerIdsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyTimelineActionCommands();
        SyncSelectedMarkerLabelUi();
        RefreshStatusBar();
        _ = RefreshSelectionPreviewAsync();
    }

    partial void OnAbridgedPlayheadChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(DisplayPlayhead));
        if (_restoringUiFromCache || _suppressViewCapture || !IsPreviewMode)
            return;
        CaptureActiveViewState();
        PersistMarkerCache(force: false);
    }

    partial void OnVideoPathChanged(string? value) =>
        OnPropertyChanged(nameof(WindowTitle));

    partial void OnIsDetectingScenesChanged(bool value)
    {
        NotifyTimelineActionCommands();
        NotifyVersionCommands();
    }

    private void NotifyTimelineActionCommands()
    {
        AddMarkerAtPlayheadCommand.NotifyCanExecuteChanged();
        MoveMarkerToPlayheadCommand.NotifyCanExecuteChanged();
        DeleteMarkerCommand.NotifyCanExecuteChanged();
        DropSectionCommand.NotifyCanExecuteChanged();
        RestoreSectionCommand.NotifyCanExecuteChanged();
        DropSectionFromStartCommand.NotifyCanExecuteChanged();
        DropSectionToEndCommand.NotifyCanExecuteChanged();
        RestoreSectionFromStartCommand.NotifyCanExecuteChanged();
        RestoreSectionToEndCommand.NotifyCanExecuteChanged();
        DetectScenesCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        TogglePreviewModeCommand.NotifyCanExecuteChanged();
        GoToBeginningCommand.NotifyCanExecuteChanged();
        NotifyUndoRedoCommands();
        NotifyCommandTooltips();
    }

    private void NotifyUndoRedoCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
    }

    private void NotifyCommandTooltips()
    {
        OnPropertyChanged(nameof(AddMarkerToolTip));
        OnPropertyChanged(nameof(MoveMarkerToPlayheadToolTip));
        OnPropertyChanged(nameof(DeleteMarkerToolTip));
        OnPropertyChanged(nameof(DetectScenesToolTip));
        OnPropertyChanged(nameof(DropSectionToolTip));
        OnPropertyChanged(nameof(RestoreSectionToolTip));
        OnPropertyChanged(nameof(DropSectionFromStartToolTip));
        OnPropertyChanged(nameof(DropSectionToEndToolTip));
        OnPropertyChanged(nameof(RestoreSectionFromStartToolTip));
        OnPropertyChanged(nameof(RestoreSectionToEndToolTip));
        OnPropertyChanged(nameof(TogglePreviewModeToolTip));
        OnPropertyChanged(nameof(ExportToolTip));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        OnPropertyChanged(nameof(CreateVersionToolTip));
        OnPropertyChanged(nameof(DuplicateVersionToolTip));
        OnPropertyChanged(nameof(RenameVersionToolTip));
        OnPropertyChanged(nameof(DeleteVersionToolTip));
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
            FadeToBlackSeconds = value.Settings.FadeToBlackSeconds ?? 0;
            var (kept, deleted) = value.GetKeptAndDeletedRuns();
            StartFadeCap = Timeline.GetStartFadeCap(kept, deleted);
            EndFadeCap = Timeline.GetEndFadeCap(kept, deleted);
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
        SyncSelectedMarkerLabelUi();
        SyncFocusFromPlayhead();
        RefreshStatusBar();
        _ = RefreshSelectionPreviewAsync();
    }

    partial void OnPlayheadMoveModeChanged(PlayheadMoveMode value) =>
        OnPropertyChanged(nameof(PlayheadMoveModeLabel));

    partial void OnIsPreviewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewModeToggleLabel));
        OnPropertyChanged(nameof(ShowEditChrome));
        OnPropertyChanged(nameof(DisplayPlayhead));
        TogglePreviewModeCommand.NotifyCanExecuteChanged();
        SyncPlayheadMoveModeForPreview(enteringPreview: value);
        SyncPreviewClockTimer();
        _ = RefreshSelectionPreviewAsync();

        if (_restoringUiFromCache)
        {
            if (value)
                EnterRealtimePreview(restoreSavedView: true);
            else
                ExitRealtimePreview();
            SyncFocusFromPlayhead();
            RefreshStatusBar();
            return;
        }

        // Capture the mode we are leaving while pps/scroll still belong to it.
        if (value)
            _editView = CaptureViewState(previewMode: false);
        else
            _previewView = CaptureViewState(previewMode: true);

        _suppressViewCapture = true;

        if (value)
            EnterRealtimePreview(restoreSavedView: _previewView is not null);
        else
            ExitRealtimePreview();

        SyncFocusFromPlayhead();

        // Defer restore until TimelineControl has switched duration / zoom bounds.
        var stateToApply = value ? _previewView : CloneView(_editView);
        var previewMode = value;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplyViewState(stateToApply, previewMode);
            PersistMarkerCache(force: false);
        }, DispatcherPriority.Loaded);

        RefreshStatusBar();
    }

    partial void OnSelectedDeletedRunIdChanged(DeletedRunId? value)
    {
        OnPropertyChanged(nameof(HasSelectedJoin));
        RestoreSectionCommand.NotifyCanExecuteChanged();
        RefreshSelectionFromTimeline();
        RefreshStatusBar();
        _ = RefreshSelectionPreviewAsync();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Timeline is null || !_editHistory.TryUndo(Timeline, out var previous))
            return;

        SetTimelineFromHistory(previous);
    }

    private bool CanUndo() => GetUndoBlockReason() is null;

    public string? UndoToolTip => GetUndoBlockReason();

    private string? GetUndoBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (IsDetectingScenes)
            return "Wait for scene detection to finish.";
        if (!_editHistory.CanUndo)
            return "Nothing to undo.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (Timeline is null || !_editHistory.TryRedo(Timeline, out var next))
            return;

        SetTimelineFromHistory(next);
    }

    private bool CanRedo() => GetRedoBlockReason() is null;

    public string? RedoToolTip => GetRedoBlockReason();

    private string? GetRedoBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (IsDetectingScenes)
            return "Wait for scene detection to finish.";
        if (!_editHistory.CanRedo)
            return "Nothing to redo.";
        return null;
    }

    [RelayCommand]
    private void ExpandTasksPane() => IsTasksPaneOpen = true;

    [RelayCommand]
    private void CollapseTasksPane() => IsTasksPaneOpen = false;

    [RelayCommand]
    private void ToggleTasksPane() => IsTasksPaneOpen = !IsTasksPaneOpen;

    partial void OnIsTasksPaneOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTasksPaneCollapsed));
        if (!_restoringUiFromCache)
            PersistMarkerCache(force: false);
    }

    partial void OnPixelsPerSecondChanged(double value)
    {
        if (_restoringUiFromCache || _suppressViewCapture)
            return;
        CaptureActiveViewState();
        PersistMarkerCache(force: false);
    }

    partial void OnPlayheadChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(DisplayPlayhead));
        NotifyTimelineActionCommands();
        SyncFocusFromPlayhead();
        if (_restoringUiFromCache || _suppressViewCapture || IsPreviewMode)
            return;
        CaptureActiveViewState();
        PersistMarkerCache(force: false);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        NotifyTimelineActionCommands();
        SyncPreviewClockTimer();
    }

    /// <summary>Called by MainWindow when the timeline scrollbar moves.</summary>
    public void ReportTimelineVisibleStart(double visibleStartSeconds)
    {
        _visibleStartSeconds = Math.Max(0, visibleStartSeconds);
        if (_restoringUiFromCache || _suppressViewCapture)
            return;
        CaptureActiveViewState();
        PersistMarkerCache(force: false);
    }

    /// <summary>Called by the main window when its bounds or state change.</summary>
    public void ReportWindowPlacement(WindowPlacementDto placement, bool forcePersist = false)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _windowPlacement = new WindowPlacementDto
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height,
            State = placement.State
        };
        if (!_restoringUiFromCache)
            PersistMarkerCache(force: forcePersist);
    }

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

    [RelayCommand]
    private async Task OpenRecentVideoAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            try
            {
                RefreshRecentFiles(_recentFiles.Remove(path));
            }
            catch (IOException)
            {
                RefreshRecentFiles();
            }
            catch (UnauthorizedAccessException)
            {
                RefreshRecentFiles();
            }

            ReportError($"File not found:\n{path}");
            return;
        }

        await LoadVideoAsync(path, runSceneDetect: true).ConfigureAwait(true);
    }

    private static MarkerCacheService CreateMarkerCache()
    {
        var dir = Environment.GetEnvironmentVariable("VIDEOABRIDGE_MARKER_CACHE");
        return string.IsNullOrWhiteSpace(dir)
            ? new MarkerCacheService()
            : new MarkerCacheService(dir);
    }

    private static AudioPeakCacheService CreateAudioPeakCache()
    {
        var dir = Environment.GetEnvironmentVariable("VIDEOABRIDGE_WAVEFORM_CACHE");
        return string.IsNullOrWhiteSpace(dir)
            ? new AudioPeakCacheService()
            : new AudioPeakCacheService(dir);
    }

    private static RecentFilesService CreateRecentFiles()
    {
        var path = Environment.GetEnvironmentVariable("VIDEOABRIDGE_RECENT_FILES");
        return string.IsNullOrWhiteSpace(path)
            ? new RecentFilesService()
            : new RecentFilesService(path);
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

        BeginInlineRename(_versionSet.ActiveVersionId);
    }

    internal void BeginInlineRename(VersionId versionId)
    {
        if (!CanManageVersions())
            return;

        foreach (var tab in VersionTabs)
        {
            if (tab.Id == versionId)
                tab.BeginEdit();
            else if (tab.IsEditing)
                tab.CancelEdit();
        }
    }

    /// <summary>Returns true when editing ended (committed or cancelled).</summary>
    internal bool CommitVersionRename(VersionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_committingVersionRename || !tab.IsEditing || _versionSet is null)
            return !tab.IsEditing;

        var trimmed = tab.EditName.Trim();
        if (trimmed.Length == 0
            || string.Equals(trimmed, tab.Name, StringComparison.Ordinal))
        {
            tab.CancelEdit();
            return true;
        }

        _committingVersionRename = true;
        try
        {
            var committed = false;
            _versionSet.Rename(tab.Id, trimmed).Match(
                next =>
                {
                    _versionSet = next;
                    tab.CancelEdit();
                    SyncVersionTabs();
                    PersistMarkerCache(force: true);
                    RefreshStatusBar();
                    committed = true;
                },
                error =>
                {
                    tab.EditName = tab.Name;
                    ReportError(error.Message);
                });
            return committed;
        }
        finally
        {
            _committingVersionRename = false;
        }
    }

    internal void ReorderVersion(VersionId versionId, int newIndex)
    {
        if (_versionSet is null || !CanManageVersions())
            return;

        _versionSet.Reorder(versionId, newIndex).Match(
            next =>
            {
                _versionSet = next;
                SyncVersionTabs();
                PersistMarkerCache(force: true);
            },
            error => ReportError(error.Message));
    }

    [RelayCommand(CanExecute = nameof(CanSelectVersion))]
    private void SelectVersion(VersionId versionId)
    {
        if (_versionSet is null || versionId == _versionSet.ActiveVersionId)
            return;

        foreach (var tab in VersionTabs.Where(t => t.IsEditing))
            tab.CancelEdit();

        PersistMarkerCache(force: true);
        ApplyVersionResult(_versionSet.SetActive(versionId));
    }

    [RelayCommand(CanExecute = nameof(CanCycleVersion))]
    private void SelectNextVersion() => CycleVersion(1);

    [RelayCommand(CanExecute = nameof(CanCycleVersion))]
    private void SelectPreviousVersion() => CycleVersion(-1);

    private bool CanCycleVersion() =>
        CanSelectVersion() && VersionTabs.Count > 1;

    private void CycleVersion(int delta)
    {
        if (_versionSet is null || VersionTabs.Count < 2)
            return;

        var index = -1;
        for (var i = 0; i < VersionTabs.Count; i++)
        {
            if (VersionTabs[i].Id == _versionSet.ActiveVersionId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        var count = VersionTabs.Count;
        var next = ((index + delta) % count + count) % count;
        SelectVersion(VersionTabs[next].Id);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteVersion))]
    private void DeleteVersion(VersionId versionId)
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

    [RelayCommand(CanExecute = nameof(CanDeleteVersion))]
    private void DeleteActiveVersion()
    {
        if (_versionSet is null)
            return;

        DeleteVersion(_versionSet.ActiveVersionId);
    }

    public TimeSpan FrameStep =>
        _frameRate > 1e-6
            ? TimeSpan.FromSeconds(1.0 / _frameRate)
            : TimeSpan.FromSeconds(1.0 / 30.0);

    private bool CanManageVersions() => GetManageVersionsBlockReason() is null;

    public string? CreateVersionToolTip => GetManageVersionsBlockReason();

    public string? DuplicateVersionToolTip => GetManageVersionsBlockReason();

    public string? RenameVersionToolTip => GetManageVersionsBlockReason();

    private string? GetManageVersionsBlockReason()
    {
        if (!HasVideoLoaded)
            return "Open a video to begin.";
        if (IsDetectingScenes)
            return "Wait for scene detection to finish.";
        return null;
    }

    private bool CanSelectVersion() => HasVideoLoaded && !IsDetectingScenes;

    private bool CanDeleteVersion() => GetDeleteVersionBlockReason() is null;

    public string DeleteVersionToolTip =>
        GetDeleteVersionBlockReason() ?? "Delete version";

    private string? GetDeleteVersionBlockReason()
    {
        if (!HasVideoLoaded)
            return "Open a video to begin.";
        if (IsDetectingScenes)
            return "Wait for scene detection to finish.";
        if (_versionSet is not { Versions.Count: > 1 })
            return "At least one version is required.";
        return null;
    }

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

    [RelayCommand(CanExecute = nameof(CanGoToBeginning))]
    private void GoToBeginning()
    {
        if (Timeline is null)
            return;

        if (IsPreviewMode)
            SeekAbridgedPlayhead(TimeSpan.Zero);
        else
            SeekPlayhead(TimeSpan.Zero);
    }

    private bool CanGoToBeginning() => HasVideoLoaded;

    [RelayCommand]
    private void PlayPause()
    {
        if (Timeline is null)
            return;

        // Prefer our flag: LibVLC IsPlaying can stay true after the first-frame pause.
        if (IsPlaying)
        {
            PausePlayback();
            return;
        }

        if (IsPreviewMode)
        {
            if (_previewPlan is null || _previewMap is null)
                return;

            var abridged = AbridgedPlayhead;
            if (_previewMap.AbridgedDuration > TimeSpan.Zero
                && abridged >= _previewMap.AbridgedDuration - Timeline.MarkerEpsilon)
            {
                SeekAbridgedPlayhead(TimeSpan.Zero);
            }
            else
            {
                SeekAbridgedPlayhead(abridged);
            }

            _compositorPlayOriginUtc = DateTime.UtcNow;
            _compositorPlayOriginAbridged = AbridgedPlayhead;
            IsPlaying = true;
            SyncPreviewClockTimer();
            ApplyCompositor(
                PreviewSeekState.FromAbridged(_previewPlan, _previewMap, AbridgedPlayhead),
                play: true);
            return;
        }

        _player.Play();
        IsPlaying = true;
    }

    [RelayCommand(CanExecute = nameof(CanTogglePreviewMode))]
    private void TogglePreviewMode() => IsPreviewMode = !IsPreviewMode;

    private bool CanTogglePreviewMode()
    {
        if (IsPreviewMode)
            return true;
        return HasKeptMaterial();
    }

    public string? TogglePreviewModeToolTip =>
        CanTogglePreviewMode()
            ? null
            : "There is no kept material to preview.";

    [RelayCommand(CanExecute = nameof(CanAddMarkerAtPlayhead))]
    private void AddMarkerAtPlayhead()
    {
        if (Timeline is null)
            return;

        var id = MarkerId.New();
        Apply(Timeline.AddMarker(Playhead, MarkerSource.User, id));
        if (Timeline is null || Timeline.Markers.All(m => m.Id != id))
            return;

        ClearMarkerSelection();
        SelectedMarkerIds.Add(id);
        SyncFocusFromPlayhead();
    }

    private bool CanAddMarkerAtPlayhead() => GetAddMarkerBlockReason() is null;

    public string? AddMarkerToolTip => GetAddMarkerBlockReason();

    private string? GetAddMarkerBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (Playhead <= TimeSpan.Zero || Playhead >= Timeline.Duration)
            return "Move the playhead inside the video to add a marker.";
        if (Timeline.Markers.Any(m => AbsDiff(m.Time, Playhead) <= Timeline.MarkerEpsilon))
            return "A marker already exists near the playhead.";
        return null;
    }

    public void ToggleMarkerSelection(MarkerId markerId, bool extendSelection = false)
    {
        SelectedDeletedRunId = null;

        IReadOnlyDictionary<MarkerId, TimeSpan> markerTimes = Timeline is null
            ? []
            : Timeline.Markers.ToDictionary(m => m.Id, m => m.Time);

        var next = MarkerSelection.ApplyClick(
            SelectedMarkerIds.ToList(),
            markerId,
            extendSelection,
            markerTimes);

        SelectedMarkerIds.Clear();
        foreach (var id in next)
            SelectedMarkerIds.Add(id);

        if (SelectedMarkerIds.Contains(markerId))
            SeekPlayheadToMarker(markerId);
        else
            SyncFocusFromPlayhead();
    }

    /// <summary>Select sticky Left/Right mode (keys 5–9). Marker is ignored in Preview.</summary>
    public void SetPlayheadMoveMode(PlayheadMoveMode mode)
    {
        if (!HasVideoLoaded)
            return;
        if (IsPreviewMode && mode == PlayheadMoveMode.Marker)
            return;

        PlayheadMoveMode = mode;
        if (!IsPreviewMode)
            _editMoveMode = mode;
    }

    /// <summary>Seek-and-pause by the sticky move mode. Returns true if the playhead moved.</summary>
    public bool MovePlayheadByMode(int direction)
    {
        if (!HasVideoLoaded || direction == 0)
            return false;

        return PlayheadMoveMode switch
        {
            PlayheadMoveMode.Marker => JumpPlayheadToMarker(direction),
            PlayheadMoveMode.Cut => IsPreviewMode
                ? JumpAbridgedPlayheadToOverlay(direction)
                : JumpPlayheadToCutCenter(direction),
            PlayheadMoveMode.Frame => NudgePlayheadByMode(direction, FrameStep),
            PlayheadMoveMode.Second => NudgePlayheadByMode(direction, TimeSpan.FromSeconds(1)),
            PlayheadMoveMode.TenSeconds => NudgePlayheadByMode(direction, TimeSpan.FromSeconds(10)),
            _ => false,
        };
    }

    /// <summary>
    /// Nudge selected manual markers by the sticky move mode (Ctrl+Left/Right).
    /// Marker and Cut modes are disabled. Cut selection moves its delimiting manual markers.
    /// On failure, shows a warning with the reason.
    /// </summary>
    public bool MoveSelectionByMode(int direction)
    {
        if (direction == 0)
            return false;

        if (GetMoveSelectionByModeBlockReason(direction, out var targets, out var delta) is { } reason)
        {
            ReportError(reason);
            return false;
        }

        PauseIfPlaying();

        // Move with the nudge so selected neighbors do not collide mid-chain.
        var ordered = direction > 0
            ? targets.OrderByDescending(m => m.Time)
            : targets.OrderBy(m => m.Time);

        Result<Timeline> result = Timeline!;
        foreach (var marker in ordered)
        {
            var id = marker.Id;
            var newTime = marker.Time + delta;
            result = result.Bind(t => t.MoveMarker(id, newTime));
        }

        var moved = false;
        result.Match(
            timeline =>
            {
                if (ReferenceEquals(timeline, Timeline))
                {
                    ReportError("Selection could not be moved further.");
                    return;
                }

                Apply(result);
                moved = true;
            },
            error => ReportError(error.Message));
        return moved;
    }

    private string? GetMoveSelectionByModeBlockReason(
        int direction,
        out List<Marker> targets,
        out TimeSpan delta)
    {
        targets = [];
        delta = default;

        if (Timeline is null)
            return "Open a video to begin.";
        if (IsPreviewMode)
            return "Switch to Edit to nudge the selection.";
        if (PlayheadMoveMode is PlayheadMoveMode.Marker or PlayheadMoveMode.Cut)
            return "Selection nudge is disabled in Marker and Cut move modes. Press 7–9 for frame or time.";

        if (GetManualMarkersForSelectionMove(out targets) is { } selectionReason)
            return selectionReason;

        delta = PlayheadMoveMode switch
        {
            PlayheadMoveMode.Frame => direction < 0 ? -FrameStep : FrameStep,
            PlayheadMoveMode.Second => TimeSpan.FromSeconds(direction < 0 ? -1 : 1),
            PlayheadMoveMode.TenSeconds => TimeSpan.FromSeconds(direction < 0 ? -10 : 10),
            _ => TimeSpan.Zero,
        };

        if (delta == TimeSpan.Zero)
            return "Nothing to move for the current move mode.";

        return null;
    }

    /// <summary>Null when <paramref name="targets"/> has at least one movable manual marker.</summary>
    private string? GetManualMarkersForSelectionMove(out List<Marker> targets)
    {
        targets = [];
        if (Timeline is null)
            return "Open a video to begin.";

        if (SelectedDeletedRunId is { } runId)
        {
            if (!Timeline.TryGetDeletedRunBoundaries(runId, out var leftId, out var rightId))
                return "Could not resolve the selected cut’s boundaries.";

            foreach (var id in new[] { leftId, rightId })
            {
                if (id == MarkerId.StartBound || id == MarkerId.EndBound)
                    continue;
                var marker = Timeline.Markers.FirstOrDefault(m => m.Id == id);
                if (marker is not null && marker.Source == MarkerSource.User)
                    targets.Add(marker);
            }

            return targets.Count == 0
                ? "This cut has no movable manual boundary markers."
                : null;
        }

        if (SelectedMarkerIds.Count == 0)
            return "Select a manual marker or a cut to nudge.";

        var sawSceneOnly = false;
        foreach (var id in SelectedMarkerIds)
        {
            var marker = Timeline.Markers.FirstOrDefault(m => m.Id == id);
            if (marker is null)
                continue;
            if (marker.Source == MarkerSource.User)
                targets.Add(marker);
            else
                sawSceneOnly = true;
        }

        if (targets.Count > 0)
            return null;

        return sawSceneOnly
            ? "Only manual markers can be nudged (scene-detect markers stay fixed)."
            : "Select a manual marker or a cut to nudge.";
    }

    private void SyncPlayheadMoveModeForPreview(bool enteringPreview)
    {
        if (enteringPreview)
        {
            if (PlayheadMoveMode == PlayheadMoveMode.Marker)
                PlayheadMoveMode = PlayheadMoveMode.Cut;
            return;
        }

        PlayheadMoveMode = _editMoveMode;
    }

    private bool NudgePlayheadByMode(int direction, TimeSpan step)
    {
        PauseIfPlaying();

        var delta = direction < 0 ? -step : step;
        if (IsPreviewMode)
        {
            if (_previewMap is null)
                return false;

            var next = AbridgedPlayhead + delta;
            if (next < TimeSpan.Zero)
                next = TimeSpan.Zero;
            else if (next > _previewMap.AbridgedDuration)
                next = _previewMap.AbridgedDuration;
            if (next == AbridgedPlayhead)
                return false;

            SeekAbridgedPlayhead(next);
            return true;
        }

        if (Timeline is null)
            return false;

        var nextSource = Playhead + delta;
        if (nextSource < TimeSpan.Zero)
            nextSource = TimeSpan.Zero;
        else if (nextSource > Timeline.Duration)
            nextSource = Timeline.Duration;
        if (nextSource == Playhead)
            return false;

        SeekPlayhead(nextSource);
        return true;
    }

    /// <summary>Seek-and-pause to previous/next marker. Returns true if the playhead moved.</summary>
    public bool JumpPlayheadToMarker(int direction)
    {
        if (IsPreviewMode || Timeline is null)
            return false;

        var markers = BuildMarkerLane();
        var target = direction < 0
            ? TimelineFocusNavigation.PreviousMarkerTime(Playhead, markers)
            : TimelineFocusNavigation.NextMarkerTime(Playhead, markers);
        if (target is not { } time)
            return false;

        PauseIfPlaying();
        SeekPlayhead(time);
        return true;
    }

    /// <summary>Seek-and-pause to previous/next deleted-run center. Returns true if the playhead moved.</summary>
    public bool JumpPlayheadToCutCenter(int direction)
    {
        if (IsPreviewMode || Timeline is null)
            return false;

        var runs = BuildDeletedRunLane();
        var target = direction < 0
            ? TimelineFocusNavigation.PreviousCutCenter(Playhead, runs)
            : TimelineFocusNavigation.NextCutCenter(Playhead, runs);
        if (target is not { } time)
            return false;

        PauseIfPlaying();
        SeekPlayhead(time);
        return true;
    }

    /// <summary>
    /// Preview: seek-and-pause to the previous/next cut or fade lead-in
    /// (<see cref="PreviewOverlayNavigation.LeadIn"/> before the range, capped at zero).
    /// </summary>
    public bool JumpAbridgedPlayheadToOverlay(int direction)
    {
        if (!IsPreviewMode || _abridgedLayout is null)
            return false;

        var targets = PreviewOverlayNavigation.SeekTargets(_abridgedLayout.Overlays);
        var target = direction < 0
            ? PreviewOverlayNavigation.Previous(AbridgedPlayhead, targets)
            : PreviewOverlayNavigation.Next(AbridgedPlayhead, targets);
        if (target is not { } time)
            return false;

        PauseIfPlaying();
        SeekAbridgedPlayhead(time);
        return true;
    }

    public void ActivateTimelineFocus(bool extendSelection)
    {
        if (IsPreviewMode || Timeline is null || FocusedTimelineItemId is not { } focus)
            return;

        if (focus is MarkerId markerId)
            ToggleMarkerSelection(markerId, extendSelection);
        else if (focus is DeletedRunId runId)
            SelectDeletedRun(runId);
    }

    public void SyncFocusFromPlayhead()
    {
        if (Timeline is null || IsPreviewMode)
        {
            FocusedTimelineItemId = null;
            return;
        }

        // Inline resolve to avoid allocating lane lists on every playhead tick.
        foreach (var marker in Timeline.Markers)
        {
            if (marker.Time == Playhead)
            {
                FocusedTimelineItemId = marker.Id;
                return;
            }
        }

        foreach (var run in Timeline.GetDeletedRuns())
        {
            if (run.Range.Contains(Playhead))
            {
                FocusedTimelineItemId = run.Id;
                return;
            }
        }

        FocusedTimelineItemId = null;
    }

    private void PauseIfPlaying()
    {
        if (!IsPlaying)
            return;
        PausePlayback();
    }

    private void PausePlayback()
    {
        _player.Pause();
        TryLayerPause(_previewLayerA);
        TryLayerPause(_previewLayerB);
        IsPlaying = false;
        SyncPreviewClockTimer();
    }

    private static void TryLayerPause(FrameVideoPlayer? layer)
    {
        try
        {
            layer?.Pause();
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

    private static void TryLayerPlay(FrameVideoPlayer? layer)
    {
        try
        {
            layer?.Play();
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

    private List<(TimelineItemId Id, TimeSpan Time)> BuildMarkerLane()
    {
        if (Timeline is null)
            return [];

        return [.. Timeline.Markers
            .OrderBy(m => m.Time)
            .Select(m => ((TimelineItemId)m.Id, m.Time))];
    }

    private List<(TimelineItemId Id, TimeSpan Start, TimeSpan End)> BuildDeletedRunLane()
    {
        if (Timeline is null)
            return [];

        return [.. Timeline.GetDeletedRuns()
            .OrderBy(r => r.Range.Start)
            .Select(r => ((TimelineItemId)r.Id, r.Range.Start, r.Range.End))];
    }

    private void SeekPlayheadToMarker(MarkerId markerId)
    {
        if (Timeline is null)
            return;

        var marker = Timeline.Markers.FirstOrDefault(m => m.Id == markerId);
        if (marker is null)
            return;

        SeekPlayhead(marker.Time);
    }

    [RelayCommand(CanExecute = nameof(CanMoveMarkerToPlayhead))]
    private void MoveMarkerToPlayhead()
    {
        if (Timeline is null || !TryGetNearestSelectedManualMarker(out var marker))
            return;

        Apply(Timeline.MoveMarker(marker.Id, Playhead));
    }

    private bool CanMoveMarkerToPlayhead() => GetMoveMarkerToPlayheadBlockReason() is null;

    public string? MoveMarkerToPlayheadToolTip => GetMoveMarkerToPlayheadBlockReason();

    private string? GetMoveMarkerToPlayheadBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (IsPlaying)
            return "Stop playback to move a marker.";
        if (SelectedMarkerIds.Count is not (1 or 2))
            return "Select one or two markers (moves the nearest manual marker).";
        if (!TryGetNearestSelectedManualMarker(out var marker))
            return "Select at least one manual marker to move.";
        if (AbsDiff(marker.Time, Playhead) <= Timeline.MarkerEpsilon)
            return "Marker is already at the playhead.";

        return Timeline.GetMoveMarkerBlockReason(marker.Id, Playhead);
    }

    /// <summary>
    /// Among the current 1–2 selected markers, the manual marker closest to the playhead.
    /// </summary>
    private bool TryGetNearestSelectedManualMarker([NotNullWhen(true)] out Marker? marker)
    {
        marker = null;
        if (Timeline is null || SelectedMarkerIds.Count is not (1 or 2))
            return false;

        Marker? nearest = null;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var id in SelectedMarkerIds)
        {
            var candidate = Timeline.Markers.FirstOrDefault(m => m.Id == id);
            if (candidate is null || candidate.Source != MarkerSource.User)
                continue;

            var distance = AbsDiff(candidate.Time, Playhead);
            if (nearest is not null && distance >= bestDistance)
                continue;

            nearest = candidate;
            bestDistance = distance;
        }

        marker = nearest;
        return nearest is not null;
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

    private bool CanDeleteMarker() => GetDeleteMarkerBlockReason() is null;

    public string? DeleteMarkerToolTip => GetDeleteMarkerBlockReason();

    private string? GetDeleteMarkerBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (SelectedMarkerIds.Count != 1)
            return "Select a single marker to delete.";
        return Timeline.GetRemoveMarkerBlockReason(SelectedMarkerIds[0]);
    }

    [RelayCommand(CanExecute = nameof(CanDropSection))]
    private void DropSection()
    {
        if (Timeline is null || !TryGetDropRestoreRange(out var left, out var right))
            return;

        Apply(Timeline.DeleteBetween(left, right));
    }

    private bool CanDropSection() => GetDropSectionBlockReason() is null;

    public string? DropSectionToolTip => GetDropSectionBlockReason();

    private string? GetDropSectionBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (TryGetOrderedSelectedMarkerIds(out var left, out var right))
        {
            return Timeline.RangeContainsSegment(left, right, isDeleted: false)
                ? null
                : "The selected range is already dropped.";
        }

        if (SelectedMarkerIds.Count != 0)
            return "Select two markers, or clear selection and place the playhead between boundaries.";
        if (!Timeline.TryGetSurroundingBoundaries(Playhead, out left, out right))
            return "Move the playhead between boundaries (not on a marker) to drop that section.";
        if (!Timeline.RangeContainsSegment(left, right, isDeleted: false))
            return "The section under the playhead is already dropped.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSection))]
    private void RestoreSection()
    {
        if (Timeline is null)
            return;

        if (SelectedDeletedRunId is { } runId)
        {
            Apply(Timeline.RestoreDeletedRun(runId));
            SelectedDeletedRunId = null;
            return;
        }

        if (!TryGetDropRestoreRange(out var left, out var right))
            return;

        Apply(Timeline.RestoreBetween(left, right));
    }

    private bool CanRestoreSection() => GetRestoreSectionBlockReason() is null;

    public string? RestoreSectionToolTip => GetRestoreSectionBlockReason();

    private string? GetRestoreSectionBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (SelectedDeletedRunId is { } runId)
        {
            return Timeline.GetDeletedRuns().Any(r => r.Id == runId)
                ? null
                : "Select a deleted section, or place the playhead in one.";
        }

        if (TryGetOrderedSelectedMarkerIds(out var left, out var right))
        {
            return Timeline.RangeContainsSegment(left, right, isDeleted: true)
                ? null
                : "The selected range has nothing deleted to restore.";
        }

        if (SelectedMarkerIds.Count != 0)
            return "Select a deleted section, two markers that bound one, or clear selection and place the playhead in a dropped section.";
        if (!Timeline.TryGetSurroundingBoundaries(Playhead, out left, out right))
            return "Move the playhead into a dropped section (not on a marker) to restore it.";
        if (!Timeline.RangeContainsSegment(left, right, isDeleted: true))
            return "The section under the playhead is not dropped.";
        return null;
    }

    /// <summary>
    /// Two selected markers, or (with empty selection) the boundaries surrounding the playhead.
    /// </summary>
    private bool TryGetDropRestoreRange(out MarkerId left, out MarkerId right)
    {
        if (TryGetOrderedSelectedMarkerIds(out left, out right))
            return true;

        left = default;
        right = default;
        return Timeline is not null
            && SelectedMarkerIds.Count == 0
            && Timeline.TryGetSurroundingBoundaries(Playhead, out left, out right);
    }

    [RelayCommand(CanExecute = nameof(CanDropSectionFromStart))]
    private void DropSectionFromStart()
    {
        if (Timeline is null || !TryGetLeftmostSelectedMarkerId(out var left))
            return;

        Apply(Timeline.DeleteBetween(MarkerId.StartBound, left));
    }

    private bool CanDropSectionFromStart() => GetDropSectionFromStartBlockReason() is null;

    public string? DropSectionFromStartToolTip => GetDropSectionFromStartBlockReason();

    private string? GetDropSectionFromStartBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (!TryGetLeftmostSelectedMarkerId(out var left))
            return "Select one or two markers.";
        if (!Timeline.RangeContainsSegment(MarkerId.StartBound, left, isDeleted: false))
            return "Everything before the selection is already dropped.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanDropSectionToEnd))]
    private void DropSectionToEnd()
    {
        if (Timeline is null || !TryGetRightmostSelectedMarkerId(out var right))
            return;

        Apply(Timeline.DeleteBetween(right, MarkerId.EndBound));
    }

    private bool CanDropSectionToEnd() => GetDropSectionToEndBlockReason() is null;

    public string? DropSectionToEndToolTip => GetDropSectionToEndBlockReason();

    private string? GetDropSectionToEndBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (!TryGetRightmostSelectedMarkerId(out var right))
            return "Select one or two markers.";
        if (!Timeline.RangeContainsSegment(right, MarkerId.EndBound, isDeleted: false))
            return "Everything after the selection is already dropped.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSectionFromStart))]
    private void RestoreSectionFromStart()
    {
        if (Timeline is null || !TryGetLeftmostSelectedMarkerId(out var left))
            return;

        Apply(Timeline.RestoreBetween(MarkerId.StartBound, left));
    }

    private bool CanRestoreSectionFromStart() => GetRestoreSectionFromStartBlockReason() is null;

    public string? RestoreSectionFromStartToolTip => GetRestoreSectionFromStartBlockReason();

    private string? GetRestoreSectionFromStartBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (!TryGetLeftmostSelectedMarkerId(out var left))
            return "Select one or two markers.";
        if (!Timeline.RangeContainsSegment(MarkerId.StartBound, left, isDeleted: true))
            return "Nothing before the selection is deleted.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSectionToEnd))]
    private void RestoreSectionToEnd()
    {
        if (Timeline is null || !TryGetRightmostSelectedMarkerId(out var right))
            return;

        Apply(Timeline.RestoreBetween(right, MarkerId.EndBound));
    }

    private bool CanRestoreSectionToEnd() => GetRestoreSectionToEndBlockReason() is null;

    public string? RestoreSectionToEndToolTip => GetRestoreSectionToEndBlockReason();

    private string? GetRestoreSectionToEndBlockReason()
    {
        if (Timeline is null)
            return "Open a video to begin.";
        if (!TryGetRightmostSelectedMarkerId(out var right))
            return "Select one or two markers.";
        if (!Timeline.RangeContainsSegment(right, MarkerId.EndBound, isDeleted: true))
            return "Nothing after the selection is deleted.";
        return null;
    }

    private bool TryGetOrderedSelectedMarkerIds(out MarkerId left, out MarkerId right)
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

    private bool TryGetLeftmostSelectedMarkerId(out MarkerId id)
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

    private bool TryGetRightmostSelectedMarkerId(out MarkerId id)
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
    private void SelectDeletedRun(DeletedRunId runId)
    {
        SelectedDeletedRunId = runId;
        SelectedMarkerIds.Clear();
        SyncFocusFromPlayhead();
        RefreshSelectionFromTimeline();
    }

    partial void OnSelectedCrossfadeSecondsChanged(double value) => CommitJoinCrossfade(value);

    partial void OnFadeFromBlackSecondsChanged(double value) => CommitStartFade(value);

    partial void OnFadeToBlackSecondsChanged(double value) => CommitEndFade(value);

    partial void OnSelectedMarkerLabelChanged(string value) => CommitMarkerLabel(value);

    private void SyncSelectedMarkerLabelUi()
    {
        OnPropertyChanged(nameof(HasSingleSelectedMarker));
        var label = "";
        if (Timeline is not null && SelectedMarkerIds.Count == 1)
        {
            var id = SelectedMarkerIds[0];
            var marker = Timeline.Markers.FirstOrDefault(m => m.Id == id);
            if (marker is not null)
                label = marker.Label;
        }

        if (SelectedMarkerLabel == label)
            return;

        _syncingSidebarUi = true;
        try
        {
            SelectedMarkerLabel = label;
        }
        finally
        {
            _syncingSidebarUi = false;
        }
    }

    private void CommitMarkerLabel(string value)
    {
        if (_syncingSidebarUi || Timeline is null || SelectedMarkerIds.Count != 1)
            return;

        var id = SelectedMarkerIds[0];
        var normalized = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        var marker = Timeline.Markers.FirstOrDefault(m => m.Id == id);
        if (marker is null)
            return;

        if (marker.Label == normalized)
        {
            if (SelectedMarkerLabel != normalized)
            {
                _syncingSidebarUi = true;
                try { SelectedMarkerLabel = normalized; }
                finally { _syncingSidebarUi = false; }
            }

            return;
        }

        if (SelectedMarkerLabel != normalized)
        {
            _syncingSidebarUi = true;
            try { SelectedMarkerLabel = normalized; }
            finally { _syncingSidebarUi = false; }
        }

        Apply(Timeline.SetMarkerLabel(id, normalized));
    }

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
            if (join.Mode == TransitionMode.HardCut && join.Crossfade <= TimeSpan.Zero)
                return;
            Apply(Timeline.SetJoinHardCut(runId));
            return;
        }

        var crossfade = TimeSpan.FromSeconds(seconds);
        if (join.Mode == TransitionMode.Crossfade
            && Math.Abs(join.Crossfade.TotalSeconds - seconds) < 1e-9)
            return;

        Apply(Timeline.SetJoinCrossfade(runId, crossfade));
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

        await RunSceneDetectInBackgroundAsync(VideoPath, Timeline.Duration, openPane: true)
            .ConfigureAwait(true);
    }

    private bool CanDetectScenes() => GetDetectScenesBlockReason() is null;

    public string? DetectScenesToolTip => GetDetectScenesBlockReason();

    private string? GetDetectScenesBlockReason()
    {
        if (VideoPath is null || Timeline is null)
            return "Open a video to begin.";
        if (IsDetectingScenes)
            return "Scene detection is already running.";
        return null;
    }

    private bool CanExport() => GetExportBlockReason() is null;

    public string? ExportToolTip => GetExportBlockReason();

    private string? GetExportBlockReason()
    {
        if (VideoPath is null)
            return "Open a video to begin.";
        if (!HasKeptMaterial())
            return "There is no kept material to export.";
        return null;
    }

    private bool HasKeptMaterial() =>
        Timeline is not null && Timeline.GetKeptRuns().Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (Timeline is null || VideoPath is null)
            return;

        ExportPlan? plan = null;
        Timeline.TryBuildExportPlan().Match(
            value => plan = value,
            _ => { });
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
        var task = CreateTask($"Export: {Path.GetFileName(outputPath)}", openPane: true);
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
        NotifyTasksPaneChrome();

        try
        {
            var progress = new Progress<double>(p => task.Progress = p);
            await _exporter.ExportAsync(
                VideoPath,
                outputPath,
                plan,
                progress,
                onLogLine: task.AppendLog,
                cancellationToken: _exportCts.Token).ConfigureAwait(true);
            task.Progress = 1;
            task.IsCompleted = true;
            task.CanCancel = false;
            task.HasResultActions = true;
            task.Title = $"Exported: {Path.GetFileName(outputPath)}";
            NotifyTasksPaneChrome();
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
        if (IsPreviewMode && _previewMap is not null)
        {
            // Preview transport is abridged-clock; map kept source times into the compositor.
            if (Timeline is not null)
                time = Timeline.SnapToNearestKept(time);
            SeekAbridgedPlayhead(_previewMap.ToAbridged(time));
            return;
        }

        Playhead = time;
        SyncAbridgedPlayheadFromSource(time);
        GateSeek(time);
        _player.Seek(time);
    }

    /// <summary>Preview timeline seek: <paramref name="abridged"/> is export-clock time.</summary>
    public void SeekAbridgedPlayhead(TimeSpan abridged)
    {
        if (_previewMap is null || _previewPlan is null || Timeline is null)
            return;

        if (abridged < TimeSpan.Zero)
            abridged = TimeSpan.Zero;
        else if (abridged > _previewMap.AbridgedDuration)
            abridged = _previewMap.AbridgedDuration;

        var seek = PreviewSeekState.FromAbridged(_previewPlan, _previewMap, abridged);
        AbridgedPlayhead = seek.AbridgedTime;
        Playhead = seek.PlayheadSourceTime;
        if (IsPlaying)
        {
            _compositorPlayOriginUtc = DateTime.UtcNow;
            _compositorPlayOriginAbridged = seek.AbridgedTime;
        }

        ApplyCompositor(seek, play: IsPlaying);
    }

    private void SyncAbridgedPlayheadFromSource(TimeSpan sourceTime)
    {
        if (_previewMap is null)
            return;
        AbridgedPlayhead = _previewMap.ToAbridged(sourceTime);
    }

    /// <summary>Edit-mode HWND playhead only. Preview is driven by <see cref="OnPreviewClockTick"/>.</summary>
    private void HandlePlayerTime(TimeSpan time)
    {
        Playhead = time;
    }

    private void EnterRealtimePreview(bool restoreSavedView)
    {
        if (Timeline is null || VideoPath is null)
        {
            IsPreviewMode = false;
            return;
        }

        if (IsPlaying)
            PausePlayback();

        // Keep HWND loaded (Edit return) but mute/cover it; Preview paints dual bitmaps.
        try
        {
            _player.Load(VideoPath);
            _player.SetVolume(0);
            _player.Pause();
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

        DisposePreviewLayers();
        PreviewBackdropOpacity = 1;
        EnsurePreviewLayers();
        RefreshRealtimePreviewModel(seekToPlayhead: !restoreSavedView);
    }

    private void ExitRealtimePreview()
    {
        DisposePreviewLayers();
        _previewPlan = null;
        _previewMap = null;
        _abridgedLayout = null;
        OnPropertyChanged(nameof(AbridgedLayout));
        OnPropertyChanged(nameof(PreviewExportPlan));
        OnPropertyChanged(nameof(PreviewTimeMap));
        _seekGateTarget = null;
        _compositorInCrossfade = false;
        _lastCompositorOutgoingSeek = TimeSpan.MinValue;
        _outgoingIsLayerA = true;
        PreviewBackdropOpacity = 0;
        PreviewBlackoutOpacity = 0;
        PreviewLayerAOpacity = 0;
        PreviewLayerBOpacity = 0;
        PreviewLayerAFrame = null;
        PreviewLayerBFrame = null;
        _player.SetVolume(100);
        SyncPreviewClockTimer();
        RefreshStatusBar();
    }

    private void RefreshRealtimePreviewModel(bool seekToPlayhead)
    {
        if (!IsPreviewMode || Timeline is null)
            return;

        ExportPlan? plan = null;
        Timeline.TryBuildExportPlan().Match(
            value => plan = value,
            _ => { });
        if (plan is null)
        {
            IsPreviewMode = false;
            return;
        }

        _previewPlan = plan;
        _previewMap = PreviewTimeMap.FromPlan(_previewPlan);
        _abridgedLayout = AbridgedTimelineLayout.FromTimeline(Timeline);
        OnPropertyChanged(nameof(AbridgedLayout));
        OnPropertyChanged(nameof(PreviewExportPlan));
        OnPropertyChanged(nameof(PreviewTimeMap));
        _compositorInCrossfade = false;
        _lastCompositorOutgoingSeek = TimeSpan.MinValue;
        _outgoingIsLayerA = true;

        if (seekToPlayhead)
        {
            var snapped = Timeline.SnapToNearestKept(Playhead);
            SeekAbridgedPlayhead(_previewMap.ToAbridged(snapped));
        }
        else
        {
            SeekAbridgedPlayhead(AbridgedPlayhead);
        }
    }

    /// <summary>
    /// Dual bitmap compositor: outgoing at (1−u), incoming at u. No visible layer ever seeks
    /// across a deleted gap — hard cuts swap to a pre-parked idle layer; XF ends by fading.
    /// </summary>
    private void ApplyCompositor(PreviewSeekState seek, bool play)
    {
        EnsurePreviewLayers();
        if (_previewLayerA is not { } layerA || _previewLayerB is not { } layerB)
            return;

        PreviewBlackoutOpacity = seek.BlackoutOpacity;
        var audible = Math.Clamp(1.0 - seek.BlackoutOpacity, 0, 1);

        if (seek.InCrossfade)
        {
            var outgoing = _outgoingIsLayerA ? layerA : layerB;
            var incoming = _outgoingIsLayerA ? layerB : layerA;
            SeekLayer(outgoing, seek.PrimarySourceTime);
            SeekLayer(incoming, seek.IncomingSourceTime);
            SetLayerOpacity(isLayerA: _outgoingIsLayerA, 1.0 - seek.IncomingOpacity);
            SetLayerOpacity(isLayerA: !_outgoingIsLayerA, seek.IncomingOpacity);
            outgoing.SetVolume((int)Math.Round(100 * audible * (1.0 - seek.IncomingOpacity)));
            incoming.SetVolume((int)Math.Round(100 * audible * seek.IncomingOpacity));
            if (play)
            {
                TryLayerPlay(outgoing);
                TryLayerPlay(incoming);
            }
            else
            {
                TryLayerPause(outgoing);
                TryLayerPause(incoming);
            }

            _compositorInCrossfade = true;
            _lastCompositorOutgoingSeek = seek.PrimarySourceTime;
            return;
        }

        // Solo (or hard cut): one visible layer at opacity 1.
        if (_compositorInCrossfade)
        {
            // XF just ended — promote the incoming layer (already on the next clip) to active.
            _outgoingIsLayerA = !_outgoingIsLayerA;
            _compositorInCrossfade = false;
        }
        else if (_lastCompositorOutgoingSeek != TimeSpan.MinValue
                 && AbsDiff(seek.PrimarySourceTime, _lastCompositorOutgoingSeek) > TimeSpan.FromSeconds(0.25))
        {
            // Hard cut / large teleport: park on the idle layer, then swap — never seek the
            // visible surface across the deleted gap.
            var wasOutgoingA = _outgoingIsLayerA;
            var idle = wasOutgoingA ? layerB : layerA;
            var oldOutgoing = wasOutgoingA ? layerA : layerB;
            SeekLayer(idle, seek.PrimarySourceTime);
            SetLayerOpacity(isLayerA: !wasOutgoingA, 1);
            SetLayerOpacity(isLayerA: wasOutgoingA, 0);
            TryLayerPause(oldOutgoing);
            oldOutgoing.SetVolume(0);
            idle.SetVolume((int)Math.Round(100 * audible));
            _outgoingIsLayerA = !wasOutgoingA;
            if (play)
                TryLayerPlay(idle);
            else
                TryLayerPause(idle);

            _lastCompositorOutgoingSeek = seek.PrimarySourceTime;
            return;
        }

        var active = _outgoingIsLayerA ? layerA : layerB;
        var other = _outgoingIsLayerA ? layerB : layerA;
        SeekLayer(active, seek.PrimarySourceTime);
        SetLayerOpacity(isLayerA: _outgoingIsLayerA, 1);
        SetLayerOpacity(isLayerA: !_outgoingIsLayerA, 0);
        active.SetVolume((int)Math.Round(100 * audible));
        other.SetVolume(0);
        TryLayerPause(other);
        if (play)
            TryLayerPlay(active);
        else
            TryLayerPause(active);

        _lastCompositorOutgoingSeek = seek.PrimarySourceTime;
    }

    private void SetLayerOpacity(bool isLayerA, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (isLayerA)
            PreviewLayerAOpacity = opacity;
        else
            PreviewLayerBOpacity = opacity;
    }

    private static void SeekLayer(FrameVideoPlayer layer, TimeSpan sourceTime) =>
        layer.Seek(sourceTime);

    private void EnsurePreviewLayers()
    {
        if (VideoPath is null)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        var path = VideoPath;
        _previewLayerA ??= CreatePreviewLayer(dispatcher, path, onFrame: bmp => PreviewLayerAFrame = bmp, ReportError);
        _previewLayerB ??= CreatePreviewLayer(dispatcher, path, onFrame: bmp => PreviewLayerBFrame = bmp, ReportError);
        PreviewLayerAFrame ??= _previewLayerA.Bitmap;
        PreviewLayerBFrame ??= _previewLayerB.Bitmap;
    }

    private static FrameVideoPlayer CreatePreviewLayer(
        Dispatcher dispatcher,
        string path,
        Action<ImageSource?> onFrame,
        Action<string> reportError)
    {
        var layer = new FrameVideoPlayer(dispatcher);
        layer.FrameUpdated += (_, _) => onFrame(layer.Bitmap);
        try
        {
            layer.Load(path);
            layer.SetVolume(0);
        }
        catch (InvalidOperationException ex)
        {
            reportError($"Preview layer failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            reportError($"Preview layer failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            reportError($"Preview layer failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            reportError($"Preview layer failed: {ex.Message}");
        }
        catch (COMException ex)
        {
            reportError($"Preview layer failed: {ex.Message}");
        }

        return layer;
    }

    private void DisposePreviewLayers()
    {
        TryLayerPause(_previewLayerA);
        TryLayerPause(_previewLayerB);
        _previewLayerA?.Dispose();
        _previewLayerB?.Dispose();
        _previewLayerA = null;
        _previewLayerB = null;
    }

    private void SyncPreviewClockTimer()
    {
        _previewClockTimer ??= CreatePreviewClockTimer();
        var fps = _frameRate > 1e-6 ? _frameRate : 30;
        var hz = Math.Clamp(fps, 24, 60);
        _previewClockTimer.Interval = TimeSpan.FromSeconds(1.0 / hz);

        var run = IsPreviewMode && IsPlaying;
        if (run)
            _previewClockTimer.Start();
        else
            _previewClockTimer.Stop();
    }

    private DispatcherTimer CreatePreviewClockTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 30),
        };
        timer.Tick += OnPreviewClockTick;
        return timer;
    }

    private void OnPreviewClockTick(object? sender, EventArgs e)
    {
        if (!IsPreviewMode || !IsPlaying || _previewPlan is null || _previewMap is null)
            return;

        var elapsed = DateTime.UtcNow - _compositorPlayOriginUtc;
        var abridged = _compositorPlayOriginAbridged + elapsed;
        if (abridged >= _previewMap.AbridgedDuration)
        {
            abridged = _previewMap.AbridgedDuration;
            var end = PreviewSeekState.FromAbridged(_previewPlan, _previewMap, abridged);
            AbridgedPlayhead = end.AbridgedTime;
            Playhead = end.PlayheadSourceTime;
            ApplyCompositor(end, play: false);
            PausePlayback();
            return;
        }

        var seek = PreviewSeekState.FromAbridged(_previewPlan, _previewMap, abridged);
        AbridgedPlayhead = seek.AbridgedTime;
        Playhead = seek.PlayheadSourceTime;
        ApplyCompositor(seek, play: true);
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
        if (SelectedDeletedRunId is { } runId
            && timeline.GetDeletedRuns().All(r => r.Id != runId))
        {
            SelectedDeletedRunId = null;
        }

        NotifyTimelineActionCommands();
        RefreshStatusBar();
        PersistMarkerCache(force: true);
    }

    private void SetActiveTimeline(Timeline timeline)
    {
        if (_versionSet is null)
        {
            Timeline = timeline;
            return;
        }

        _versionSet.ReplaceActiveTimeline(timeline).Match(
            set =>
            {
                _versionSet = set;
                Timeline = timeline;
            },
            error => ReportError(error.Message));
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
        SyncFocusFromPlayhead();
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
        if (_versionSet is null)
        {
            VersionTabs.Clear();
            OnPropertyChanged(nameof(ShowVersionTabs));
            OnPropertyChanged(nameof(HasVideoLoaded));
            return;
        }

        var versions = _versionSet.Versions;
        var activeId = _versionSet.ActiveVersionId;

        for (var i = VersionTabs.Count - 1; i >= 0; i--)
        {
            if (versions.All(v => v.Id != VersionTabs[i].Id))
                VersionTabs.RemoveAt(i);
        }

        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            var isActive = version.Id == activeId;
            var existingIndex = -1;
            for (var j = 0; j < VersionTabs.Count; j++)
            {
                if (VersionTabs[j].Id == version.Id)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                VersionTabs.Insert(i, new VersionTabViewModel(version.Id, version.Name, isActive));
                continue;
            }

            var tab = VersionTabs[existingIndex];
            if (!tab.IsEditing)
                tab.Name = version.Name;
            tab.IsActive = isActive;
            if (existingIndex != i)
                VersionTabs.Move(existingIndex, i);
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
        DeleteActiveVersionCommand.NotifyCanExecuteChanged();
        SelectVersionCommand.NotifyCanExecuteChanged();
        SelectNextVersionCommand.NotifyCanExecuteChanged();
        SelectPreviousVersionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CreateVersionToolTip));
        OnPropertyChanged(nameof(DuplicateVersionToolTip));
        OnPropertyChanged(nameof(RenameVersionToolTip));
        OnPropertyChanged(nameof(DeleteVersionToolTip));
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
        var id = MarkerId.New();
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
            _markerCache.SaveVersionSet(VideoPath, _versionSet, CaptureUiCacheState());
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

    private UiCacheState CaptureUiCacheState()
    {
        if (!_suppressViewCapture)
            CaptureActiveViewState();
        return new UiCacheState
        {
            IsPreviewMode = IsPreviewMode,
            IsTasksPaneOpen = IsTasksPaneOpen,
            Window = _windowPlacement,
            EditView = CloneView(_editView),
            PreviewView = CloneView(_previewView),
            PixelsPerSecond = _editView.PixelsPerSecond
        };
    }

    private void CaptureActiveViewState()
    {
        if (_suppressViewCapture)
            return;

        if (IsPreviewMode)
            _previewView = CaptureViewState(previewMode: true);
        else
            _editView = CaptureViewState(previewMode: false);
    }

    private TimelineViewStateDto CaptureViewState(bool previewMode) =>
        new()
        {
            PixelsPerSecond = PixelsPerSecond,
            VisibleStartSeconds = _visibleStartSeconds,
            PlayheadSeconds = previewMode
                ? AbridgedPlayhead.TotalSeconds
                : Playhead.TotalSeconds
        };

    private static TimelineViewStateDto? CloneView(TimelineViewStateDto? view) =>
        view is null
            ? null
            : new TimelineViewStateDto
            {
                PixelsPerSecond = view.PixelsPerSecond,
                VisibleStartSeconds = view.VisibleStartSeconds,
                PlayheadSeconds = view.PlayheadSeconds
            };

    private void ApplyViewState(TimelineViewStateDto? state, bool previewMode)
    {
        _suppressViewCapture = true;

        if (state is null)
        {
            // First visit to Preview: fit the abridged timeline once, then remember it.
            if (previewMode)
            {
                PendingVisibleStartSeconds = 0;
                OnPropertyChanged(nameof(PendingVisibleStartSeconds));
                PendingForceTimelineFit = true;
                OnPropertyChanged(nameof(PendingForceTimelineFit));
            }
            else
            {
                _suppressViewCapture = false;
            }

            return;
        }

        if (state.PixelsPerSecond > 0)
            PixelsPerSecond = Math.Clamp(state.PixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond);

        _visibleStartSeconds = Math.Max(0, state.VisibleStartSeconds);
        PendingVisibleStartSeconds = _visibleStartSeconds;
        OnPropertyChanged(nameof(PendingVisibleStartSeconds));

        var playhead = TimeSpan.FromSeconds(Math.Max(0, state.PlayheadSeconds));
        if (previewMode)
        {
            if (_previewMap is not null)
                SeekAbridgedPlayhead(playhead);
            else
                AbridgedPlayhead = playhead;
        }
        else
        {
            SeekPlayhead(playhead);
        }

        // Stay suppressed until MainWindow applies scroll (ClearPendingTimelineViewRequests).
    }

    public bool PendingForceTimelineFit { get; private set; }

    public void ClearPendingTimelineViewRequests()
    {
        PendingVisibleStartSeconds = null;
        PendingForceTimelineFit = false;
        _suppressViewCapture = false;
        CaptureActiveViewState();
    }

    private (AbridgedVersionSet VersionSet, MarkerCacheEntry? Entry) RestoreFromCache(
        TimeSpan duration,
        string path)
    {
        var entry = _markerCache.TryLoad(path, duration);
        if (entry is null)
            return (CreateDefaultVersionSet(duration), null);

        var set = MarkerCacheService.TryToVersionSet(entry, duration);
        return set is null
            ? (CreateDefaultVersionSet(duration), null)
            : (set, entry);
    }

    private static AbridgedVersionSet CreateDefaultVersionSet(TimeSpan duration) =>
        AbridgedVersionSet.CreateDefault(duration).Match(
            set => set,
            error => throw new InvalidOperationException($"{error.Code}: {error.Message}"));

    private void ApplyUiStateFromCache(MarkerCacheEntry? entry)
    {
        _restoringUiFromCache = true;
        try
        {
            _editView = entry?.EditView is { } edit
                ? new TimelineViewStateDto
                {
                    PixelsPerSecond = edit.PixelsPerSecond,
                    VisibleStartSeconds = edit.VisibleStartSeconds,
                    PlayheadSeconds = edit.PlayheadSeconds
                }
                : new TimelineViewStateDto
                {
                    PixelsPerSecond = entry?.PixelsPerSecond is { } legacy && legacy > 0 ? legacy : PixelsPerSecond,
                    VisibleStartSeconds = 0,
                    PlayheadSeconds = 0
                };
            _previewView = CloneView(entry?.PreviewView);

            if (entry?.IsTasksPaneOpen is { } open)
                IsTasksPaneOpen = open;

            _windowPlacement = entry?.Window is { } window
                ? new WindowPlacementDto
                {
                    Left = window.Left,
                    Top = window.Top,
                    Width = window.Width,
                    Height = window.Height,
                    State = window.State
                }
                : null;
            PendingWindowPlacement = _windowPlacement;
            OnPropertyChanged(nameof(PendingWindowPlacement));

            var wantPreview = entry?.IsPreviewMode == true && HasKeptMaterial();
            if (wantPreview)
            {
                IsPreviewMode = true;
                ApplyViewState(_previewView, previewMode: true);
            }
            else
            {
                if (IsPreviewMode)
                    IsPreviewMode = false;
                ApplyViewState(_editView, previewMode: false);
            }
        }
        finally
        {
            _restoringUiFromCache = false;
        }
    }

    private async Task EnsureMarkerThumbnailAsync(
        MarkerId markerId,
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

    private async Task RunSceneDetectInBackgroundAsync(
        string path,
        TimeSpan duration,
        bool openPane = false)
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
        var task = CreateTask(taskTitle, openPane);
        task.CanCancel = true;
        task.CancelCommand = new RelayCommand(CancelSceneDetect);
        NotifyTasksPaneChrome();
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
                onLogLine: task.AppendLog,
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
        CancelAudioPeaks();
        AudioPeakEnvelope = null;
        if (IsPreviewMode)
            IsPreviewMode = false;

        TimeSpan duration = default;
        try
        {
            IsBusy = true;
            var fullPath = Path.GetFullPath(path);
            var info = await _probe.ProbeAsync(fullPath).ConfigureAwait(true);
            duration = info.Duration;
            _frameRate = info.FrameRate > 1e-6 ? info.FrameRate : 30;
            SyncPreviewClockTimer();
            VideoPath = fullPath;
            _player.Load(fullPath);
            IsPlaying = false;

            var (versionSet, cacheEntry) = RestoreFromCache(info.Duration, fullPath);
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

            ClearMarkerSelection();
            MarkerThumbs.Clear();
            _editHistory.Clear();
            ApplyUiStateFromCache(cacheEntry);
            NotifyTimelineActionCommands();
            RememberRecentFile(fullPath);
            RefreshStatusBar();
            if (versionSet.ActiveTimeline.Markers.Count > 0)
                _ = RefreshThumbnailsAsync();
            _ = LoadAudioPeaksAsync(fullPath, duration);
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

    private void RememberRecentFile(string path)
    {
        try
        {
            RefreshRecentFiles(_recentFiles.Add(path));
        }
        catch (IOException)
        {
            // Recent-file persistence is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Recent-file persistence is best-effort.
        }
    }

    private void RefreshRecentFiles(IReadOnlyList<string>? paths = null)
    {
        var list = paths ?? _recentFiles.Load();
        RecentFiles.Clear();
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in list)
        {
            var fileName = FileNameOrPath(path);
            nameCounts[fileName] = nameCounts.GetValueOrDefault(fileName) + 1;
        }

        foreach (var path in list)
        {
            var fileName = FileNameOrPath(path);
            var displayName = nameCounts[fileName] > 1 ? path : fileName;
            RecentFiles.Add(new RecentFileItem(path, displayName));
        }

        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private static string FileNameOrPath(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void CancelAudioPeaks()
    {
        if (_audioPeaksCts is null)
            return;

        _audioPeaksCts.Cancel();
        _audioPeaksCts.Dispose();
        _audioPeaksCts = null;
    }

    private async Task LoadAudioPeaksAsync(string path, TimeSpan duration)
    {
        CancelAudioPeaks();
        AudioPeakEnvelope = null;
        var cts = new CancellationTokenSource();
        _audioPeaksCts = cts;

        var cached = _audioPeakCache.TryLoad(
            path,
            duration,
            AudioPeakExtractor.DefaultBucketDuration,
            AudioPeakExtractor.SampleRate);
        if (cached is { IsEmpty: false }
            && !cts.IsCancellationRequested
            && ReferenceEquals(_audioPeaksCts, cts))
        {
            AudioPeakEnvelope = cached;
            cts.Dispose();
            if (ReferenceEquals(_audioPeaksCts, cts))
                _audioPeaksCts = null;
            return;
        }

        var task = CreateTask($"Waveform: {Path.GetFileName(path)}");
        task.CanCancel = true;
        task.CancelCommand = new RelayCommand(CancelAudioPeaks);
        NotifyTasksPaneChrome();
        try
        {
            var progress = new Progress<double>(p => task.Progress = p);
            var envelope = await _audioPeaks.ExtractAsync(
                    path,
                    duration: duration,
                    progress: progress,
                    onLogLine: task.AppendLog,
                    cancellationToken: cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(_audioPeaksCts, cts))
            {
                RemoveTask(task);
                return;
            }

            if (envelope.IsEmpty)
            {
                AudioPeakEnvelope = null;
            }
            else
            {
                AudioPeakEnvelope = envelope;
                _audioPeakCache.Save(path, duration, envelope, AudioPeakExtractor.SampleRate);
            }

            RemoveTask(task);
        }
        catch (OperationCanceledException)
        {
            RemoveTask(task);
        }
        catch (InvalidOperationException)
        {
            RemoveTask(task);
            if (!cts.IsCancellationRequested && ReferenceEquals(_audioPeaksCts, cts))
                AudioPeakEnvelope = null;
        }
        catch (IOException)
        {
            RemoveTask(task);
            if (!cts.IsCancellationRequested && ReferenceEquals(_audioPeaksCts, cts))
                AudioPeakEnvelope = null;
        }
        finally
        {
            if (ReferenceEquals(_audioPeaksCts, cts))
            {
                cts.Dispose();
                _audioPeaksCts = null;
            }
        }
    }

    private LongTaskItem CreateTask(string title, bool openPane = false)
    {
        var task = new LongTaskItem
        {
            Title = title,
            Progress = 0,
            HasResultActions = false
        };
        Tasks.Add(task);
        if (openPane)
            IsTasksPaneOpen = true;
        NotifyTasksPaneChrome();
        return task;
    }

    private void RemoveTask(LongTaskItem task)
    {
        void Remove()
        {
            Tasks.Remove(task);
            NotifyTasksPaneChrome();
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Remove();
        else
            Application.Current?.Dispatcher.Invoke(Remove);
    }

    private void DismissTask(LongTaskItem task) => RemoveTask(task);

    private void NotifyTasksPaneChrome()
    {
        OnPropertyChanged(nameof(RunningTaskCount));
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(TasksPaneCollapsedLabel));
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

        var (kept, deletedRuns) = Timeline.GetKeptAndDeletedRuns();
        var run = deletedRuns.FirstOrDefault(r => r.Id == SelectedDeletedRunId);
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
                ? run.Join.Crossfade.TotalSeconds
                : 0;
            SelectedCrossfadeCap = Timeline.GetCrossfadeCap(run.Range, kept).TotalSeconds;
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

    private static BitmapImage? LoadBitmap(string? path, int decodePixelWidth = 120)
    {
        if (path is null || !File.Exists(path))
            return null;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Sidebar selection strip: on-demand before/after frames for the current selection.</summary>
    private async Task RefreshSelectionPreviewAsync()
    {
        if (_selectionPreviewCts is not null)
        {
            await _selectionPreviewCts.CancelAsync().ConfigureAwait(false);
            _selectionPreviewCts.Dispose();
            _selectionPreviewCts = null;
        }

        if (!TryResolveSelectionPreview(
                out var leftTime,
                out var leftSide,
                out var rightTime,
                out var rightSide))
        {
            await Application.Current.Dispatcher.InvokeAsync(ClearSelectionPreview).Task.ConfigureAwait(false);
            return;
        }

        var path = VideoPath;
        var duration = Timeline?.Duration ?? TimeSpan.Zero;
        if (path is null || duration <= TimeSpan.Zero)
        {
            await Application.Current.Dispatcher.InvokeAsync(ClearSelectionPreview).Task.ConfigureAwait(false);
            return;
        }

        var cts = new CancellationTokenSource();
        _selectionPreviewCts = cts;
        _selectionPreviewLeftSeek = leftTime;
        _selectionPreviewRightSeek = rightTime;

        // Keep prior frames visible until both replacements are ready (no clear/flicker mid-move).

        try
        {
            var leftPath = await _thumbnails
                .ExtractAsync(path, leftTime, leftSide, duration, cts.Token)
                .ConfigureAwait(false);
            var rightPath = await _thumbnails
                .ExtractAsync(path, rightTime, rightSide, duration, cts.Token)
                .ConfigureAwait(false);

            if (cts.IsCancellationRequested)
                return;

            // Sidebar tiles are much larger than timeline thumbs (120px decode).
            const int sidebarDecodeWidth = 480;
            var leftImage = LoadBitmap(leftPath, sidebarDecodeWidth);
            var rightImage = LoadBitmap(rightPath, sidebarDecodeWidth);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested)
                    return;
                SelectionPreviewLeftImage = leftImage;
                SelectionPreviewRightImage = rightImage;
                HasSelectionPreview = true;
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
    }

    private void ClearSelectionPreview()
    {
        HasSelectionPreview = false;
        SelectionPreviewLeftImage = null;
        SelectionPreviewRightImage = null;
        _selectionPreviewLeftSeek = default;
        _selectionPreviewRightSeek = default;
    }

    private bool TryResolveSelectionPreview(
        out TimeSpan leftTime,
        out ThumbnailSide leftSide,
        out TimeSpan rightTime,
        out ThumbnailSide rightSide)
    {
        leftTime = default;
        rightTime = default;
        leftSide = ThumbnailSide.Before;
        rightSide = ThumbnailSide.After;

        if (IsPreviewMode || Timeline is null || VideoPath is null)
            return false;

        if (SelectedDeletedRunId is { } runId)
        {
            var run = Timeline.GetDeletedRuns().FirstOrDefault(r => r.Id == runId);
            if (run is null)
                return false;

            leftTime = run.Range.Start;
            leftSide = ThumbnailSide.Before;
            rightTime = run.Range.End;
            rightSide = ThumbnailSide.After;
            return true;
        }

        if (SelectedMarkerIds.Count == 2
            && TryGetOrderedSelectedMarkerIds(out var leftId, out var rightId))
        {
            var leftMarker = Timeline.Markers.FirstOrDefault(m => m.Id == leftId);
            var rightMarker = Timeline.Markers.FirstOrDefault(m => m.Id == rightId);
            if (leftMarker is null || rightMarker is null)
                return false;

            leftTime = leftMarker.Time;
            leftSide = ThumbnailSide.After;
            rightTime = rightMarker.Time;
            rightSide = ThumbnailSide.Before;
            return true;
        }

        if (SelectedMarkerIds.Count == 1)
        {
            var id = SelectedMarkerIds[0];
            var marker = Timeline.Markers.FirstOrDefault(m => m.Id == id);
            if (marker is null)
                return false;

            leftTime = marker.Time;
            leftSide = ThumbnailSide.Before;
            rightTime = marker.Time;
            rightSide = ThumbnailSide.After;
            return true;
        }

        return false;
    }

    /// <summary>Seek-and-pause to the left or right selection-preview boundary.</summary>
    public void SeekSelectionPreview(bool left)
    {
        if (!HasSelectionPreview || IsPreviewMode)
            return;

        PauseIfPlaying();
        SeekPlayhead(left ? _selectionPreviewLeftSeek : _selectionPreviewRightSeek);
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
            CancelAudioPeaks();
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

            if (_selectionPreviewCts is not null)
            {
                _selectionPreviewCts.CancelAsync().GetAwaiter().GetResult();
                try
                {
                    _selectionPreviewCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }

                _selectionPreviewCts = null;
            }

            if (_previewClockTimer is not null)
            {
                _previewClockTimer.Stop();
                _previewClockTimer.Tick -= OnPreviewClockTick;
                _previewClockTimer = null;
            }

            DisposePreviewLayers();

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
