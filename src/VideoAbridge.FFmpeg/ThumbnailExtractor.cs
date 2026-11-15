using System.Globalization;

namespace VideoAbridge.FFmpeg;

public enum ThumbnailSide
{
    Before,
    After
}

public sealed class ThumbnailExtractor
{
    private readonly string _ffmpegPath;
    private readonly string _cacheDirectory;

    public ThumbnailExtractor(string? ffmpegPath = null, string? cacheDirectory = null)
    {
        _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("ffmpeg was not found.");
        _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "VideoAbridge", "thumbs");
        Directory.CreateDirectory(_cacheDirectory);
    }

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

        var key = $"{Path.GetFileName(videoPath)}_{seek.TotalMilliseconds:0}_{side}".GetHashCode(StringComparison.Ordinal);
        var output = Path.Combine(_cacheDirectory, $"{key:X8}.jpg");
        if (File.Exists(output))
            return output;

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
            cancellationToken).ConfigureAwait(false);

        return result.Success && File.Exists(output) ? output : null;
    }
}
