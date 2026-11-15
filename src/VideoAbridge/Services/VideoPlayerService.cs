using LibVLCSharp.Shared;

namespace VideoAbridge.Services;

public sealed class VideoPlayerService : IDisposable
{
    private readonly LibVLC _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;

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
    }

    public void Play() => MediaPlayer.Play();

    public void Pause() => MediaPlayer.Pause();

    public void Stop() => MediaPlayer.Stop();

    public void Seek(TimeSpan time) => MediaPlayer.Time = (long)time.TotalMilliseconds;

    public bool IsPlaying => MediaPlayer.IsPlaying;

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        TimeChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Time));

    private void OnEndReached(object? sender, EventArgs e) => EndReached?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.TimeChanged -= OnTimeChanged;
            _mediaPlayer.EndReached -= OnEndReached;
            _mediaPlayer.Dispose();
        }

        _media?.Dispose();
        _libVlc.Dispose();
    }
}
