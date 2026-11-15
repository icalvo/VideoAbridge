using System.Globalization;
using System.Text;
using VideoAbridge.Core;

namespace VideoAbridge.FFmpeg;

public sealed class VideoExporter
{
    private readonly string _ffmpegPath;

    public VideoExporter(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
            ?? throw new InvalidOperationException("ffmpeg was not found.");
    }

    public async Task ExportAsync(
        string inputPath,
        string outputPath,
        ExportPlan plan,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan.Clips.Count == 0)
            throw new ArgumentException("Export plan has no clips.", nameof(plan));

        var args = BuildArguments(inputPath, outputPath, plan);
        var result = await ProcessRunner.RunAsync(
            _ffmpegPath,
            args,
            cancellationToken,
            line =>
            {
                // Best-effort progress from time= in stderr
                var idx = line.IndexOf("time=", StringComparison.Ordinal);
                if (idx < 0)
                    return;
                var span = line.AsSpan(idx + 5);
                var end = span.IndexOf(' ');
                var token = end < 0 ? span : span[..end];
                if (TimeSpan.TryParse(token, CultureInfo.InvariantCulture, out var current))
                {
                    var total = EstimateOutputDuration(plan).TotalSeconds;
                    if (total > 0)
                        progress?.Report(Math.Clamp(current.TotalSeconds / total, 0, 1));
                }
            }).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"Export failed: {result.StandardError}");

        progress?.Report(1);
    }

    public static List<string> BuildArguments(string inputPath, string outputPath, ExportPlan plan)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-y",
            "-i", inputPath
        };

        if (plan.Clips.Count == 1 && plan.JoinsBetweenClips.Count == 0 && !plan.FadeToBlackAtEnd)
        {
            var clip = plan.Clips[0];
            args.AddRange(
            [
                "-ss", Format(clip.Start),
                "-to", Format(clip.End),
                "-c", "copy",
                outputPath
            ]);
            return args;
        }

        var filter = BuildFilterComplex(plan);
        args.AddRange(
        [
            "-filter_complex", filter,
            "-map", "[vout]",
            "-map", "[aout]",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "18",
            "-c:a", "aac",
            "-b:a", "192k",
            "-movflags", "+faststart",
            outputPath
        ]);
        return args;
    }

    public static string BuildFilterComplex(ExportPlan plan)
    {
        var sb = new StringBuilder();
        var n = plan.Clips.Count;

        for (var i = 0; i < n; i++)
        {
            var clip = plan.Clips[i];
            var start = Format(clip.Start);
            var end = Format(clip.End);
            sb.Append(CultureInfo.InvariantCulture,
                $"[0:v]trim=start={start}:end={end},setpts=PTS-STARTPTS,format=yuv420p,settb=AVTB[v{i}];");
            sb.Append(CultureInfo.InvariantCulture,
                $"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
        }

        string vCurrent = "v0";
        string aCurrent = "a0";
        var accumulated = plan.Clips[0].Duration.TotalSeconds;

        for (var i = 0; i < plan.JoinsBetweenClips.Count; i++)
        {
            var join = plan.JoinsBetweenClips[i];
            var nextV = $"v{i + 1}";
            var nextA = $"a{i + 1}";
            var outV = $"vx{i}";
            var outA = $"ax{i}";

            if (join.Mode == TransitionMode.HardCut || join.CrossfadeSeconds <= 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"[{vCurrent}][{nextV}]concat=n=2:v=1:a=0[{outV}];");
                sb.Append(CultureInfo.InvariantCulture, $"[{aCurrent}][{nextA}]concat=n=2:v=0:a=1[{outA}];");
                accumulated += plan.Clips[i + 1].Duration.TotalSeconds;
            }
            else
            {
                var d = join.CrossfadeSeconds;
                var offset = Math.Max(0, accumulated - d);
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{vCurrent}][{nextV}]xfade=transition=fade:duration={FormatDouble(d)}:offset={FormatDouble(offset)}[{outV}];");
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{aCurrent}][{nextA}]acrossfade=d={FormatDouble(d)}[{outA}];");
                accumulated = offset + plan.Clips[i + 1].Duration.TotalSeconds;
            }

            vCurrent = outV;
            aCurrent = outA;
        }

        if (plan.FadeToBlackAtEnd && plan.FadeToBlackSeconds > 0)
        {
            var fade = plan.FadeToBlackSeconds;
            var fadeStart = Math.Max(0, accumulated - fade);
            sb.Append(CultureInfo.InvariantCulture,
                $"[{vCurrent}]fade=t=out:st={FormatDouble(fadeStart)}:d={FormatDouble(fade)}[vout];");
            sb.Append(CultureInfo.InvariantCulture,
                $"[{aCurrent}]afade=t=out:st={FormatDouble(fadeStart)}:d={FormatDouble(fade)}[aout]");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{vCurrent}]null[vout];");
            sb.Append(CultureInfo.InvariantCulture, $"[{aCurrent}]anull[aout]");
        }

        return sb.ToString();
    }

    private static TimeSpan EstimateOutputDuration(ExportPlan plan)
    {
        var total = plan.Clips.Sum(c => c.Duration.TotalSeconds);
        foreach (var join in plan.JoinsBetweenClips)
        {
            if (join.Mode == TransitionMode.Crossfade)
                total -= join.CrossfadeSeconds;
        }

        return TimeSpan.FromSeconds(Math.Max(0, total));
    }

    private static string Format(TimeSpan time) =>
        time.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static string FormatDouble(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);
}
