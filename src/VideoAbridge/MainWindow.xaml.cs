using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using VideoAbridge.Services;
using VideoAbridge.ViewModels;

namespace VideoAbridge;

public partial class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan PreviewChromeHideDelay = TimeSpan.FromSeconds(2);

    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _previewChromeHideTimer;
    private readonly RelayCommand _zoomToAbridgedContentCommand;
    private readonly RelayCommand _zoomToFitCommand;
    private bool _shutdownStarted;
    private bool _previewChromeVisible;
    private bool _applyingWindowPlacement;
    private bool _disposed;

    public MainWindow()
    {
        _zoomToAbridgedContentCommand = new RelayCommand(
            () => Timeline.ZoomToContent(),
            () => _viewModel.HasVideoLoaded);
        _zoomToFitCommand = new RelayCommand(
            () => Timeline.ZoomToFit(),
            () => _viewModel.HasVideoLoaded);

        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(MainViewModel.HasVideoLoaded))
            {
                _zoomToAbridgedContentCommand.NotifyCanExecuteChanged();
                _zoomToFitCommand.NotifyCanExecuteChanged();
            }

            if (e.PropertyName is null or nameof(MainViewModel.PendingWindowPlacement))
                ApplyPendingWindowPlacement();

            if (e.PropertyName is null
                or nameof(MainViewModel.PendingVisibleStartSeconds)
                or nameof(MainViewModel.PendingForceTimelineFit))
            {
                ApplyPendingTimelineView();
            }
        };

        _previewChromeHideTimer = new DispatcherTimer { Interval = PreviewChromeHideDelay };
        _previewChromeHideTimer.Tick += (_, _) => HidePreviewChrome();

        Timeline.SeekRequested += (_, e) =>
        {
            if (_viewModel.IsPreviewMode)
                _viewModel.SeekAbridgedPlayhead(e.Time);
            else
                _viewModel.SeekPlayhead(e.Time);
        };
        Timeline.MarkerClicked += (_, e) =>
            _viewModel.ToggleMarkerSelection(
                e.Id,
                extendSelection: (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
        Timeline.DeletedRunSelected += (_, e) => _viewModel.SelectDeletedRunCommand.Execute(e.Id);
        Timeline.FadeInClicked += (_, _) => FocusFadeTextBox(StartFadeTextBox);
        Timeline.FadeOutClicked += (_, _) => FocusFadeTextBox(EndFadeTextBox);
        Timeline.TimelineViewChanged += (_, _) =>
            _viewModel.ReportTimelineVisibleStart(Timeline.GetVisibleStartSeconds());

        LocationChanged += (_, _) => ReportWindowPlacementToCache();
        SizeChanged += (_, _) => ReportWindowPlacementToCache();
        StateChanged += (_, _) => ReportWindowPlacementToCache();

        Closing += (_, _) =>
        {
            if (_shutdownStarted)
                return;
            _shutdownStarted = true;

            ReportWindowPlacementToCache(forcePersist: true);

            // Detach the host before tearing down LibVLC / cancelling ffmpeg.
            try
            {
                VideoView.MediaPlayer = null;
            }
            catch (ObjectDisposedException)
            {
                // Ignore detach failures during shutdown.
            }
            catch (InvalidOperationException)
            {
                // Ignore detach failures during shutdown.
            }
            catch (COMException)
            {
                // Ignore detach failures during shutdown.
            }

            Dispose();
        };

        Closed += (_, _) =>
        {
            // LibVLC native threads can keep the process alive after the window is gone.
            Environment.Exit(0);
        };

        // LibVLC VideoView needs MediaPlayer assigned after load in some hosts
        Loaded += async (_, _) =>
        {
            VideoView.MediaPlayer ??= _viewModel.Player.MediaPlayer;
            ReportWindowPlacementToCache();

            var path = TryGetVideoPathFromArgs(Environment.GetCommandLineArgs());
            if (path is not null)
                await _viewModel.OpenVideoPathAsync(path).ConfigureAwait(true);
        };
    }

    public IRelayCommand ZoomToAbridgedContentCommand => _zoomToAbridgedContentCommand;

    public IRelayCommand ZoomToFitCommand => _zoomToFitCommand;

    private void ApplyPendingTimelineView()
    {
        if (!IsLoaded)
            return;

        // Wait until Edit/Preview layout and zoom bounds have settled.
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.PendingForceTimelineFit)
            {
                Timeline.ZoomToFit();
                _viewModel.ReportTimelineVisibleStart(Timeline.GetVisibleStartSeconds());
                _viewModel.ClearPendingTimelineViewRequests();
                return;
            }

            if (_viewModel.PendingVisibleStartSeconds is not { } start)
                return;

            Timeline.UpdateLayout();
            Timeline.SetVisibleStartSeconds(start);
            _viewModel.ReportTimelineVisibleStart(Timeline.GetVisibleStartSeconds());
            _viewModel.ClearPendingTimelineViewRequests();
        }, DispatcherPriority.Loaded);
    }

    private void ReportWindowPlacementToCache(bool forcePersist = false)
    {
        if (_applyingWindowPlacement || !IsLoaded)
            return;

        // Persist restore bounds while maximized/minimized so reopen can return to the right place.
        var left = Left;
        var top = Top;
        var width = Width;
        var height = Height;
        if (WindowState != WindowState.Normal && RestoreBounds.Width > 0 && RestoreBounds.Height > 0)
        {
            left = RestoreBounds.Left;
            top = RestoreBounds.Top;
            width = RestoreBounds.Width;
            height = RestoreBounds.Height;
        }

        if (width < MinWidth || height < MinHeight)
            return;

        _viewModel.ReportWindowPlacement(
            new WindowPlacementDto
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                State = WindowState.ToString()
            },
            forcePersist);
    }

    private void ApplyPendingWindowPlacement()
    {
        if (!IsLoaded)
            return;

        var placement = _viewModel.PendingWindowPlacement;
        if (placement is null)
        {
            // Seed current bounds into the per-video cache when none were stored.
            ReportWindowPlacementToCache();
            return;
        }

        if (placement.Width < MinWidth || placement.Height < MinHeight)
            return;

        var left = placement.Left;
        var top = placement.Top;
        var width = placement.Width;
        var height = placement.Height;
        ClampToWorkArea(ref left, ref top, ref width, ref height);

        _applyingWindowPlacement = true;
        try
        {
            WindowState = WindowState.Normal;
            Left = left;
            Top = top;
            Width = width;
            Height = height;

            if (Enum.TryParse<WindowState>(placement.State, ignoreCase: true, out var state)
                && state is WindowState.Maximized or WindowState.Minimized or WindowState.Normal)
            {
                WindowState = state;
            }
        }
        finally
        {
            _applyingWindowPlacement = false;
        }

        ReportWindowPlacementToCache();
    }

    private static void ClampToWorkArea(ref double left, ref double top, ref double width, ref double height)
    {
        var work = SystemParameters.WorkArea;
        width = Math.Clamp(width, 0, work.Width);
        height = Math.Clamp(height, 0, work.Height);
        left = Math.Clamp(left, work.Left, work.Right - Math.Min(width, work.Width));
        top = Math.Clamp(top, work.Top, work.Bottom - Math.Min(height, work.Height));
    }

    private void VersionTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionTabViewModel tab })
            _viewModel.SelectVersionCommand.Execute(tab.Id);
    }

    private void VersionTabClose_Click(object sender, RoutedEventArgs e) => e.Handled = true;

    private static void FocusFadeTextBox(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private void DurationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
            return;

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (IsTextInputFocused())
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        var mods = Keyboard.Modifiers;
        var handled = mods switch
        {
            ModifierKeys.Control | ModifierKeys.Alt when e.Key is Key.Home =>
                TryExecute(_viewModel.RestoreSectionFromStartCommand),
            ModifierKeys.Control | ModifierKeys.Alt when e.Key is Key.End =>
                TryExecute(_viewModel.RestoreSectionToEndCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.D =>
                TryExecute(_viewModel.RestoreSectionCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.Home =>
                TryExecute(_viewModel.DeleteSectionFromStartCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.End =>
                TryExecute(_viewModel.DeleteSectionToEndCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.S =>
                TryExecute(_viewModel.DetectScenesCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.N =>
                TryExecute(_viewModel.DuplicateVersionCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.T =>
                TryExecute(_viewModel.ToggleTasksPaneCommand),
            ModifierKeys.Control when e.Key is Key.O =>
                TryExecute(_viewModel.OpenVideoCommand),
            ModifierKeys.Control when e.Key is Key.E =>
                TryExecute(_viewModel.ExportCommand),
            ModifierKeys.Control when e.Key is Key.D =>
                TryExecute(_viewModel.DeleteSectionCommand),
            ModifierKeys.Control when e.Key is Key.N =>
                TryExecute(_viewModel.CreateVersionCommand),
            ModifierKeys.Control when e.Key is Key.Z =>
                TryExecute(_viewModel.UndoCommand),
            ModifierKeys.Control when e.Key is Key.Y =>
                TryExecute(_viewModel.RedoCommand),
            ModifierKeys.Control when e.Key is Key.D1 or Key.NumPad1 =>
                TryExecute(ZoomToAbridgedContentCommand),
            ModifierKeys.Control when e.Key is Key.D0 or Key.NumPad0 =>
                TryExecute(ZoomToFitCommand),
            ModifierKeys.None when e.Key is Key.Space =>
                TryExecute(_viewModel.PlayPauseCommand),
            ModifierKeys.None when e.Key is Key.Home =>
                TryExecute(_viewModel.GoToBeginningCommand),
            ModifierKeys.None when e.Key is Key.M =>
                TryExecute(_viewModel.AddMarkerAtPlayheadCommand),
            ModifierKeys.None when e.Key is Key.Delete =>
                TryExecute(_viewModel.DeleteMarkerCommand),
            ModifierKeys.None when e.Key is Key.Tab =>
                TryExecute(_viewModel.TogglePreviewModeCommand),
            ModifierKeys.None when e.Key is Key.F2 =>
                TryExecute(_viewModel.RenameVersionCommand),
            _ => false
        };

        if (handled)
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private static bool TryExecute(ICommand? command)
    {
        if (command is null || !command.CanExecute(null))
            return false;
        command.Execute(null);
        return true;
    }

    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is TextBox
            || focused is PasswordBox
            || focused is ComboBox { IsEditable: true };
    }

    private void PreviewHost_MouseEnter(object sender, MouseEventArgs e) => ShowPreviewChrome();

    private void PreviewHost_MouseMove(object sender, MouseEventArgs e) => ShowPreviewChrome();

    private void PreviewHost_MouseLeave(object sender, MouseEventArgs e) => HidePreviewChrome();

    private void ShowPreviewChrome()
    {
        if (!_previewChromeVisible)
        {
            _previewChromeVisible = true;
            PlayPauseOverlayButton.Opacity = 1;
            PlayPauseOverlayButton.IsHitTestVisible = true;
        }

        _previewChromeHideTimer.Stop();
        _previewChromeHideTimer.Start();
    }

    private void HidePreviewChrome()
    {
        _previewChromeHideTimer.Stop();
        if (!_previewChromeVisible)
            return;

        _previewChromeVisible = false;
        PlayPauseOverlayButton.Opacity = 0;
        PlayPauseOverlayButton.IsHitTestVisible = false;
    }

    internal static string? TryGetVideoPathFromArgs(string[] args)
    {
        // args[0] is the executable; take the first existing file path after that.
        foreach (var arg in args.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-', StringComparison.Ordinal))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(arg.Trim('"'));
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch (ArgumentException)
            {
                // Ignore malformed paths from the command line.
            }
            catch (NotSupportedException)
            {
                // Ignore malformed paths from the command line.
            }
            catch (PathTooLongException)
            {
                // Ignore malformed paths from the command line.
            }
        }

        return null;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
            _viewModel.Dispose();

        _disposed = true;
    }
}
