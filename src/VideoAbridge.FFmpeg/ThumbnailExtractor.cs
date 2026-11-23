using System.Globalization;

namespace VideoAbridge.FFmpeg;

public sealed class ThumbnailExtractor
{
    private readonly string _ffmpegPath;
    private readonly string _cacheDirectory;

    public ThumbnailExtractor(string? ffmpegPath = null, string? cacheDirectory = null)
    {
        _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("ffmpeg was not found.");
        _cacheDirectory = cacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoAbridge",
                "ThumbCache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    public async Task<string?> ExtractAsync(
        string videoPath,
        TimeSpan markerTime,
        ThumbnailSide side,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var epsilon = TimeSpan.FromMilliseconds(40);
        TimeSpan seek;
        if (side == ThumbnailSide.Before)
        {
            seek = markerTime <= epsilon ? TimeSpan.Zero : markerTime - epsilon;
        }
        else
        {
            seek = markerTime;
            if (seek >= duration)
                seek = duration - epsilon;
            if (seek < TimeSpan.Zero)
                seek = TimeSpan.Zero;
        }

        var output = GetCachePath(videoPath, seek, side, duration);
        if (output is null)
            return null;
        if (File.Exists(output))
            return output;

        var cacheDir = Path.GetDirectoryName(output);
        if (cacheDir is not null)
            Directory.CreateDirectory(cacheDir);

        var ss = seek.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        var result = await ProcessRunner.RunAsync(
            _ffmpegPath,
            [
                "-hide_banner",
                "-ss", ss,
                "-i", videoPath,
                "-frames:v", "1",
                "-q:v", "4",
                "-y",
                output
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Success && File.Exists(output) ? output : null;
    }

    /// <summary>
    /// Stable on-disk path keyed by file-content fingerprint + seek time + side
    /// (under %LocalAppData%\VideoAbridge\ThumbCache).
    /// </summary>
    public string? GetCachePath(string videoPath, TimeSpan seek, ThumbnailSide side, TimeSpan duration)
    {
        var fullPath = Path.GetFullPath(videoPath);
        return FileContentId.Compute(fullPath, duration).Match<string?>(
            videoKey =>
            {
                var fileName = $"{(long)Math.Round(seek.TotalMilliseconds)}_{side}.jpg";
                return Path.Combine(_cacheDirectory, videoKey, fileName);
            },
            _ => null);
    }
}
