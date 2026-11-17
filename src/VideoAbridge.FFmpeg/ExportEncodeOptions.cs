namespace VideoAbridge.FFmpeg;

public sealed record ExportEncodeOptions(
    string Preset,
    int Crf,
    string AudioBitrate,
    bool ForceTranscode)
{
    public static ExportEncodeOptions Default { get; } = new("veryfast", 18, "192k", ForceTranscode: false);

    /// <summary>Faster, lower-quality encode used for interactive Preview mode.</summary>
    public static ExportEncodeOptions Preview { get; } = new("ultrafast", 28, "128k", ForceTranscode: true);
}
