using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using VideoAbridge.Core;

namespace VideoAbridge.FFmpeg;

public sealed class VideoExporter(string? ffmpegPath = null)
{
    private readonly string _ffmpegPath = ffmpegPath ?? FFmpegLocator.FindFFmpeg()
        ?? throw new InvalidOperationException("ffmpeg was not found.");

    public async Task ExportAsync(
        string inputPath,
        string outputPath,
        ExportPlan plan,
        IProgress<double>? progress = null,
        ExportEncodeOptions? encodeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Clips.Count == 0)
            throw new ArgumentException("Export plan has no clips.", nameof(plan));

        var options = encodeOptions ?? ExportEncodeOptions.Default;
        var args = BuildArguments(inputPath, outputPath, plan, options);
        var result = await ProcessRunner.RunAsync(
            _ffmpegPath,
            args,
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
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"Export failed: {result.StandardError}");

        progress?.Report(1);
    }

    public static Collection<string> BuildArguments(
        string inputPath,
        string outputPath,
        ExportPlan plan,
        ExportEncodeOptions? encodeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var options = encodeOptions ?? ExportEncodeOptions.Default;
        List<string> args =
        [
            "-hide_banner",
            "-y",
            "-i", inputPath
        ];

        // Stream-copy shortcut only for high-quality exports of a single trimmed clip.
        if (!options.ForceTranscode
            && plan.Clips.Count == 1
            && plan.JoinsBetweenClips.Count == 0
            && !plan.FadeFromBlackAtStart
            && !plan.FadeToBlackAtEnd)
        {
            var clip = plan.Clips[0];
            args.AddRange(
            [
                "-ss", Format(clip.Start),
                "-to", Format(clip.End),
                "-c", "copy",
                outputPath
            ]);
            return [.. args];
        }

        var filter = BuildFilterComplex(plan);
        args.AddRange(
        [
            "-filter_complex", filter,
            "-map", "[vout]",
            "-map", "[aout]",
            "-c:v", "libx264",
            "-preset", options.Preset,
            "-crf", options.Crf.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac",
            "-b:a", options.AudioBitrate,
            "-movflags", "+faststart",
            outputPath
        ]);
        return [.. args];
    }

    public static string BuildFilterComplex(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
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

        var fadeIn = plan.FadeFromBlackAtStart && plan.FadeFromBlackSeconds > 0;
        var fadeOut = plan.FadeToBlackAtEnd && plan.FadeToBlackSeconds > 0;
        if (fadeIn || fadeOut)
        {
            var vFilters = new List<string>();
            var aFilters = new List<string>();
            if (fadeIn)
            {
                var fade = plan.FadeFromBlackSeconds;
                vFilters.Add($"fade=t=in:st=0:d={FormatDouble(fade)}");
                aFilters.Add($"afade=t=in:st=0:d={FormatDouble(fade)}");
            }

            if (fadeOut)
            {
                var fade = plan.FadeToBlackSeconds;
                var fadeStart = Math.Max(0, accumulated - fade);
                vFilters.Add($"fade=t=out:st={FormatDouble(fadeStart)}:d={FormatDouble(fade)}");
                aFilters.Add($"afade=t=out:st={FormatDouble(fadeStart)}:d={FormatDouble(fade)}");
            }

            sb.Append(CultureInfo.InvariantCulture, $"[{vCurrent}]{string.Join(",", vFilters)}[vout];");
            sb.Append(CultureInfo.InvariantCulture, $"[{aCurrent}]{string.Join(",", aFilters)}[aout]");
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
