using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VideoAbridge.Core;
using VideoAbridge.ViewModels;

namespace VideoAbridge.Controls;

public partial class TimelineControl : UserControl
{
    public static readonly DependencyProperty TimelineProperty =
        DependencyProperty.Register(nameof(Timeline), typeof(Timeline), typeof(TimelineControl),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty PlayheadProperty =
        DependencyProperty.Register(nameof(Playhead), typeof(TimeSpan), typeof(TimelineControl),
            new PropertyMetadata(TimeSpan.Zero, OnPlayheadChanged));

    public static readonly DependencyProperty PixelsPerSecondProperty =
        DependencyProperty.Register(nameof(PixelsPerSecond), typeof(double), typeof(TimelineControl),
            new PropertyMetadata(40.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty MarkerThumbsProperty =
        DependencyProperty.Register(nameof(MarkerThumbs), typeof(IEnumerable<MarkerThumbViewModel>), typeof(TimelineControl),
            new PropertyMetadata(null, OnMarkerThumbsChanged));

    public static readonly DependencyProperty SelectedMarkerIdProperty =
        DependencyProperty.Register(nameof(SelectedMarkerId), typeof(Guid?), typeof(TimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged));

    public static readonly DependencyProperty SelectedDeletedRunIdProperty =
        DependencyProperty.Register(nameof(SelectedDeletedRunId), typeof(Guid?), typeof(TimelineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged));

    private Line? _playheadLine;
    private Guid? _dragMarkerId;
    private bool _dragging;

    public TimelineControl()
    {
        InitializeComponent();
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

    public IEnumerable<MarkerThumbViewModel>? MarkerThumbs
    {
        get => (IEnumerable<MarkerThumbViewModel>?)GetValue(MarkerThumbsProperty);
        set => SetValue(MarkerThumbsProperty, value);
    }

    public Guid? SelectedMarkerId
    {
        get => (Guid?)GetValue(SelectedMarkerIdProperty);
        set => SetValue(SelectedMarkerIdProperty, value);
    }

    public Guid? SelectedDeletedRunId
    {
        get => (Guid?)GetValue(SelectedDeletedRunIdProperty);
        set => SetValue(SelectedDeletedRunIdProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<Guid>? MarkerSelected;
    public event EventHandler<Guid>? DeletedRunSelected;
    public event EventHandler<(Guid MarkerId, TimeSpan NewTime)>? MarkerMoved;

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimelineControl)d).Redraw();

    private static void OnMarkerThumbsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TimelineControl)d;
        if (e.OldValue is INotifyCollectionChanged oldNc)
            oldNc.CollectionChanged -= control.OnThumbCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNc)
            newNc.CollectionChanged += control.OnThumbCollectionChanged;
        control.Redraw();
    }

    private void OnThumbCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private static void OnPlayheadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimelineControl)d).UpdatePlayhead();

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        RootCanvas.Children.Clear();
        _playheadLine = null;

        var timeline = Timeline;
        if (timeline is null || timeline.Duration <= TimeSpan.Zero)
        {
            RootCanvas.Width = 400;
            return;
        }

        var pps = Math.Max(2, PixelsPerSecond);
        var width = Math.Max(400, timeline.Duration.TotalSeconds * pps + 80);
        RootCanvas.Width = width;

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

        foreach (var run in timeline.GetDeletedRuns().Where(r => r.IsInterior))
        {
            var left = run.Start.TotalSeconds * pps;
            var right = run.End.TotalSeconds * pps;
            var mid = (left + right) / 2;
            var label = new TextBlock
            {
                Text = run.Join?.Mode == TransitionMode.Crossfade
                    ? $"XF {run.Join.CrossfadeSeconds:0.##}s"
                    : "Cut",
                Foreground = Brushes.White,
                FontSize = 10,
                Tag = run.Id
            };
            Canvas.SetLeft(label, mid - 12);
            Canvas.SetTop(label, trackTop + trackHeight + 4);
            label.MouseLeftButtonDown += (_, e) =>
            {
                DeletedRunSelected?.Invoke(this, run.Id);
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

        var thumbs = MarkerThumbs?.ToDictionary(t => t.MarkerId) ?? new Dictionary<Guid, MarkerThumbViewModel>();
        const double thumbWidth = 56;
        const double thumbHeight = 32;
        const double minGap = thumbWidth * 2 + 8;

        for (var i = 0; i < timeline.Markers.Count; i++)
        {
            var marker = timeline.Markers[i];
            var x = marker.Time.TotalSeconds * pps;

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

            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 4,
                Y2 = trackTop + trackHeight,
                Stroke = SelectedMarkerId == marker.Id ? Brushes.Orange : Brushes.DeepSkyBlue,
                StrokeThickness = SelectedMarkerId == marker.Id ? 3 : 2,
                Tag = marker.Id,
                Cursor = Cursors.SizeWE
            };
            line.MouseLeftButtonDown += (_, e) =>
            {
                SelectedMarkerId = marker.Id;
                MarkerSelected?.Invoke(this, marker.Id);
                _dragMarkerId = marker.Id;
                _dragging = true;
                line.CaptureMouse();
                e.Handled = true;
            };
            line.MouseMove += OnMarkerMouseMove;
            line.MouseLeftButtonUp += OnMarkerMouseUp;
            RootCanvas.Children.Add(line);

            var handle = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = SelectedMarkerId == marker.Id ? Brushes.Orange : Brushes.DeepSkyBlue,
                Tag = marker.Id,
                Cursor = Cursors.SizeWE
            };
            Canvas.SetLeft(handle, x - 5);
            Canvas.SetTop(handle, trackTop + trackHeight - 5);
            handle.MouseLeftButtonDown += (_, e) =>
            {
                SelectedMarkerId = marker.Id;
                MarkerSelected?.Invoke(this, marker.Id);
                _dragMarkerId = marker.Id;
                _dragging = true;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseMove += OnMarkerMouseMove;
            handle.MouseLeftButtonUp += OnMarkerMouseUp;
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
        if (_playheadLine is null || Timeline is null)
            return;

        var x = Playhead.TotalSeconds * Math.Max(2, PixelsPerSecond);
        _playheadLine.X1 = x;
        _playheadLine.X2 = x;
    }

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Timeline is null)
            return;

        var pos = e.GetPosition(RootCanvas);
        var time = TimeSpan.FromSeconds(Math.Clamp(pos.X / Math.Max(2, PixelsPerSecond), 0, Timeline.Duration.TotalSeconds));
        SeekRequested?.Invoke(this, time);
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragMarkerId is null || Timeline is null)
            return;

        var pos = e.GetPosition(RootCanvas);
        var time = TimeSpan.FromSeconds(Math.Clamp(pos.X / Math.Max(2, PixelsPerSecond), 0, Timeline.Duration.TotalSeconds));
        MarkerMoved?.Invoke(this, (_dragMarkerId.Value, time));
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragMarkerId = null;
    }

    private void OnMarkerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragMarkerId is null || e.LeftButton != MouseButtonState.Pressed || Timeline is null)
            return;

        var pos = e.GetPosition(RootCanvas);
        var time = TimeSpan.FromSeconds(Math.Clamp(pos.X / Math.Max(2, PixelsPerSecond), 0, Timeline.Duration.TotalSeconds));
        MarkerMoved?.Invoke(this, (_dragMarkerId.Value, time));
    }

    private void OnMarkerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is IInputElement el)
            el.ReleaseMouseCapture();
        _dragging = false;
        _dragMarkerId = null;
    }

    private void OnCanvasMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // reserved for context menu later
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, 4, 400);
        e.Handled = true;
    }
}
