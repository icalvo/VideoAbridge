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
    private Point? _versionTabDragStart;
    private bool _versionTabDragMoved;

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
                if (_viewModel.HasVideoLoaded && !_viewModel.IsPreviewMode)
                    Dispatcher.BeginInvoke(FocusTimeline, DispatcherPriority.Loaded);
            }

            if (e.PropertyName is nameof(MainViewModel.IsPreviewMode)
                && !_viewModel.IsPreviewMode
                && _viewModel.HasVideoLoaded)
            {
                Dispatcher.BeginInvoke(FocusTimeline, DispatcherPriority.Input);
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

    private void VersionTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionTabViewModel tab } || tab.IsEditing)
        {
            ClearVersionTabDrag();
            return;
        }

        if (e.ClickCount == 2)
        {
            ClearVersionTabDrag();
            if (!tab.IsActive && _viewModel.SelectVersionCommand.CanExecute(tab.Id))
                _viewModel.SelectVersionCommand.Execute(tab.Id);

            _viewModel.BeginInlineRename(tab.Id);
            e.Handled = true;
            return;
        }

        _versionTabDragStart = e.GetPosition(this);
        _versionTabDragMoved = false;
    }

    private void VersionTab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_versionTabDragStart is null
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element
            || element.DataContext is not VersionTabViewModel tab
            || tab.IsEditing)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var start = _versionTabDragStart.Value;
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _versionTabDragMoved = true;
        var data = new DataObject(typeof(Guid), tab.Id);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
        ClearVersionTabDrag();
    }

    private void VersionTab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_versionTabDragMoved)
        {
            ClearVersionTabDrag();
            return;
        }

        ClearVersionTabDrag();
        if (sender is FrameworkElement { DataContext: VersionTabViewModel { IsEditing: false } tab })
            _viewModel.SelectVersionCommand.Execute(tab.Id);
    }

    private void VersionTab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetVersionDragId(e, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void VersionTabs_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetVersionDragId(e, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void VersionTab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionTabViewModel target }
            || !TryGetVersionDragId(e, out var sourceId))
        {
            return;
        }

        e.Handled = true;
        if (sourceId == target.Id)
            return;

        var targetIndex = IndexOfVersionTab(target.Id);
        if (targetIndex < 0)
            return;

        var pos = e.GetPosition((IInputElement)sender);
        if (sender is FrameworkElement fe && pos.X > fe.ActualWidth / 2)
            targetIndex++;

        var sourceIndex = IndexOfVersionTab(sourceId);
        if (sourceIndex < 0)
            return;

        if (sourceIndex < targetIndex)
            targetIndex--;

        if (targetIndex != sourceIndex)
            _viewModel.ReorderVersion(sourceId, targetIndex);
    }

    private void VersionTabs_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetVersionDragId(e, out var sourceId))
            return;

        e.Handled = true;
        var lastIndex = _viewModel.VersionTabs.Count - 1;
        if (lastIndex < 0)
            return;

        var sourceIndex = IndexOfVersionTab(sourceId);
        if (sourceIndex < 0 || sourceIndex == lastIndex)
            return;

        _viewModel.ReorderVersion(sourceId, lastIndex);
    }

    private void VersionTab_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void VersionTabRenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box)
            return;

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!box.IsVisible)
                    return;
                box.Focus();
                box.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void VersionTabRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: VersionTabViewModel tab } box)
            return;

        if (e.Key == Key.Enter)
        {
            if (_viewModel.CommitVersionRename(tab))
                Keyboard.ClearFocus();
            else
            {
                box.Focus();
                box.SelectAll();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tab.CancelEdit();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void VersionTabRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: VersionTabViewModel { IsEditing: true } tab } box)
            return;

        if (_viewModel.CommitVersionRename(tab))
            return;

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!tab.IsEditing)
                    return;
                box.Focus();
                box.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private int IndexOfVersionTab(Guid id)
    {
        for (var i = 0; i < _viewModel.VersionTabs.Count; i++)
        {
            if (_viewModel.VersionTabs[i].Id == id)
                return i;
        }

        return -1;
    }

    private static bool TryGetVersionDragId(DragEventArgs e, out Guid id)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Data.GetDataPresent(typeof(Guid)) && e.Data.GetData(typeof(Guid)) is Guid guid)
        {
            id = guid;
            return true;
        }

        id = default;
        return false;
    }

    private void ClearVersionTabDrag()
    {
        _versionTabDragStart = null;
        _versionTabDragMoved = false;
    }

    private void VersionTabClose_Click(object sender, RoutedEventArgs e) => e.Handled = true;

    private static void FocusFadeTextBox(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private void FocusTimeline()
    {
        if (!_viewModel.HasVideoLoaded || _viewModel.IsPreviewMode)
            return;
        Timeline.Focus();
    }

    private void DurationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
            return;

        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        FocusTimeline();
        e.Handled = true;
    }

    private void SidebarDurationTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Defer so focus can settle on another textbox before reclaiming the timeline.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsTextInputFocused() || _viewModel.IsPreviewMode)
                return;
            FocusTimeline();
        }, DispatcherPriority.Input);
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

        if (!_viewModel.IsPreviewMode
            && Timeline.IsKeyboardFocusWithin
            && TryHandleTimelineFocusKeys(mods, e.Key))
        {
            e.Handled = true;
            return;
        }

        var handled = mods switch
        {
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.Tab =>
                TryExecute(_viewModel.SelectPreviousVersionCommand) || _viewModel.HasVideoLoaded,
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.Home =>
                TryExecute(_viewModel.RestoreSectionFromStartCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.End =>
                TryExecute(_viewModel.RestoreSectionToEndCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.N =>
                TryExecute(_viewModel.DuplicateVersionCommand),
            ModifierKeys.Control | ModifierKeys.Shift when e.Key is Key.T =>
                TryExecute(_viewModel.ToggleTasksPaneCommand),
            ModifierKeys.Control when e.Key is Key.Tab =>
                TryExecute(_viewModel.SelectNextVersionCommand) || _viewModel.HasVideoLoaded,
            ModifierKeys.Control when e.Key is Key.O =>
                TryExecute(_viewModel.OpenVideoCommand),
            ModifierKeys.Control when e.Key is Key.S =>
                TryExecute(_viewModel.ExportCommand),
            ModifierKeys.Control when e.Key is Key.D =>
                TryExecute(_viewModel.DetectScenesCommand),
            ModifierKeys.Control when e.Key is Key.Home =>
                TryExecute(_viewModel.DeleteSectionFromStartCommand),
            ModifierKeys.Control when e.Key is Key.End =>
                TryExecute(_viewModel.DeleteSectionToEndCommand),
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
            ModifierKeys.Shift when e.Key is Key.Delete =>
                TryExecute(_viewModel.RestoreSectionCommand),
            ModifierKeys.None when e.Key is Key.P =>
                TryExecute(_viewModel.PlayPauseCommand),
            ModifierKeys.None when e.Key is Key.Home =>
                TryExecute(_viewModel.GoToBeginningCommand),
            ModifierKeys.None when e.Key is Key.M =>
                TryExecute(_viewModel.AddMarkerAtPlayheadCommand),
            // Mutually exclusive CanExecute: 1 marker → delete marker; 2 markers → delete section.
            ModifierKeys.None when e.Key is Key.Delete =>
                TryExecute(_viewModel.DeleteSectionCommand)
                || TryExecute(_viewModel.DeleteMarkerCommand),
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

    private bool TryHandleTimelineFocusKeys(ModifierKeys mods, Key key)
    {
        if (mods == ModifierKeys.None)
        {
            if (key is Key.Left)
            {
                _viewModel.MoveTimelineFocusHorizontal(-1);
                return true;
            }

            if (key is Key.Right)
            {
                _viewModel.MoveTimelineFocusHorizontal(1);
                return true;
            }

            if (key is Key.Up or Key.Down)
            {
                _viewModel.MoveTimelineFocusVertical();
                return true;
            }

            if (key is Key.Space)
            {
                _viewModel.ActivateTimelineFocus(extendSelection: false);
                return true;
            }
        }
        else if (mods == ModifierKeys.Shift && key is Key.Space)
        {
            _viewModel.ActivateTimelineFocus(extendSelection: true);
            return true;
        }

        return false;
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
