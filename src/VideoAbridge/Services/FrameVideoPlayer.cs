using System.Diagnostics.CodeAnalysis;
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
internal sealed class FrameVideoPlayer : IDisposable
{
    // CA2213: disposed on a worker thread in Dispose — see DisposeOwnedVlcResources.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "LibVLC Stop/Dispose on the UI thread deadlocks; owned resources are disposed in DisposeOwnedVlcResources via Task.Run.")]
    private readonly LibVLC _libVlc;
    private readonly Dispatcher _dispatcher;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "LibVLC Stop/Dispose on the UI thread deadlocks; owned resources are disposed in DisposeOwnedVlcResources via Task.Run.")]
    private VlcMediaPlayer? _mediaPlayer;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "LibVLC Stop/Dispose on the UI thread deadlocks; owned resources are disposed in DisposeOwnedVlcResources via Task.Run.")]
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
        var player = EnsurePlayer();
        _media?.Dispose();
        _media = new VlcMedia(_libVlc, path, FromType.FromPath);
        player.Media = _media;
        AttachCallbacks();
        player.Play();
        player.SetPause(true);
    }

    public void Seek(TimeSpan time)
    {
        if (_mediaPlayer is null)
            return;
        var ms = (long)time.TotalMilliseconds;
        _ = Task.Run(() =>
        {
            try
            {
                _mediaPlayer.Time = ms;
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
        try
        {
            _mediaPlayer?.SetPause(true);
        }
        catch (ObjectDisposedException)
        {
            // Ignore pause during teardown.
        }
        catch (InvalidOperationException)
        {
            // Ignore pause during teardown.
        }
        catch (COMException)
        {
            // Ignore pause during teardown.
        }
    }

    public void SetVolume(int volume0To100)
    {
        if (_mediaPlayer is null)
            return;
        try
        {
            _mediaPlayer.Volume = Math.Clamp(volume0To100, 0, 100);
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

    private VlcMediaPlayer EnsurePlayer()
    {
        _mediaPlayer ??= new VlcMediaPlayer(_libVlc);
        return _mediaPlayer;
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
            catch (ObjectDisposedException)
            {
                // Ignore disposed bitmap races.
            }
            catch (InvalidOperationException)
            {
                // Ignore disposed bitmap races.
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

        // Stop/Dispose on a worker thread — doing this on the UI thread deadlocks like Seek.
        _ = Task.Run(DisposeOwnedVlcResources);

        if (_bufferHandle.IsAllocated)
            _bufferHandle.Free();
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
