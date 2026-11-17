using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VideoAbridge.ViewModels;

namespace VideoAbridge;

public partial class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan PreviewChromeHideDelay = TimeSpan.FromSeconds(2);

    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _previewChromeHideTimer;
    private bool _shutdownStarted;
    private bool _previewChromeVisible;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _previewChromeHideTimer = new DispatcherTimer { Interval = PreviewChromeHideDelay };
        _previewChromeHideTimer.Tick += (_, _) => HidePreviewChrome();

        Timeline.SeekRequested += (_, e) =>
        {
            if (_viewModel.IsPreviewMode)
                _viewModel.SeekAbridgedPlayhead(e.Time);
            else
                _viewModel.SeekPlayhead(e.Time);
        };
        Timeline.MarkerClicked += (_, e) => _viewModel.ToggleMarkerSelection(e.Id);
        Timeline.DeletedRunSelected += (_, e) => _viewModel.SelectDeletedRunCommand.Execute(e.Id);
        Timeline.FadeInClicked += (_, _) => FocusFadeTextBox(StartFadeTextBox);
        Timeline.FadeOutClicked += (_, _) => FocusFadeTextBox(EndFadeTextBox);

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

            var path = TryGetVideoPathFromArgs(Environment.GetCommandLineArgs());
            if (path is not null)
                await _viewModel.OpenVideoPathAsync(path).ConfigureAwait(true);
        };
    }

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

        if (!IsTextInputFocused())
        {
            if (e.Key == Key.Space)
            {
                if (_viewModel.PlayPauseCommand.CanExecute(null))
                {
                    _viewModel.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewModel.UndoCommand.CanExecute(null))
                {
                    _viewModel.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewModel.RedoCommand.CanExecute(null))
                {
                    _viewModel.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
        }

        base.OnPreviewKeyDown(e);
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
