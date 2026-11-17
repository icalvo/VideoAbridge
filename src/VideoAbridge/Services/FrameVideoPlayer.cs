using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoAbridge.Services;

/// <summary>
/// Secondary LibVLC player that renders into a <see cref="WriteableBitmap"/>
/// so WPF opacity blending can show realtime crossfades over the primary HWND view.
/// </summary>
public sealed class FrameVideoPlayer : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly Dispatcher _dispatcher;
    private VlcMediaPlayer? _mediaPlayer;
    private VlcMedia? _media;
    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;
    private GCHandle _bufferHandle;
    private int _width;
    private int _height;
    private int _pitch;
    private bool _disposed;
    private bool _callbacksAttached;

    public FrameVideoPlayer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        global::LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
    }

    public WriteableBitmap? Bitmap => _bitmap;

    public event EventHandler? FrameUpdated;

    public void Load(string path)
    {
        ThrowIfDisposed();
        EnsurePlayer();
        _media?.Dispose();
        _media = new VlcMedia(_libVlc, path, FromType.FromPath);
        _mediaPlayer!.Media = _media;
        AttachCallbacks();
        _mediaPlayer.Play();
        _mediaPlayer.SetPause(true);
    }

    public void Seek(TimeSpan time)
    {
        if (_mediaPlayer is null)
            return;
        var ms = (long)time.TotalMilliseconds;
        _ = Task.Run(() =>
        {
            try { _mediaPlayer.Time = ms; }
            catch { /* ignore */ }
        });
    }

    public void Play()
    {
        if (_mediaPlayer is null)
            return;
        if (_mediaPlayer.WillPlay || _mediaPlayer.IsPlaying)
            _mediaPlayer.SetPause(false);
        else
            _mediaPlayer.Play();
    }

    public void Pause()
    {
        try { _mediaPlayer?.SetPause(true); }
        catch { /* ignore */ }
    }

    public void SetVolume(int volume0To100)
    {
        if (_mediaPlayer is null)
            return;
        try { _mediaPlayer.Volume = Math.Clamp(volume0To100, 0, 100); }
        catch { /* ignore */ }
    }

    private void EnsurePlayer()
    {
        if (_mediaPlayer is not null)
            return;
        _mediaPlayer = new VlcMediaPlayer(_libVlc);
    }

    private void AttachCallbacks()
    {
        if (_mediaPlayer is null || _callbacksAttached)
            return;

        _mediaPlayer.SetVideoFormatCallbacks(VideoFormat, Cleanup);
        _mediaPlayer.SetVideoCallbacks(Lock, null, Display);
        _callbacksAttached = true;
    }

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 = BGRA for WriteableBitmap Bgra32
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);

        _width = (int)width;
        _height = (int)height;
        _pitch = _width * 4;
        pitches = (uint)_pitch;
        lines = height;

        _buffer = new byte[_pitch * _height];
        if (_bufferHandle.IsAllocated)
            _bufferHandle.Free();
        _bufferHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);

        _dispatcher.Invoke(() =>
        {
            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        });

        return 1;
    }

    private void Cleanup(ref IntPtr opaque)
    {
        if (_bufferHandle.IsAllocated)
            _bufferHandle.Free();
        _buffer = null;
    }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        if (!_bufferHandle.IsAllocated)
            return IntPtr.Zero;
        Marshal.WriteIntPtr(planes, _bufferHandle.AddrOfPinnedObject());
        return IntPtr.Zero;
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        if (_buffer is null || _bitmap is null)
            return;

        var copy = _buffer;
        _dispatcher.BeginInvoke(() =>
        {
            if (_bitmap is null || _disposed)
                return;
            try
            {
                _bitmap.WritePixels(
                    new Int32Rect(0, 0, _width, _height),
                    copy,
                    _pitch,
                    0);
                FrameUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // ignore disposed bitmap races
            }
        });
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var player = _mediaPlayer;
        var media = _media;
        _mediaPlayer = null;
        _media = null;

        _ = Task.Run(() =>
        {
            try { player?.Stop(); } catch { /* ignore */ }
            try { if (player is not null) player.Media = null; } catch { /* ignore */ }
            try { player?.Dispose(); } catch { /* ignore */ }
            try { media?.Dispose(); } catch { /* ignore */ }
            try { _libVlc.Dispose(); } catch { /* ignore */ }
        });

        if (_bufferHandle.IsAllocated)
            _bufferHandle.Free();
    }
}
