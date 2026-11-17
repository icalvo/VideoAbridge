using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VideoAbridge.ViewModels;

namespace VideoAbridge;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PreviewChromeHideDelay = TimeSpan.FromSeconds(2);

    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _previewChromeHideTimer;
    private TasksWindow? _tasksWindow;
    private bool _shutdownStarted;
    private bool _previewChromeVisible;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _previewChromeHideTimer = new DispatcherTimer { Interval = PreviewChromeHideDelay };
        _previewChromeHideTimer.Tick += (_, _) => HidePreviewChrome();

        Timeline.SeekRequested += (_, time) => _viewModel.SeekPlayhead(time);
        Timeline.MarkerClicked += (_, id) => _viewModel.ToggleMarkerSelection(id);
        Timeline.DeletedRunSelected += (_, id) => _viewModel.SelectDeletedRunCommand.Execute(id);

        _viewModel.RequestShowTasksWindow = ShowOrActivateTasksWindow;
        _viewModel.RequestCloseTasksWindow = CloseTasksWindow;

        Closing += (_, _) =>
        {
            if (_shutdownStarted)
                return;
            _shutdownStarted = true;

            // Detach the host before tearing down LibVLC / cancelling ffmpeg.
            try
            {
                VideoView.MediaPlayer = null;
            }
            catch
            {
                // Ignore detach failures during shutdown.
            }

            try
            {
                _tasksWindow?.Close();
            }
            catch
            {
                // Ignore.
            }

            _viewModel.Dispose();
        };

        Closed += (_, _) =>
        {
            // LibVLC native threads can keep the process alive after the window is gone.
            Environment.Exit(0);
        };

        // LibVLC VideoView needs MediaPlayer assigned after load in some hosts
        Loaded += async (_, _) =>
        {
            if (VideoView.MediaPlayer is null)
                VideoView.MediaPlayer = _viewModel.Player.MediaPlayer;

            var path = TryGetVideoPathFromArgs(Environment.GetCommandLineArgs());
            if (path is not null)
                await _viewModel.OpenVideoPathAsync(path);
        };
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

    private void ShowOrActivateTasksWindow()
    {
        if (_tasksWindow is null)
        {
            _tasksWindow = new TasksWindow
            {
                Owner = this,
                DataContext = _viewModel
            };
            _tasksWindow.Closed += (_, _) => _tasksWindow = null;
            _tasksWindow.Show();
        }
        else
        {
            if (!_tasksWindow.IsVisible)
                _tasksWindow.Show();
            _tasksWindow.Activate();
        }
    }

    private void CloseTasksWindow()
    {
        try
        {
            _tasksWindow?.Close();
        }
        catch
        {
            // Ignore close failures during shutdown/teardown.
        }
    }

    internal static string? TryGetVideoPathFromArgs(string[] args)
    {
        // args[0] is the executable; take the first existing file path after that.
        foreach (var arg in args.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(arg.Trim('"'));
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch
            {
                // Ignore malformed paths from the command line.
            }
        }

        return null;
    }
}
