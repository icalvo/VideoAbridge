using LibVLCSharp.Shared;

namespace VideoAbridge.Services;

public sealed class VideoPlayerService : IDisposable
{
    private readonly LibVLC _libVlc;
    private MediaPlayer? _mediaPlayer;
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
        try { MediaPlayer.Volume = Math.Clamp(volume0To100, 0, 100); }
        catch { /* ignore */ }
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
            catch
            {
                // Ignore seek failures from disposed/replaced media.
            }
        });
    }

    /// <summary>True when media is actively playing (not the paused first-frame preview).</summary>
    public bool IsPlaying => !_userPaused && MediaPlayer.IsPlaying;

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
        var player = _mediaPlayer;
        var media = _media;
        var libVlc = _libVlc;
        _mediaPlayer = null;
        _media = null;

        if (player is not null)
        {
            if (_firstFrameHandler is not null)
                player.Playing -= _firstFrameHandler;
            _firstFrameHandler = null;
            player.TimeChanged -= OnTimeChanged;
            player.EndReached -= OnEndReached;
        }

        // Stop/Dispose on a worker thread — doing this on the UI thread deadlocks like Seek.
        _ = Task.Run(() =>
        {
            try { player?.Stop(); } catch { /* ignore */ }
            try { if (player is not null) player.Media = null; } catch { /* ignore */ }
            try { player?.Dispose(); } catch { /* ignore */ }
            try { media?.Dispose(); } catch { /* ignore */ }
            try { libVlc.Dispose(); } catch { /* ignore */ }
        });
    }
}
