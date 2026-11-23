using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VideoAbridge.FFmpeg;
using VideoAbridge.ViewModels;

namespace VideoAbridge.Controls;

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
        DependencyProperty.Register(nameof(SelectedDeletedRunId), typeof(DeletedRunId?), typeof(TimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsPreviewModeProperty =
        DependencyProperty.Register(nameof(IsPreviewMode), typeof(bool), typeof(TimelineControl),
            new PropertyMetadata(false, OnPreviewModeChanged));

    public static readonly DependencyProperty AbridgedLayoutProperty =
        DependencyProperty.Register(nameof(AbridgedLayout), typeof(AbridgedTimelineLayout), typeof(TimelineControl),
            new PropertyMetadata(null, OnAbridgedLayoutChanged));

    public static readonly DependencyProperty AudioPeakEnvelopeProperty =
        DependencyProperty.Register(nameof(AudioPeakEnvelope), typeof(AudioPeakEnvelope), typeof(TimelineControl),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty PreviewExportPlanProperty =
        DependencyProperty.Register(nameof(PreviewExportPlan), typeof(ExportPlan), typeof(TimelineControl),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty PreviewTimeMapProperty =
        DependencyProperty.Register(nameof(PreviewTimeMap), typeof(PreviewTimeMap), typeof(TimelineControl),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty FocusedTimelineItemIdProperty =
        DependencyProperty.Register(nameof(FocusedTimelineItemId), typeof(TimelineItemId?), typeof(TimelineControl),
            new PropertyMetadata(null, OnFocusItemChanged));

    public static readonly DependencyProperty IsTimelineKeyboardFocusedProperty =
        DependencyProperty.Register(nameof(IsTimelineKeyboardFocused), typeof(bool), typeof(TimelineControl),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public const double MinOverlayPixels = 6;

    /// <summary>Scroll margin so the focus triangle clears the viewport edge when scroll room exists.</summary>
    private const double FocusVisibilityPad = 8;
    private const double FocusTriangleHalfWidth = 5;
    private const double FocusTriangleHeight = 7;

    private static readonly Brush WaveformBrush = CreateFrozenBrush(0x99, 0x00, 0x00, 0x00);
    private static readonly Brush KeepDurationLabelBrush = CreateFrozenBrush(0x9A, 0x9A, 0x9E);
    private static readonly Brush FocusTriangleBrightBrush = CreateFrozenBrush(0xFF, 0x33, 0x33);
    private static readonly Brush FocusTriangleDimBrush = CreateFrozenBrush(0x66, 0x14, 0x14);
    private static readonly Brush KeptSegmentBrush = CreateFrozenBrush(0x32, 0x6E, 0x46);
    /// <summary>40% darker than <see cref="KeptSegmentBrush"/> for fade/crossfade tails.</summary>
    private static readonly Brush TransitionShadeBrush = CreateFrozenBrush(0x1E, 0x42, 0x2A);
    private static readonly Brush DeletedSegmentBrush = CreateFrozenBrush(0x78, 0x28, 0x28);

    private Line? _playheadLine;
    private TimeSpan _fittedDuration = TimeSpan.Zero;
    private bool _updatingZoom;
    private double _lastViewportWidth;
    private HashSet<MarkerId> _selectedMarkerSet = [];
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
        Scroller.ScrollChanged += (_, _) => TimelineViewChanged?.Invoke(this, EventArgs.Empty);
        IsKeyboardFocusWithinChanged += (_, _) =>
            IsTimelineKeyboardFocused = IsKeyboardFocusWithin;
        Loaded += (_, _) =>
        {
            UpdateZoomBounds(forceFit: Timeline is not null);
            _lastViewportWidth = GetViewportWidth();
            IsTimelineKeyboardFocused = IsKeyboardFocusWithin;
        };
    }

    /// <summary>Left edge of the viewport as timeline seconds.</summary>
    public double GetVisibleStartSeconds()
    {
        var pps = Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        if (pps <= 1e-9)
            return 0;
        return Math.Max(0, Scroller.HorizontalOffset / pps);
    }

    /// <summary>Scroll so <paramref name="startSeconds"/> is at the left edge of the viewport.</summary>
    public void SetVisibleStartSeconds(double startSeconds)
    {
        var pps = Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        UpdateLayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, startSeconds * pps));
    }

    /// <summary>
    /// Scroll horizontally just enough so the focus triangle is in the viewport when scroll room allows.
    /// At the true timeline start/end the triangle may clip; no extra canvas pad is added.
    /// </summary>
    public void EnsureFocusedItemVisible()
    {
        if (IsPreviewMode || Timeline is null || FocusedTimelineItemId is not { } focus)
            return;

        var pps = Math.Max(MinPixelsPerSecond, PixelsPerSecond);
        double? centerX = focus switch
        {
            MarkerId markerId => Timeline.Markers.FirstOrDefault(m => m.Id == markerId) is { } marker
                ? marker.Time.TotalSeconds * pps
                : null,
            DeletedRunId runId => Timeline.GetDeletedRuns().FirstOrDefault(r => r.Id == runId) is { } run
                ? (run.Range.Start.TotalSeconds + run.Range.End.TotalSeconds) / 2.0 * pps
                : null,
        };

        if (centerX is not { } x)
            return;

        var viewport = GetViewportWidth();
        if (viewport <= 1)
            return;

        var canvasWidth = RootCanvas.Width > 1
            ? RootCanvas.Width
            : Timeline.Duration.TotalSeconds * pps;
        var maxOffset = Math.Max(0, canvasWidth - viewport);
        var left = Scroller.HorizontalOffset;
        var right = left + viewport;

        if (x - FocusVisibilityPad < left)
            Scroller.ScrollToHorizontalOffset(Math.Clamp(x - FocusVisibilityPad, 0, maxOffset));
        else if (x + FocusVisibilityPad > right)
            Scroller.ScrollToHorizontalOffset(Math.Clamp(x + FocusVisibilityPad - viewport, 0, maxOffset));
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

    public DeletedRunId? SelectedDeletedRunId
    {
        get => (DeletedRunId?)GetValue(SelectedDeletedRunIdProperty);
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

    public AudioPeakEnvelope? AudioPeakEnvelope
    {
        get => (AudioPeakEnvelope?)GetValue(AudioPeakEnvelopeProperty);
        set => SetValue(AudioPeakEnvelopeProperty, value);
    }

    public ExportPlan? PreviewExportPlan
    {
        get => (ExportPlan?)GetValue(PreviewExportPlanProperty);
        set => SetValue(PreviewExportPlanProperty, value);
    }

    public PreviewTimeMap? PreviewTimeMap
    {
        get => (PreviewTimeMap?)GetValue(PreviewTimeMapProperty);
        set => SetValue(PreviewTimeMapProperty, value);
    }

    public TimelineItemId? FocusedTimelineItemId
    {
        get => (TimelineItemId?)GetValue(FocusedTimelineItemIdProperty);
        set => SetValue(FocusedTimelineItemIdProperty, value);
    }

    public bool IsTimelineKeyboardFocused
    {
        get => (bool)GetValue(IsTimelineKeyboardFocusedProperty);
        set => SetValue(IsTimelineKeyboardFocusedProperty, value);
    }

    public event EventHandler<TimeSpanEventArgs>? SeekRequested;
    public event EventHandler<TimelineItemIdEventArgs>? MarkerClicked;
    public event EventHandler<TimelineItemIdEventArgs>? DeletedRunSelected;
    public event EventHandler? TimelineViewChanged;
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

    private static void OnFocusItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        control.Redraw();
        control.EnsureFocusedItemVisible();
    }

    private static void OnPreviewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        // Keep the caller's zoom when switching Edit/Preview; only refresh min/max for the new duration.
        control.UpdateZoomBounds(forceFit: false);
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
        _selectedMarkerSet = SelectedMarkerIds?.OfType<MarkerId>().ToHashSet() ?? [];
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

    /// <summary>Fit the entire displayed timeline in the viewport (full zoom out).</summary>
    public void ZoomToFit()
    {
        UpdateZoomBounds(forceFit: true);
        Redraw();
        UpdateLayout();
        Scroller.ScrollToHorizontalOffset(0);
    }

    /// <summary>
    /// Zoom in/out around the focused marker or cut (red triangle); falls back to the playhead.
    /// </summary>
    /// <param name="zoomIn">True to zoom in (~1.1×), false to zoom out.</param>
    public void ZoomAroundFocus(bool zoomIn)
    {
        var min = MinPixelsPerSecond;
        var max = Math.Max(MaxPixelsPerSecond, min);
        var oldPps = Math.Max(min, PixelsPerSecond);
        var factor = zoomIn ? 1.1 : 1 / 1.1;
        var newPps = Math.Clamp(oldPps * factor, min, max);
        if (Math.Abs(newPps - oldPps) < 1e-9)
            return;

        var centerSeconds = TryGetFocusCenterSeconds() ?? Playhead.TotalSeconds;
        var viewport = GetViewportWidth();
        var viewportCenterX = viewport > 1 ? viewport / 2 : 0;

        PixelsPerSecond = newPps;
        UpdateLayout();
        var newX = centerSeconds * newPps;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, newX - viewportCenterX));
        TimelineViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private double? TryGetFocusCenterSeconds()
    {
        if (IsPreviewMode || Timeline is null || FocusedTimelineItemId is not { } focus)
            return null;

        return focus switch
        {
            MarkerId markerId => Timeline.Markers.FirstOrDefault(m => m.Id == markerId) is { } marker
                ? marker.Time.TotalSeconds
                : null,
            DeletedRunId runId => Timeline.GetDeletedRuns().FirstOrDefault(r => r.Id == runId) is { } run
                ? (run.Range.Start.TotalSeconds + run.Range.End.TotalSeconds) / 2.0
                : null,
        };
    }

    /// <summary>
    /// Zoom so all undeleted (kept) sections fit in the viewport.
    /// In preview mode the display is already abridged, so this matches <see cref="ZoomToFit"/>.
    /// </summary>
    public void ZoomToContent()
    {
        if (IsPreviewMode || Timeline is null)
        {
            ZoomToFit();
            return;
        }

        var kept = Timeline.GetKeptRuns();
        if (kept.Count == 0)
        {
            ZoomToFit();
            return;
        }

        var start = kept[0].Start;
        var end = kept[^1].End;
        var contentSeconds = (end - start).TotalSeconds;
        if (contentSeconds <= 1e-9)
        {
            ZoomToFit();
            return;
        }

        UpdateZoomBounds(forceFit: false);
        var viewport = GetViewportWidth();
        if (viewport <= 1)
            return;

        var min = MinPixelsPerSecond;
        var max = Math.Max(MaxPixelsPerSecond, min);
        var newPps = Math.Clamp(viewport / contentSeconds, min, max);

        _updatingZoom = true;
        try
        {
            PixelsPerSecond = newPps;
        }
        finally
        {
            _updatingZoom = false;
        }

        Redraw();
        UpdateLayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, start.TotalSeconds * newPps));
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
            Fill = KeptSegmentBrush,
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

            var selectedInterior = overlay.Value is InteriorOverlay interior
                && SelectedDeletedRunId is { } sel
                && interior.DeletedRunId == sel
                && !interior.DeletedRunId.IsEmpty;

            var rect = new Rectangle
            {
                Width = Math.Max(1, right - left),
                Height = trackHeight,
                Fill = OverlayBrush(overlay),
                Stroke = selectedInterior ? Brushes.Gold : Brushes.Transparent,
                StrokeThickness = selectedInterior ? 2 : 0,
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
        }

        DrawPreviewWaveform(layout, pps, trackTop, trackHeight);

        foreach (var overlay in layout.Overlays)
        {
            if (overlay.Value is not InteriorOverlay joinOverlay)
                continue;

            var (left, right) = OverlayPixelBounds(overlay, pps, trackWidth);
            var label = new TextBlock
            {
                Text = joinOverlay.Mode == TransitionMode.Crossfade
                    ? $"XF {joinOverlay.Range.Duration.TotalSeconds:0.##}s"
                    : "Cut",
                Foreground = Brushes.White,
                FontSize = 10,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, (left + right) / 2 - 12);
            Canvas.SetTop(label, trackTop + trackHeight + 4);
            RootCanvas.Children.Add(label);
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
        var (keptRuns, deletedRuns) = timeline.GetKeptAndDeletedRuns();
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
                Fill = deleted ? DeletedSegmentBrush : KeptSegmentBrush,
                Stroke = Brushes.Black,
                StrokeThickness = 0.5,
                Tag = ("segment", i)
            };
            Canvas.SetLeft(rect, start);
            Canvas.SetTop(rect, trackTop);
            RootCanvas.Children.Add(rect);
        }

        foreach (var shade in timeline.GetTransitionShadeRanges(keptRuns, deletedRuns))
        {
            var left = shade.Start.TotalSeconds * pps;
            var right = shade.End.TotalSeconds * pps;
            if (right - left < 0.5)
                continue;

            var shadeRect = new Rectangle
            {
                Width = Math.Max(1, right - left),
                Height = trackHeight,
                Fill = TransitionShadeBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(shadeRect, left);
            Canvas.SetTop(shadeRect, trackTop);
            RootCanvas.Children.Add(shadeRect);
        }

        DrawEditWaveform(timeline.Duration, pps, trackTop, trackHeight);

        DrawKeepDurationLabels(timeline, pps, trackTop, trackHeight);

        foreach (var run in deletedRuns)
        {
            var left = run.Range.Start.TotalSeconds * pps;
            var right = run.Range.End.TotalSeconds * pps;
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
                Focus();
                DeletedRunSelected?.Invoke(this, new TimelineItemIdEventArgs(run.Id));
                SelectedDeletedRunId = run.Id;
                e.Handled = true;
            };
            RootCanvas.Children.Add(label);

            if (FocusedTimelineItemId == run.Id)
            {
                AddFocusTriangle(mid, trackTop + trackHeight + 18);
            }

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
                Focus();
                MarkerClicked?.Invoke(this, new TimelineItemIdEventArgs(marker.Id));
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
                Focus();
                MarkerClicked?.Invoke(this, new TimelineItemIdEventArgs(marker.Id));
                e.Handled = true;
            };
            RootCanvas.Children.Add(handle);

            if (FocusedTimelineItemId == marker.Id)
            {
                AddFocusTriangle(x, trackTop + trackHeight + 6);
            }
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
        var trueLeft = overlay.Range.Start.TotalSeconds * pps;
        var trueRight = overlay.Range.End.TotalSeconds * pps;
        var trueWidth = Math.Max(0, trueRight - trueLeft);
        var width = Math.Max(trueWidth, MinOverlayPixels);
        var isHardCut = overlay.Value is InteriorOverlay { Mode: TransitionMode.HardCut };

        double left;
        if (isHardCut || trueWidth < MinOverlayPixels)
        {
            // Expand around the true center so tiny fades/XFs stay clickable without shifting neighbors.
            var center = isHardCut
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

    private void DrawEditWaveform(TimeSpan duration, double pps, double trackTop, double trackHeight)
    {
        var peaks = AudioPeakEnvelope;
        if (peaks is null || peaks.IsEmpty || duration <= TimeSpan.Zero)
            return;

        var trackWidth = duration.TotalSeconds * pps;
        var columnCount = WaveformColumnCount(trackWidth);
        var columns = new (float Min, float Max, double Gain)[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var t = TimeSpan.FromSeconds((i + 0.5) * duration.TotalSeconds / columnCount);
            peaks.Sample(t, out var min, out var max);
            columns[i] = (min, max, 1.0);
        }

        DrawWaveformPath(trackTop, trackHeight, trackWidth, columns);
    }

    private void DrawPreviewWaveform(
        AbridgedTimelineLayout layout,
        double pps,
        double trackTop,
        double trackHeight)
    {
        var peaks = AudioPeakEnvelope;
        var plan = PreviewExportPlan;
        var map = PreviewTimeMap;
        if (peaks is null || peaks.IsEmpty || plan is null || map is null || layout.Duration <= TimeSpan.Zero)
            return;

        var trackWidth = layout.Duration.TotalSeconds * pps;
        var columnCount = WaveformColumnCount(trackWidth);
        var columns = new (float Min, float Max, double Gain)[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var abridged = TimeSpan.FromSeconds((i + 0.5) * layout.Duration.TotalSeconds / columnCount);
            var sample = PreviewAudioGain.Evaluate(plan, map, abridged);
            peaks.Sample(sample.PrimarySourceTime, out var pMin, out var pMax);
            var min = pMin * (float)sample.PrimaryWeight;
            var max = pMax * (float)sample.PrimaryWeight;
            if (sample.SecondaryWeight > 1e-6)
            {
                peaks.Sample(sample.SecondarySourceTime, out var sMin, out var sMax);
                min += sMin * (float)sample.SecondaryWeight;
                max += sMax * (float)sample.SecondaryWeight;
            }

            columns[i] = (min, max, sample.AudibleGain);
        }

        DrawWaveformPath(trackTop, trackHeight, trackWidth, columns);
    }

    private int WaveformColumnCount(double trackWidth)
    {
        var viewport = GetViewportWidth();
        return (int)Math.Clamp(
            Math.Ceiling(Math.Max(viewport, trackWidth) * 2),
            32,
            4096);
    }

    private void DrawWaveformPath(
        double trackTop,
        double trackHeight,
        double trackWidth,
        (float Min, float Max, double Gain)[] columns)
    {
        if (trackWidth < 2 || trackHeight < 2 || columns.Length == 0)
            return;

        var midY = trackTop + trackHeight / 2;
        // Samples are scaled to ±1 (percentile peak + clamp); 0.4 from center → 80% bar height.
        var amp = trackHeight * 0.4;
        var step = trackWidth / columns.Length;

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, midY), isFilled: true, isClosed: true);
            for (var i = 0; i < columns.Length; i++)
            {
                var (min, max, gain) = columns[i];
                var g = Math.Clamp(gain, 0, 1);
                var y = midY - Math.Clamp(max, -1, 1) * amp * g;
                ctx.LineTo(new Point((i + 0.5) * step, y), isStroked: true, isSmoothJoin: false);
            }

            for (var i = columns.Length - 1; i >= 0; i--)
            {
                var (min, max, gain) = columns[i];
                var g = Math.Clamp(gain, 0, 1);
                var y = midY - Math.Clamp(min, -1, 1) * amp * g;
                ctx.LineTo(new Point((i + 0.5) * step, y), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = WaveformBrush,
            Stroke = null,
            IsHitTestVisible = false
        };
        RootCanvas.Children.Add(path);
    }

    private void DrawKeepDurationLabels(
        Timeline timeline,
        double pps,
        double trackTop,
        double trackHeight)
    {
        // Segments[0] = start→first marker; Segments[i+1] = markers[i]→markers[i+1];
        // Segments[Markers.Count] = last marker→end (or the whole timeline when empty).
        for (var i = 0; i < timeline.Segments.Count; i++)
        {
            if (timeline.Segments[i].IsDeleted)
                continue;

            var start = i == 0 ? TimeSpan.Zero : timeline.Markers[i - 1].Time;
            var end = i == timeline.Markers.Count ? timeline.Duration : timeline.Markers[i].Time;
            var left = start.TotalSeconds * pps;
            var right = end.TotalSeconds * pps;
            var gap = right - left;
            var labelText = FormatKeepDuration(end - start);
            // Same width estimate used for centering; skip when the keep gap can't fit the text.
            var labelWidth = Math.Max(12, labelText.Length * 3.2);
            if (gap < labelWidth)
                continue;

            var label = new TextBlock
            {
                Text = labelText,
                Foreground = KeepDurationLabelBrush,
                FontSize = 10,
                IsHitTestVisible = false
            };
            var mid = (left + right) / 2;
            Canvas.SetLeft(label, mid - labelWidth);
            Canvas.SetTop(label, trackTop + trackHeight + 4);
            RootCanvas.Children.Add(label);
        }
    }

    private static string FormatKeepDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero));
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", minutes, seconds);
    }

    private static string DeletedRunLabel(DeletedRun run, TimeSpan duration)
    {
        if (run.IsInterior)
        {
            return run.Join?.Mode == TransitionMode.Crossfade
                ? $"XF {run.Join.Crossfade.TotalSeconds:0.##}s"
                : "Cut";
        }

        if (run.Range.Covers(duration))
            return "All";
        if (run.Range.TouchesStart)
            return "Begin";
        return "End";
    }

    private static SolidColorBrush OverlayBrush(AbridgedOverlay overlay) => overlay switch
    {
        ExteriorOverlay => CreateFrozenBrush(0x2E, 0x86, 0xAB),
        InteriorOverlay { Mode: TransitionMode.Crossfade } => CreateFrozenBrush(0xE0, 0x8A, 0x2E),
        InteriorOverlay => CreateFrozenBrush(0xC4, 0x4E, 0x52)
    };

    private void HandleOverlayClick(AbridgedOverlay overlay)
    {
        if (overlay is ExteriorOverlay { Kind: ExteriorOverlayKind.FadeIn })
        {
            FadeInClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (overlay is ExteriorOverlay { Kind: ExteriorOverlayKind.FadeOut })
        {
            FadeOutClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (overlay is InteriorOverlay { DeletedRunId: var id } && !id.IsEmpty)
        {
            DeletedRunSelected?.Invoke(this, new TimelineItemIdEventArgs(id));
            SelectedDeletedRunId = id;
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

    /// <summary>Small upward-pointing triangle; <paramref name="top"/> is the tip Y.</summary>
    private void AddFocusTriangle(double centerX, double top)
    {
        var brush = IsTimelineKeyboardFocused ? FocusTriangleBrightBrush : FocusTriangleDimBrush;
        var triangle = new Polygon
        {
            Points =
            [
                new Point(centerX, top),
                new Point(centerX - FocusTriangleHalfWidth, top + FocusTriangleHeight),
                new Point(centerX + FocusTriangleHalfWidth, top + FocusTriangleHeight),
            ],
            Fill = brush,
            IsHitTestVisible = false,
        };
        RootCanvas.Children.Add(triangle);
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

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b) =>
        CreateFrozenBrush(0xFF, r, g, b);

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
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
        Focus();

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
        var factor = e.Delta > 0 ? 1.05 : 1 / 1.05;
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
        TimelineViewChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
