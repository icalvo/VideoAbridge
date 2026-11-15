using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoAbridge.FFmpeg;

public sealed class SceneDetector
{
    private static readonly Regex PtsTimeRegex = new(
        @"pts_time:(?<t>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _ffmpegPath;

    public SceneDetector(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("ffmpeg was not found.");
    }

    public async Task<IReadOnlyList<TimeSpan>> DetectAsync(
        string videoPath,
        double threshold = 0.3,
        CancellationToken cancellationToken = default)
    {
        var thresholdText = threshold.ToString("0.###", CultureInfo.InvariantCulture);
        var result = await ProcessRunner.RunAsync(
            _ffmpegPath,
            [
                "-hide_banner",
                "-i", videoPath,
                "-filter:v", $"select='gt(scene,{thresholdText})',showinfo",
                "-f", "null",
                "-"
            ],
            cancellationToken).ConfigureAwait(false);

        // ffmpeg writes showinfo to stderr; exit code may be 0 even with detections
        var times = new List<TimeSpan>();
        foreach (Match match in PtsTimeRegex.Matches(result.StandardError))
        {
            if (double.TryParse(match.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                times.Add(TimeSpan.FromSeconds(seconds));
            }
        }

        return times
            .DistinctBy(t => Math.Round(t.TotalMilliseconds))
            .OrderBy(t => t)
            .ToList();
    }
}
