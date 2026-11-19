namespace VideoAbridge.Services;

/// <summary>UI chrome persisted alongside timeline versions for a video.</summary>
internal sealed class UiCacheState
{
    public bool IsPreviewMode { get; init; }
    public bool IsTasksPaneOpen { get; init; }
    public WindowPlacementDto? Window { get; init; }
    public TimelineViewStateDto? EditView { get; init; }
    public TimelineViewStateDto? PreviewView { get; init; }

    /// <summary>Legacy single zoom; used only when <see cref="EditView"/> is absent.</summary>
    public double? PixelsPerSecond { get; init; }
}
