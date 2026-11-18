using System.Globalization;
using System.Text.Json;

namespace VideoAbridge.FFmpeg;

public sealed class MediaProbe(string? ffprobePath = null)
{
    private readonly string _ffprobePath = ffprobePath ?? FFmpegLocator.FindFFprobe()
        ?? throw new InvalidOperationException("ffprobe was not found.");

    public async Task<MediaInfo> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            _ffprobePath,
            [
                "-v", "quiet",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                videoPath
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"ffprobe failed: {result.StandardError}");

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;

        double durationSeconds = 0;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durProp)
            && double.TryParse(durProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            durationSeconds = parsed;
        }

        var width = 0;
        var height = 0;
        double fps = 25;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    if (stream.TryGetProperty("avg_frame_rate", out var fr)
                        && TryParseFrameRate(fr.GetString(), out var rate))
                    {
                        fps = rate;
                    }

                    break;
                }
            }
        }

        return new MediaInfo(TimeSpan.FromSeconds(durationSeconds), width, height, fps);
    }

    private static bool TryParseFrameRate(string? value, out double fps)
    {
        fps = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den != 0)
        {
            fps = num / den;
            return true;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
    }
}
