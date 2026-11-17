using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VideoAbridge.Core;
using VideoAbridge.ViewModels;

namespace VideoAbridge.Controls;

public sealed class TimeSpanEventArgs(TimeSpan time) : EventArgs
{
    public TimeSpan Time { get; } = time;
}

public sealed class GuidEventArgs(Guid id) : EventArgs
{
    public Guid Id { get; } = id;
}

public partial class TimelineControl : UserControl
{
    public const double AbsoluteMaxPixelsPerSecond = 400;

    public static readonly DependencyProperty TimelineProperty =
        DependencyProperty.Register(nameof(Timeline), typeof(Timeline), typeof(TimelineControl),
            new PropertyMetadata(null, OnTimelineChanged));

    public static readonly DependencyProperty PlayheadProperty =
        DependencyProperty.Register(nameof(Playhead), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, OnPlayheadChanged));

    public static readonly DependencyProperty PixelsPerSecondProperty =
        DependencyProperty.Register(nameof(PixelsPerSecond), typeof(double), typeof(TimelineControl),
            new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPixelsPerSecondChanged));

    public static readonly DependencyProperty MinPixelsPerSecondProperty =
        DependencyProperty.Register(nameof(MinPixelsPerSecond), typeof(double), typeof(TimelineControl),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MaxPixelsPerSecondProperty =
        DependencyProperty.Register(nameof(MaxPixelsPerSecond), typeof(double), typeof(TimelineControl),
            new FrameworkPropertyMetadata(AbsoluteMaxPixelsPerSecond, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MarkerThumbsProperty =
        DependencyProperty.Register(nameof(MarkerThumbs), typeof(IEnumerable<MarkerThumbViewModel>), typeof(TimelineControl),
            new PropertyMetadata(null, OnMarkerThumbsChanged));

    public static readonly DependencyProperty SelectedMarkerIdsProperty =
        DependencyProperty.Register(nameof(SelectedMarkerIds), typeof(IEnumerable), typeof(TimelineControl),
            new PropertyMetadata(null, OnSelectedMarkerIdsChanged));

    public static readonly DependencyProperty SelectedDeletedRunIdProperty =
        DependencyProperty.Register(nameof(SelectedDeletedRunId), typeof(Guid?), typeof(TimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsPreviewModeProperty =
        DependencyProperty.Register(nameof(IsPreviewMode), typeof(bool), typeof(TimelineControl),
            new PropertyMetadata(false, OnPreviewModeChanged));

    public static readonly DependencyProperty AbridgedLayoutProperty =
        DependencyProperty.Register(nameof(AbridgedLayout), typeof(AbridgedTimelineLayout), typeof(TimelineControl),
            new PropertyMetadata(null, OnAbridgedLayoutChanged));

    public const double MinOverlayPixels = 6;

    private Line? _playheadLine;
    private TimeSpan _fittedDuration = TimeSpan.Zero;
    private bool _updatingZoom;
    private double _lastViewportWidth;
    private HashSet<Guid> _selectedMarkerSet = [];
    private readonly DispatcherTimer _thumbRedrawTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly List<(AbridgedOverlay Overlay, double Left, double Right)> _previewHitRegions = [];

    public TimelineControl()
    {
        InitializeComponent();
        _thumbRedrawTimer.Tick += (_, _) =>
        {
            _thumbRedrawTimer.Stop();
            Redraw();
        };
        SizeChanged += OnControlSizeChanged;
        Loaded += (_, _) =>
        {
            UpdateZoomBounds(forceFit: Timeline is not null);
            _lastViewportWidth = GetViewportWidth();
        };
    }

    public Timeline? Timeline
    {
        get => (Timeline?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public TimeSpan Playhead
    {
        get => (TimeSpan)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public double MinPixelsPerSecond
    {
        get => (double)GetValue(MinPixelsPerSecondProperty);
        set => SetValue(MinPixelsPerSecondProperty, value);
    }

    public double MaxPixelsPerSecond
    {
        get => (double)GetValue(MaxPixelsPerSecondProperty);
        set => SetValue(MaxPixelsPerSecondProperty, value);
    }

    public IEnumerable<MarkerThumbViewModel>? MarkerThumbs
    {
        get => (IEnumerable<MarkerThumbViewModel>?)GetValue(MarkerThumbsProperty);
        set => SetValue(MarkerThumbsProperty, value);
    }

    public IEnumerable? SelectedMarkerIds
    {
        get => (IEnumerable?)GetValue(SelectedMarkerIdsProperty);
        set => SetValue(SelectedMarkerIdsProperty, value);
    }

    public Guid? SelectedDeletedRunId
    {
        get => (Guid?)GetValue(SelectedDeletedRunIdProperty);
        set => SetValue(SelectedDeletedRunIdProperty, value);
    }

    public bool IsPreviewMode
    {
        get => (bool)GetValue(IsPreviewModeProperty);
        set => SetValue(IsPreviewModeProperty, value);
    }

    public AbridgedTimelineLayout? AbridgedLayout
    {
        get => (AbridgedTimelineLayout?)GetValue(AbridgedLayoutProperty);
        set => SetValue(AbridgedLayoutProperty, value);
    }

    public event EventHandler<TimeSpanEventArgs>? SeekRequested;
    public event EventHandler<GuidEventArgs>? MarkerClicked;
    public event EventHandler<GuidEventArgs>? DeletedRunSelected;
    public event EventHandler? FadeInClicked;
    public event EventHandler? FadeOutClicked;

    private TimeSpan DisplayDuration
    {
        get
        {
            if (IsPreviewMode)
                return AbridgedLayout?.Duration ?? TimeSpan.Zero;
            return Timeline?.Duration ?? TimeSpan.Zero;
        }
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimelineControl)d).Redraw();

    private static void OnPreviewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        control.UpdateZoomBounds(forceFit: true);
        control.Redraw();
    }

    private static void OnAbridgedLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        if (!control.IsPreviewMode)
            return;
        var newDuration = (e.NewValue as AbridgedTimelineLayout)?.Duration ?? TimeSpan.Zero;
        var forceFit = newDuration != control._fittedDuration;
        control.UpdateZoomBounds(forceFit);
        control.Redraw();
    }

    private static void OnTimelineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        if (control.IsPreviewMode)
        {
            control.Redraw();
            return;
        }

        var newDuration = (e.NewValue as Timeline)?.Duration ?? TimeSpan.Zero;
        var forceFit = newDuration != control._fittedDuration;
        control.UpdateZoomBounds(forceFit);
        control.Redraw();
    }

    private static void OnPixelsPerSecondChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        if (!control._updatingZoom)
            control.ClampPixelsPerSecond();
        control.Redraw();
    }

    private static void OnMarkerThumbsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        if (e.OldValue is INotifyCollectionChanged oldNc)
            oldNc.CollectionChanged -= control.OnThumbCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNc)
            newNc.CollectionChanged += control.OnThumbCollectionChanged;
        control.Redraw();
    }

    private static void OnSelectedMarkerIdsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        if (e.OldValue is INotifyCollectionChanged oldNc)
            oldNc.CollectionChanged -= control.OnSelectedMarkerIdsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNc)
            newNc.CollectionChanged += control.OnSelectedMarkerIdsCollectionChanged;
        control.RefreshSelectedMarkerSet();
        control.Redraw();
    }

    private void OnThumbCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Batch redraws while thumbnails stream in after opening a cached project.
        _thumbRedrawTimer.Stop();
        _thumbRedrawTimer.Start();
    }

    private void OnSelectedMarkerIdsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSelectedMarkerSet();
        Redraw();
    }

    private void RefreshSelectedMarkerSet()
    {
        _selectedMarkerSet = SelectedMarkerIds?.OfType<Guid>().ToHashSet() ?? [];
    }

    private static void OnPlayheadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimelineControl)d).UpdatePlayhead();

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;

        // ScrollViewer.ViewportWidth is often still stale inside SizeChanged during live
        // window drags, so derive the new viewport from the control width ratio.
        var oldViewport = _lastViewportWidth > 1 ? _lastViewportWidth : e.PreviousSize.Width;
        var prevWidth = e.PreviousSize.Width > 1 ? e.PreviousSize.Width : oldViewport;
        var newViewport = prevWidth > 1
            ? oldViewport * (e.NewSize.Width / prevWidth)
            : e.NewSize.Width;

        if (newViewport <= 1 || DisplayDuration <= TimeSpan.Zero)
        {
            UpdateZoomBounds(forceFit: false, viewportWidth: newViewport);
            Redraw();
            _lastViewportWidth = newViewport > 1 ? newViewport : GetViewportWidth();
            return;
        }

        var oldPps = Math.Max(PixelsPerSecond, 1e-9);
        var visibleStartSeconds = oldViewport > 1 ? Scroller.HorizontalOffset / oldPps : 0;
        var visibleDurationSeconds = oldViewport > 1 ? oldViewport / oldPps : DisplayDuration.TotalSeconds;

        var durationSeconds = DisplayDuration.TotalSeconds;
        var fit = durationSeconds > 0 ? newViewport / durationSeconds : 4;
        var max = Math.Max(AbsoluteMaxPixelsPerSecond, fit);
        var newPps = visibleDurationSeconds > 1e-9
            ? Math.Clamp(newViewport / visibleDurationSeconds, fit, max)
            : fit;

        _updatingZoom = true;
        try
        {
            MinPixelsPerSecond = fit;
            MaxPixelsPerSecond = max;
            PixelsPerSecond = newPps;
            _fittedDuration = DisplayDuration;
        }
        finally
        {
            _updatingZoom = false;
        }

        Redraw();
        UpdateLayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, visibleStartSeconds * newPps));

        // Keep the viewport we scaled against. ViewportWidth is often still stale mid-drag.
        _lastViewportWidth = newViewport;
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Layout-only; zoom bounds are driven by the scroller viewport.
    }

    private void SetCanvasWidth(double width)
    {
        RootCanvas.Width = width;
        RootCanvas.MinWidth = width;
        RootCanvas.InvalidateMeasure();
        Scroller.InvalidateScrollInfo();
    }

    private double GetViewportWidth()
    {
        if (Scroller.ViewportWidth > 1)
            return Scroller.ViewportWidth;
        if (ActualWidth > 1)
            return ActualWidth;
        return 0;
    }

    private double ComputeFitPixelsPerSecond(double? viewportWidth = null)
    {
        var durationSeconds = DisplayDuration.TotalSeconds;
        if (durationSeconds <= 0)
            return 4;

        var viewport = viewportWidth is > 1 ? viewportWidth.Value : GetViewportWidth();
        if (viewport <= 1)
            return MinPixelsPerSecond > 0 ? MinPixelsPerSecond : 4;

        return viewport / durationSeconds;
    }

    private void UpdateZoomBounds(bool forceFit, double? viewportWidth = null)
    {
        var fit = ComputeFitPixelsPerSecond(viewportWidth);
        var max = Math.Max(AbsoluteMaxPixelsPerSecond, fit);

        _updatingZoom = true;
        try
        {
            MinPixelsPerSecond = fit;
            MaxPixelsPerSecond = max;
            _fittedDuration = DisplayDuration;

            if (forceFit || PixelsPerSecond < fit)
                PixelsPerSecond = fit;
            else if (PixelsPerSecond > max)
                PixelsPerSecond = max;
        }
        finally
        {
            _updatingZoom = false;
        }
    }

    private void ClampPixelsPerSecond()
    {
        var min = MinPixelsPerSecond;
        var max = Math.Max(MaxPixelsPerSecond, min);
        var clamped = Math.Clamp(PixelsPerSecond, min, max);
        if (Math.Abs(clamped - PixelsPerSecond) < 0.0001)
            return;

        _updatingZoom = true;
        try
        {
            PixelsPerSecond = clamped;
        }
        finally
        {
            _updatingZoom = false;
        }
    }

    private void Redraw()
    {
        RootCanvas.Children.Clear();
        _playheadLine = null;
        _previewHitRegions.Clear();

        if (IsPreviewMode)
        {
            RedrawPreview();
            return;
        }

        RedrawEdit();
    }

    private void RedrawPreview()
    {
        var layout = AbridgedLayout;
        if (layout is null || layout.Duration <= TimeSpan.Zero)
        {
            SetCanvasWidth(Math.Max(400, GetViewportWidth()));
            return;
        }

        var pps = Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        var viewport = GetViewportWidth();
        var width = Math.Max(viewport > 1 ? viewport : 400, layout.Duration.TotalSeconds * pps);
        SetCanvasWidth(width);

        const double trackTop = 48;
        const double trackHeight = 44;
        var trackWidth = layout.Duration.TotalSeconds * pps;

        var baseBar = new Rectangle
        {
            Width = Math.Max(1, trackWidth),
            Height = trackHeight,
            Fill = CreateFrozenBrush(0x32, 0x6E, 0x46),
            Stroke = Brushes.Black,
            StrokeThickness = 0.5,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(baseBar, 0);
        Canvas.SetTop(baseBar, trackTop);
        RootCanvas.Children.Add(baseBar);

        foreach (var overlay in layout.Overlays)
        {
            var (left, right) = OverlayPixelBounds(overlay, pps, trackWidth);
            _previewHitRegions.Add((overlay, left, right));

            var rect = new Rectangle
            {
                Width = Math.Max(1, right - left),
                Height = trackHeight,
                Fill = OverlayBrush(overlay.Kind),
                Stroke = SelectedDeletedRunId is Guid sel
                         && overlay.DeletedRunId == sel
                    ? Brushes.Gold
                    : Brushes.Transparent,
                StrokeThickness = SelectedDeletedRunId is Guid s2
                                  && overlay.DeletedRunId == s2
                    ? 2
                    : 0,
                Cursor = Cursors.Hand,
                Tag = overlay
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, trackTop);
            rect.MouseLeftButtonDown += (_, e) =>
            {
                HandleOverlayClick(overlay);
                e.Handled = true;
            };
            RootCanvas.Children.Add(rect);

            if (overlay.Kind is AbridgedOverlayKind.Crossfade or AbridgedOverlayKind.Cut)
            {
                var label = new TextBlock
                {
                    Text = overlay.Kind == AbridgedOverlayKind.Crossfade
                        ? $"XF {(overlay.End - overlay.Start).TotalSeconds:0.##}s"
                        : "Cut",
                    Foreground = Brushes.White,
                    FontSize = 10,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, (left + right) / 2 - 12);
                Canvas.SetTop(label, trackTop + trackHeight + 4);
                RootCanvas.Children.Add(label);
            }
        }

        // Topmost hit order: later overlays win; reverse list so cuts/xf drawn later are tested first.
        _previewHitRegions.Reverse();

        _playheadLine = new Line
        {
            Y1 = 0,
            Y2 = 160,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        RootCanvas.Children.Add(_playheadLine);
        UpdatePlayhead();
    }

    private void RedrawEdit()
    {
        var timeline = Timeline;
        if (timeline is null || timeline.Duration <= TimeSpan.Zero)
        {
            SetCanvasWidth(Math.Max(400, GetViewportWidth()));
            return;
        }

        var pps = Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        var viewport = GetViewportWidth();
        var width = Math.Max(viewport > 1 ? viewport : 400, timeline.Duration.TotalSeconds * pps);
        SetCanvasWidth(width);

        var boundaries = timeline.GetBoundaries();
        const double trackTop = 70;
        const double trackHeight = 36;

        for (var i = 0; i < timeline.Segments.Count; i++)
        {
            var start = boundaries[i].TotalSeconds * pps;
            var end = boundaries[i + 1].TotalSeconds * pps;
            var deleted = timeline.Segments[i].IsDeleted;

            var rect = new Rectangle
            {
                Width = Math.Max(1, end - start),
                Height = trackHeight,
                Fill = deleted
                    ? new SolidColorBrush(Color.FromRgb(120, 40, 40))
                    : new SolidColorBrush(Color.FromRgb(50, 110, 70)),
                Stroke = Brushes.Black,
                StrokeThickness = 0.5,
                Tag = ("segment", i)
            };
            Canvas.SetLeft(rect, start);
            Canvas.SetTop(rect, trackTop);
            RootCanvas.Children.Add(rect);
        }

        foreach (var run in timeline.GetDeletedRuns())
        {
            var left = run.Start.TotalSeconds * pps;
            var right = run.End.TotalSeconds * pps;
            var mid = (left + right) / 2;
            var labelText = DeletedRunLabel(run, timeline.Duration);
            var label = new TextBlock
            {
                Text = labelText,
                Foreground = Brushes.White,
                FontSize = 10,
                Cursor = Cursors.Hand,
                Tag = run.Id
            };
            // Approximate center using typical label width for short captions.
            Canvas.SetLeft(label, mid - Math.Max(12, labelText.Length * 3.2));
            Canvas.SetTop(label, trackTop + trackHeight + 4);
            label.MouseLeftButtonDown += (_, e) =>
            {
                DeletedRunSelected?.Invoke(this, new GuidEventArgs(run.Id));
                SelectedDeletedRunId = run.Id;
                e.Handled = true;
            };
            RootCanvas.Children.Add(label);

            if (SelectedDeletedRunId == run.Id)
            {
                var highlight = new Rectangle
                {
                    Width = Math.Max(1, right - left),
                    Height = trackHeight,
                    Stroke = Brushes.Gold,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(highlight, left);
                Canvas.SetTop(highlight, trackTop);
                RootCanvas.Children.Add(highlight);
            }
        }

        var thumbs = MarkerThumbs?.ToDictionary(t => t.MarkerId) ?? [];
        const double thumbWidth = 56;
        const double thumbHeight = 32;
        const double minGap = thumbWidth * 2 + 8;

        for (var i = 0; i < timeline.Markers.Count; i++)
        {
            var marker = timeline.Markers[i];
            var x = marker.Time.TotalSeconds * pps;
            var isSelected = _selectedMarkerSet.Contains(marker.Id);

            var prevX = i == 0 ? 0 : timeline.Markers[i - 1].Time.TotalSeconds * pps;
            var nextX = i == timeline.Markers.Count - 1
                ? timeline.Duration.TotalSeconds * pps
                : timeline.Markers[i + 1].Time.TotalSeconds * pps;

            var spaceLeft = x - prevX;
            var spaceRight = nextX - x;
            var showThumbs = spaceLeft >= minGap / 2 && spaceRight >= minGap / 2
                             || spaceLeft + spaceRight >= minGap;

            if (showThumbs && thumbs.TryGetValue(marker.Id, out var thumb))
            {
                if (thumb.BeforeImage is not null && spaceLeft >= thumbWidth + 4)
                    AddThumb(thumb.BeforeImage, x - thumbWidth - 2, 8, thumbWidth, thumbHeight);
                if (thumb.AfterImage is not null && spaceRight >= thumbWidth + 4)
                    AddThumb(thumb.AfterImage, x + 2, 8, thumbWidth, thumbHeight);
            }

            var brush = MarkerBrush(marker.Source, isSelected);
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 4,
                Y2 = trackTop + trackHeight,
                Stroke = brush,
                StrokeThickness = isSelected ? 3 : 2,
                Tag = marker.Id,
                Cursor = Cursors.Hand
            };
            line.MouseLeftButtonDown += (_, e) =>
            {
                MarkerClicked?.Invoke(this, new GuidEventArgs(marker.Id));
                e.Handled = true;
            };
            RootCanvas.Children.Add(line);

            var handle = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = brush,
                Tag = marker.Id,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(handle, x - 5);
            Canvas.SetTop(handle, trackTop + trackHeight - 5);
            handle.MouseLeftButtonDown += (_, e) =>
            {
                MarkerClicked?.Invoke(this, new GuidEventArgs(marker.Id));
                e.Handled = true;
            };
            RootCanvas.Children.Add(handle);
        }

        _playheadLine = new Line
        {
            Y1 = 0,
            Y2 = 160,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        RootCanvas.Children.Add(_playheadLine);
        UpdatePlayhead();
    }

    private static (double Left, double Right) OverlayPixelBounds(
        AbridgedOverlay overlay,
        double pps,
        double trackWidth)
    {
        var trueLeft = overlay.Start.TotalSeconds * pps;
        var trueRight = overlay.End.TotalSeconds * pps;
        var trueWidth = Math.Max(0, trueRight - trueLeft);
        var width = Math.Max(trueWidth, MinOverlayPixels);

        double left;
        if (overlay.Kind == AbridgedOverlayKind.Cut || trueWidth < MinOverlayPixels)
        {
            // Expand around the true center so tiny fades/XFs stay clickable without shifting neighbors.
            var center = overlay.Kind == AbridgedOverlayKind.Cut
                ? trueLeft
                : (trueLeft + trueRight) / 2;
            left = center - width / 2;
        }
        else
        {
            left = trueLeft;
        }

        if (left < 0)
            left = 0;
        if (left + width > trackWidth && trackWidth > width)
            left = trackWidth - width;

        return (left, left + width);
    }

    private static string DeletedRunLabel(DeletedRun run, TimeSpan duration)
    {
        if (run.IsInterior)
        {
            return run.Join?.Mode == TransitionMode.Crossfade
                ? $"XF {run.Join.CrossfadeSeconds:0.##}s"
                : "Cut";
        }

        if (run.Start <= TimeSpan.Zero && run.End >= duration)
            return "All";
        if (run.Start <= TimeSpan.Zero)
            return "Begin";
        return "End";
    }

    private static SolidColorBrush OverlayBrush(AbridgedOverlayKind kind) => kind switch
    {
        AbridgedOverlayKind.FadeIn => CreateFrozenBrush(0x2E, 0x86, 0xAB),
        AbridgedOverlayKind.FadeOut => CreateFrozenBrush(0x2E, 0x86, 0xAB),
        AbridgedOverlayKind.Crossfade => CreateFrozenBrush(0xE0, 0x8A, 0x2E),
        AbridgedOverlayKind.Cut => CreateFrozenBrush(0xC4, 0x4E, 0x52),
        _ => (SolidColorBrush)Brushes.Gray
    };

    private void HandleOverlayClick(AbridgedOverlay overlay)
    {
        switch (overlay.Kind)
        {
            case AbridgedOverlayKind.FadeIn:
                FadeInClicked?.Invoke(this, EventArgs.Empty);
                break;
            case AbridgedOverlayKind.FadeOut:
                FadeOutClicked?.Invoke(this, EventArgs.Empty);
                break;
            case AbridgedOverlayKind.Crossfade:
            case AbridgedOverlayKind.Cut:
                if (overlay.DeletedRunId is Guid id)
                {
                    DeletedRunSelected?.Invoke(this, new GuidEventArgs(id));
                    SelectedDeletedRunId = id;
                }
                break;
            default:
                break;
        }
    }

    private void AddThumb(BitmapImage image, double left, double top, double width, double height)
    {
        var img = new Image
        {
            Source = image,
            Width = width,
            Height = height,
            Stretch = Stretch.UniformToFill
        };
        var border = new Border
        {
            Child = img,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Width = width,
            Height = height
        };
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        RootCanvas.Children.Add(border);
    }

    private void UpdatePlayhead()
    {
        if (_playheadLine is null)
            return;

        if (IsPreviewMode)
        {
            if (AbridgedLayout is null || AbridgedLayout.Duration <= TimeSpan.Zero)
                return;
        }
        else if (Timeline is null)
        {
            return;
        }

        var x = Playhead.TotalSeconds * Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        _playheadLine.X1 = x;
        _playheadLine.X2 = x;
    }

    private static readonly Brush SceneMarkerBrush = CreateFrozenBrush(0x6C, 0xA0, 0xDC);
    private static readonly Brush SceneMarkerSelectedBrush = CreateFrozenBrush(0xF0, 0xA0, 0x4B);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Manual markers: bright sky blue / orange when selected.
    /// Scene-detect markers: softer cornflower blue / amber when selected (similar palette).
    /// </summary>
    private static Brush MarkerBrush(MarkerSource source, bool selected) =>
        (source, selected) switch
        {
            (MarkerSource.User, false) => Brushes.DeepSkyBlue,
            (MarkerSource.User, true) => Brushes.Orange,
            (MarkerSource.SceneDetect, false) => SceneMarkerBrush,
            (MarkerSource.SceneDetect, true) => SceneMarkerSelectedBrush,
            _ => Brushes.DeepSkyBlue
        };

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var duration = DisplayDuration;
        if (duration <= TimeSpan.Zero)
            return;

        var pps = Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        var pos = e.GetPosition(RootCanvas);

        if (IsPreviewMode)
        {
            foreach (var (overlay, left, right) in _previewHitRegions)
            {
                if (pos.X >= left && pos.X <= right)
                {
                    HandleOverlayClick(overlay);
                    e.Handled = true;
                    return;
                }
            }
        }

        var time = TimeSpan.FromSeconds(Math.Clamp(pos.X / pps, 0, duration.TotalSeconds));
        SeekRequested?.Invoke(this, new TimeSpanEventArgs(time));
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        // Marker dragging removed.
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
    }

    private void OnCanvasMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // reserved for context menu later
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var min = MinPixelsPerSecond;
        var max = Math.Max(MaxPixelsPerSecond, min);
        var oldPps = Math.Max(min, PixelsPerSecond);
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newPps = Math.Clamp(oldPps * factor, min, max);
        if (Math.Abs(newPps - oldPps) < 1e-9)
        {
            e.Handled = true;
            return;
        }

        // Keep the timeline time under the cursor fixed while zooming.
        var mouseOnCanvas = e.GetPosition(RootCanvas);
        var mouseInViewport = e.GetPosition(Scroller);
        var timeAtMouse = mouseOnCanvas.X / oldPps;

        PixelsPerSecond = newPps;

        // Redraw/layout updates canvas width from the new PPS; then re-center scroll on the cursor.
        UpdateLayout();
        var newX = timeAtMouse * newPps;
        var targetOffset = newX - mouseInViewport.X;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, targetOffset));
        e.Handled = true;
    }
}
