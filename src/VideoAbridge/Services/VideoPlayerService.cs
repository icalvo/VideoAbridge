using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace VideoAbridge.Services;

internal sealed class VideoPlayerService : IDisposable
{
    // CA2213: disposed on a worker thread in Dispose — see DisposeOwnedVlcResources.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "LibVLC Stop/Dispose on the UI thread deadlocks; owned resources are disposed in DisposeOwnedVlcResources via Task.Run.")]
    private readonly LibVLC _libVlc;
    private MediaPlayer? _mediaPlayer;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "LibVLC Stop/Dispose on the UI thread deadlocks; owned resources are disposed in DisposeOwnedVlcResources via Task.Run.")]
    private Media? _media;
    private EventHandler<EventArgs>? _firstFrameHandler;
    private bool _userPaused;

    public VideoPlayerService()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
    }

    public MediaPlayer MediaPlayer => _mediaPlayer ??= new MediaPlayer(_libVlc);

    public event EventHandler<TimeSpan>? TimeChanged;
    public event EventHandler? EndReached;

    public void Load(string path)
    {
        _media?.Dispose();
        _media = new Media(_libVlc, path, FromType.FromPath);
        MediaPlayer.Media = _media;
        MediaPlayer.TimeChanged -= OnTimeChanged;
        MediaPlayer.TimeChanged += OnTimeChanged;
        MediaPlayer.EndReached -= OnEndReached;
        MediaPlayer.EndReached += OnEndReached;
        _userPaused = true;
        ShowFirstFrame();
    }

    public void Play()
    {
        _userPaused = false;
        // After ShowFirstFrame we are paused via SetPause; resume with SetPause(false).
        // Play() alone is unreliable from that state in LibVLC.
        if (MediaPlayer.WillPlay || MediaPlayer.IsPlaying)
            MediaPlayer.SetPause(false);
        else
            MediaPlayer.Play();
    }

    public void Pause()
    {
        _userPaused = true;
        MediaPlayer.SetPause(true);
    }

    public void Stop() => MediaPlayer.Stop();

    public void SetVolume(int volume0To100)
    {
        try
        {
            MediaPlayer.Volume = Math.Clamp(volume0To100, 0, 100);
        }
        catch (ObjectDisposedException)
        {
            // Ignore volume during teardown.
        }
        catch (InvalidOperationException)
        {
            // Ignore volume during teardown.
        }
        catch (COMException)
        {
            // Ignore volume during teardown.
        }
    }

    public void Seek(TimeSpan time)
    {
        var ms = (long)time.TotalMilliseconds;
        // Setting Time on the UI thread can block forever against LibVLC's callback/UI marshaling.
        _ = Task.Run(() =>
        {
            try
            {
                MediaPlayer.Time = ms;
            }
            catch (ObjectDisposedException)
            {
                // Ignore seek failures from disposed/replaced media.
            }
            catch (InvalidOperationException)
            {
                // Ignore seek failures from disposed/replaced media.
            }
            catch (COMException)
            {
                // Ignore seek failures from disposed/replaced media.
            }
        });
    }

    /// <summary>True when media is actively playing (not the paused first-frame preview).</summary>
    public bool IsPlaying => !_userPaused && MediaPlayer.IsPlaying;

    /// <summary>Current media time, or false if the player is not ready.</summary>
    public bool TryGetTime(out TimeSpan time)
    {
        try
        {
            if (_mediaPlayer is null)
            {
                time = default;
                return false;
            }

            var ms = _mediaPlayer.Time;
            if (ms < 0)
            {
                time = default;
                return false;
            }

            time = TimeSpan.FromMilliseconds(ms);
            return true;
        }
        catch (ObjectDisposedException)
        {
            time = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            time = default;
            return false;
        }
        catch (COMException)
        {
            time = default;
            return false;
        }
    }

    private void ShowFirstFrame()
    {
        // LibVLC only renders after Play; pause as soon as playback starts so frame 0 is visible.
        if (_firstFrameHandler is not null)
            MediaPlayer.Playing -= _firstFrameHandler;

        _firstFrameHandler = (_, _) =>
        {
            if (_firstFrameHandler is not null)
                MediaPlayer.Playing -= _firstFrameHandler;
            _firstFrameHandler = null;

            // Do not set MediaPlayer.Time here — mutating Time on the Playing callback
            // thread can wedge LibVLC (same deadlock class as UI-thread Seek).
            MediaPlayer.SetPause(true);
            _userPaused = true;
        };

        MediaPlayer.Playing += _firstFrameHandler;
        MediaPlayer.Play();
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        TimeChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Time));

    private void OnEndReached(object? sender, EventArgs e) => EndReached?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_mediaPlayer is not null)
        {
            if (_firstFrameHandler is not null)
                _mediaPlayer.Playing -= _firstFrameHandler;
            _firstFrameHandler = null;
            _mediaPlayer.TimeChanged -= OnTimeChanged;
            _mediaPlayer.EndReached -= OnEndReached;
        }

        // Stop/Dispose on a worker thread — doing this on the UI thread deadlocks like Seek.
        _ = Task.Run(DisposeOwnedVlcResources);
    }

    private void DisposeOwnedVlcResources()
    {
        try
        {
            _mediaPlayer?.Stop();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }

        try
        {
            _mediaPlayer?.Media = null;
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }

        try
        {
            _mediaPlayer?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }

        _mediaPlayer = null;

        try
        {
            _media?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }

        _media = null;

        try
        {
            _libVlc.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
    }
}
