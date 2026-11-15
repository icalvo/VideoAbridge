using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoAbridge.Core;
using VideoAbridge.FFmpeg;
using VideoAbridge.Services;

namespace VideoAbridge.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly VideoPlayerService _player = new();
    private readonly MediaProbe _probe = new();
    private readonly SceneDetector _sceneDetector = new();
    private readonly ThumbnailExtractor _thumbnails = new();
    private readonly VideoExporter _exporter = new();
    private CancellationTokenSource? _exportCts;

    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private Timeline? _timeline;
    [ObservableProperty] private TimeSpan _playhead;
    [ObservableProperty] private string _statusMessage = "Open a video to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private Guid? _selectedMarkerId;
    [ObservableProperty] private Guid? _selectedDeletedRunId;
    [ObservableProperty] private Guid? _rangeAnchorId;
    [ObservableProperty] private bool _selectedIsCrossfade;
    [ObservableProperty] private double _selectedCrossfadeSeconds;
    [ObservableProperty] private double _selectedCrossfadeCap;
    [ObservableProperty] private bool _fadeToBlackEnabled;
    [ObservableProperty] private double _fadeToBlackSeconds;
    [ObservableProperty] private double _pixelsPerSecond = 40;
    [ObservableProperty] private bool _isPlaying;

    public VideoPlayerService Player => _player;

    public ObservableCollection<MarkerThumbViewModel> MarkerThumbs { get; } = [];

    public MainViewModel()
    {
        _player.TimeChanged += (_, time) =>
        {
            Application.Current.Dispatcher.Invoke(() => Playhead = time);
        };
    }

    partial void OnTimelineChanged(Timeline? value)
    {
        if (value is null)
            return;

        FadeToBlackEnabled = value.Settings.FadeToBlackAtEnd;
        FadeToBlackSeconds = value.Settings.FadeToBlackSeconds;
        _ = RefreshThumbnailsAsync();
        RefreshSelectionFromTimeline();
    }

    partial void OnSelectedDeletedRunIdChanged(Guid? value) => RefreshSelectionFromTimeline();

    [RelayCommand]
    private async Task OpenVideoAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|All files|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        await LoadVideoAsync(dialog.FileName, runSceneDetect: true);
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var dialog = new OpenFileDialog { Filter = "VideoAbridge project|*.vabproj|JSON|*.json" };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            IsBusy = true;
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var docResult = ProjectDocument.Deserialize(json);
            if (!docResult.IsSuccess)
            {
                StatusMessage = docResult.Error!.Value.Message;
                return;
            }

            var timelineResult = docResult.Value!.ToTimeline();
            if (!timelineResult.IsSuccess)
            {
                StatusMessage = timelineResult.Error!.Value.Message;
                return;
            }

            var (path, timeline) = timelineResult.Value;
            if (!File.Exists(path))
            {
                StatusMessage = $"Video not found: {path}";
                return;
            }

            VideoPath = path;
            _player.Load(path);
            Timeline = timeline;
            StatusMessage = $"Loaded project with {timeline.Markers.Count} markers.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SaveProject()
    {
        if (Timeline is null || VideoPath is null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "VideoAbridge project|*.vabproj",
            FileName = Path.GetFileNameWithoutExtension(VideoPath) + ".vabproj"
        };
        if (dialog.ShowDialog() != true)
            return;

        var doc = ProjectDocument.FromTimeline(VideoPath, Timeline);
        File.WriteAllText(dialog.FileName, ProjectDocument.Serialize(doc));
        StatusMessage = "Project saved.";
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (Timeline is null)
            return;

        if (_player.IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }
        else
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    private void AddMarkerAtPlayhead()
    {
        if (Timeline is null)
            return;

        Apply(Timeline.AddMarker(Playhead, MarkerSource.User));
    }

    [RelayCommand]
    private void RemoveSelectedMarker()
    {
        if (Timeline is null || SelectedMarkerId is null)
            return;

        Apply(Timeline.RemoveMarker(SelectedMarkerId.Value));
        SelectedMarkerId = null;
    }

    [RelayCommand]
    private void SelectMarker(Guid markerId)
    {
        SelectedMarkerId = markerId;
        SelectedDeletedRunId = null;
        if (RangeAnchorId is null)
            RangeAnchorId = markerId;
    }

    [RelayCommand]
    private void SetRangeAnchor()
    {
        if (SelectedMarkerId is null)
            return;
        RangeAnchorId = SelectedMarkerId;
        StatusMessage = "Range anchor set. Select another marker and Delete/Restore Between.";
    }

    [RelayCommand]
    private void DeleteBetweenSelection()
    {
        if (Timeline is null || RangeAnchorId is null || SelectedMarkerId is null)
            return;

        Apply(OrderedRange(Timeline.DeleteBetween, RangeAnchorId.Value, SelectedMarkerId.Value));
    }

    [RelayCommand]
    private void RestoreBetweenSelection()
    {
        if (Timeline is null || RangeAnchorId is null || SelectedMarkerId is null)
            return;

        Apply(OrderedRange(Timeline.RestoreBetween, RangeAnchorId.Value, SelectedMarkerId.Value));
    }

    [RelayCommand]
    private void DeleteFromStartToSelection()
    {
        if (Timeline is null || SelectedMarkerId is null)
            return;
        Apply(Timeline.DeleteBetween(Timeline.StartBoundId, SelectedMarkerId.Value));
    }

    [RelayCommand]
    private void DeleteFromSelectionToEnd()
    {
        if (Timeline is null || SelectedMarkerId is null)
            return;
        Apply(Timeline.DeleteBetween(SelectedMarkerId.Value, Timeline.EndBoundId));
    }

    private Result<Timeline> OrderedRange(
        Func<Guid, Guid, Result<Timeline>> op,
        Guid a,
        Guid b)
    {
        if (!TryBoundaryTime(a, out var ta) || !TryBoundaryTime(b, out var tb))
            return Result<Timeline>.Fail("BoundaryNotFound", "One or both boundaries were not found.");

        return ta <= tb ? op(a, b) : op(b, a);
    }

    private bool TryBoundaryTime(Guid id, out TimeSpan time)
    {
        time = default;
        if (Timeline is null)
            return false;
        if (id == Timeline.StartBoundId)
        {
            time = TimeSpan.Zero;
            return true;
        }

        if (id == Timeline.EndBoundId)
        {
            time = Timeline.Duration;
            return true;
        }

        var marker = Timeline.Markers.FirstOrDefault(m => m.Id == id);
        if (marker is null)
            return false;
        time = marker.Time;
        return true;
    }

    [RelayCommand]
    private void SelectDeletedRun(Guid runId)
    {
        SelectedDeletedRunId = runId;
        SelectedMarkerId = null;
        RefreshSelectionFromTimeline();
    }

    [RelayCommand]
    private void ApplyHardCut()
    {
        if (Timeline is null || SelectedDeletedRunId is null)
            return;
        Apply(Timeline.SetJoinHardCut(SelectedDeletedRunId.Value));
        SelectedIsCrossfade = false;
    }

    [RelayCommand]
    private void ApplyCrossfade()
    {
        if (Timeline is null || SelectedDeletedRunId is null)
            return;

        var seconds = SelectedCrossfadeSeconds;
        if (seconds <= 0)
            seconds = Math.Min(1.0, SelectedCrossfadeCap);

        Apply(Timeline.SetJoinCrossfade(SelectedDeletedRunId.Value, seconds));
        SelectedIsCrossfade = true;
    }

    partial void OnSelectedCrossfadeSecondsChanged(double value)
    {
        if (Timeline is null || SelectedDeletedRunId is null || !SelectedIsCrossfade)
            return;

        // Debounced by user clicking Apply; keep property only. Use ApplyCrossfade command.
    }

    [RelayCommand]
    private void ApplyEndFade()
    {
        if (Timeline is null)
            return;
        Apply(Timeline.SetEndFade(FadeToBlackEnabled, FadeToBlackSeconds));
    }

    [RelayCommand]
    private async Task DetectScenesAsync()
    {
        if (VideoPath is null || Timeline is null)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Detecting scene cuts…";
            var times = await _sceneDetector.DetectAsync(VideoPath);
            var result = Timeline.AddMarkers(times.Select(t => (t, MarkerSource.SceneDetect)));
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error!.Value.Message;
                return;
            }

            Timeline = result.Value!;
            StatusMessage = $"Scene detect added markers (total {Timeline!.Markers.Count}).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Timeline is null || VideoPath is null)
            return;

        var planResult = Timeline.TryBuildExportPlan();
        if (!planResult.IsSuccess)
        {
            StatusMessage = planResult.Error!.Value.Message;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileNameWithoutExtension(VideoPath) + "_abridged.mp4"
        };
        if (dialog.ShowDialog() != true)
            return;

        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        StatusMessage = "Exporting…";

        try
        {
            var progress = new Progress<double>(p => ExportProgress = p);
            await _exporter.ExportAsync(VideoPath, dialog.FileName, planResult.Value!, progress, _exportCts.Token);
            StatusMessage = $"Exported: {dialog.FileName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Export cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void CancelExport()
    {
        _exportCts?.Cancel();
    }

    public void SeekPlayhead(TimeSpan time)
    {
        Playhead = time;
        _player.Seek(time);
    }

    public void MoveMarker(Guid markerId, TimeSpan newTime)
    {
        if (Timeline is null)
            return;
        Apply(Timeline.MoveMarker(markerId, newTime));
    }

    private void Apply(Result<Timeline> result)
    {
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error!.Value.Message;
            return;
        }

        Timeline = result.Value;
        StatusMessage = "Ready.";
    }

    private async Task LoadVideoAsync(string path, bool runSceneDetect)
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Probing video…";
            var info = await _probe.ProbeAsync(path);
            VideoPath = path;
            _player.Load(path);

            var timeline = Timeline.Create(info.Duration);
            if (runSceneDetect)
            {
                StatusMessage = "Detecting scene cuts…";
                var times = await _sceneDetector.DetectAsync(path);
                var added = timeline.AddMarkers(times.Select(t => (t, MarkerSource.SceneDetect)));
                if (added.IsSuccess)
                    timeline = added.Value!;
            }

            Timeline = timeline;
            Playhead = TimeSpan.Zero;
            StatusMessage = $"Loaded {Path.GetFileName(path)} — {timeline.Markers.Count} markers.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshSelectionFromTimeline()
    {
        if (Timeline is null || SelectedDeletedRunId is null)
        {
            SelectedCrossfadeCap = 0;
            return;
        }

        var run = Timeline.GetDeletedRuns().FirstOrDefault(r => r.Id == SelectedDeletedRunId);
        if (run is null || !run.IsInterior || run.Join is null)
        {
            SelectedDeletedRunId = null;
            return;
        }

        SelectedIsCrossfade = run.Join.Mode == TransitionMode.Crossfade;
        SelectedCrossfadeSeconds = run.Join.CrossfadeSeconds;
        SelectedCrossfadeCap = Timeline.GetCrossfadeCap(run.Start, run.End);
    }

    private async Task RefreshThumbnailsAsync()
    {
        if (Timeline is null || VideoPath is null)
            return;

        var path = VideoPath;
        var duration = Timeline.Duration;
        var markers = Timeline.Markers.ToList();

        MarkerThumbs.Clear();
        foreach (var marker in markers)
        {
            string? before = null;
            string? after = null;
            try
            {
                before = await _thumbnails.ExtractAsync(path, marker.Time, ThumbnailSide.Before, duration);
                after = await _thumbnails.ExtractAsync(path, marker.Time, ThumbnailSide.After, duration);
            }
            catch
            {
                // ignore thumb failures
            }

            MarkerThumbs.Add(new MarkerThumbViewModel
            {
                MarkerId = marker.Id,
                Time = marker.Time,
                BeforeImage = LoadBitmap(before),
                AfterImage = LoadBitmap(after)
            });
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

    public void Dispose() => _player.Dispose();
}

public sealed class MarkerThumbViewModel
{
    public Guid MarkerId { get; init; }
    public TimeSpan Time { get; init; }
    public BitmapImage? BeforeImage { get; init; }
    public BitmapImage? AfterImage { get; init; }
}
