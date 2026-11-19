namespace VideoAbridge.Services;

/// <summary>Per-mode timeline chrome: zoom, scroll, and playhead ("now").</summary>
internal sealed class TimelineViewStateDto
{
    public double PixelsPerSecond { get; set; } = 40;
    public double VisibleStartSeconds { get; set; }
    public double PlayheadSeconds { get; set; }
}
