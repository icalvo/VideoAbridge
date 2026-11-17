using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoAbridge.FFmpeg;

public sealed class SceneDetector
{
    /// <summary>Downscale width used before scoring; keeps hard-cut quality while cutting decode cost.</summary>
    public const int AnalysisWidth = 640;

    /// <summary>scdet threshold on a 0–100 scale. Docs recommend roughly 8–14; 10 is ffmpeg's default.</summary>
    public const double DefaultThreshold = 10;

    private static readonly Regex ScdTimeRegex = new(
        @"lavfi\.scd\.time:\s*(?<t>\d+(?:[.:]\d+)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _ffmpegPath;

    public SceneDetector(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("ffmpeg was not found.");
    }

    public async Task DetectAsync(
        string videoPath,
        double threshold = DefaultThreshold,
        TimeSpan? duration = null,
        TimeSpan startAt = default,
        IProgress<double>? progress = null,
        IProgress<TimeSpan>? sceneFound = null,
        IProgress<TimeSpan>? positionReached = null,
        CancellationToken cancellationToken = default)
    {
        var thresholdText = threshold.ToString("0.###", CultureInfo.InvariantCulture);
        var totalSeconds = duration?.TotalSeconds ?? 0;
        var startSeconds = Math.Max(0, startAt.TotalSeconds);
        var seenMs = new HashSet<long>();
        var filter =
            $"scale={AnalysisWidth}:-2:flags=area,scdet=threshold={thresholdText}";

        // -ss BEFORE -i so decode/scdet actually starts at the cached position.
        // ( -ss after -i still feeds the filter from t=0 and only discards later.)
        var args = new List<string> { "-hide_banner", "-an" };
        if (startSeconds > 0.05)
        {
            args.Add("-ss");
            args.Add(startSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "-i", videoPath,
            "-filter:v", filter,
            "-f", "null",
            "-"
        ]);

        if (startSeconds > 0)
        {
            positionReached?.Report(TimeSpan.FromSeconds(startSeconds));
            ReportProgress(progress, startSeconds, totalSeconds);
        }

        var result = await ProcessRunner.RunAsync(
            _ffmpegPath,
            args,
            cancellationToken,
            line =>
            {
                foreach (Match match in ScdTimeRegex.Matches(line))
                {
                    if (!TryParseTimestamp(match.Groups["t"].Value, out var filterSeconds))
                        continue;

                    // With -ss before -i, lavfi timestamps restart at 0 — map back to file time.
                    var absolute = filterSeconds + startSeconds;
                    if (absolute <= startSeconds + 0.02)
                        continue;

                    ReportScene(
                        TimeSpan.FromSeconds(absolute),
                        seenMs,
                        sceneFound,
                        progress,
                        positionReached,
                        totalSeconds);
                }

                ReportProgressFromTimeToken(progress, positionReached, line, totalSeconds, startSeconds);
            }).ConfigureAwait(false);

        // Fallback if line-splitting missed cuts (still report them live-style to the caller).
        if (seenMs.Count == 0)
        {
            foreach (Match match in ScdTimeRegex.Matches(result.StandardError))
            {
                if (!TryParseTimestamp(match.Groups["t"].Value, out var filterSeconds))
                    continue;

                var absolute = filterSeconds + startSeconds;
                if (absolute <= startSeconds + 0.02)
                    continue;

                ReportScene(
                    TimeSpan.FromSeconds(absolute),
                    seenMs,
                    sceneFound,
                    progress,
                    positionReached,
                    totalSeconds);
            }
        }

        if (totalSeconds > 0)
            positionReached?.Report(TimeSpan.FromSeconds(totalSeconds));
        progress?.Report(1);
    }

    private static void ReportScene(
        TimeSpan time,
        HashSet<long> seenMs,
        IProgress<TimeSpan>? sceneFound,
        IProgress<double>? progress,
        IProgress<TimeSpan>? positionReached,
        double totalSeconds)
    {
        if (!seenMs.Add((long)Math.Round(time.TotalMilliseconds)))
            return;

        sceneFound?.Report(time);
        positionReached?.Report(time);
        ReportProgress(progress, time.TotalSeconds, totalSeconds);
    }

    private static bool TryParseTimestamp(string token, out double seconds)
    {
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return true;

        if (TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out var time))
        {
            seconds = time.TotalSeconds;
            return true;
        }

        seconds = 0;
        return false;
    }

    private static void ReportProgressFromTimeToken(
        IProgress<double>? progress,
        IProgress<TimeSpan>? positionReached,
        string line,
        double totalSeconds,
        double startSeconds)
    {
        var idx = line.IndexOf("time=", StringComparison.Ordinal);
        if (idx < 0)
            return;

        var span = line.AsSpan(idx + 5);
        var end = span.IndexOf(' ');
        var token = end < 0 ? span : span[..end];
        if (!TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out var current))
            return;

        // Progress time= is relative to the post-seek stream when -ss is before -i.
        var absolute = current.TotalSeconds + startSeconds;
        positionReached?.Report(TimeSpan.FromSeconds(absolute));
        ReportProgress(progress, absolute, totalSeconds);
    }

    private static void ReportProgress(IProgress<double>? progress, double currentSeconds, double totalSeconds)
    {
        if (progress is null || totalSeconds <= 0)
            return;

        progress.Report(Math.Clamp(currentSeconds / totalSeconds, 0, 1));
    }
}
