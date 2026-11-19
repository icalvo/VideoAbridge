using System.Collections.ObjectModel;

namespace VideoAbridge.Services;

/// <summary>
/// Per-video edit-state cache: versions, shared scene progress, fades/cuts, and UI chrome.
/// </summary>
internal sealed class MarkerCacheEntry
{
    public string FileHash { get; set; } = "";
    public double SceneDetectPositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public Guid ActiveVersionId { get; set; }
    public Collection<VersionCacheDto> Versions { get; init; } = [];

    /// <summary>Legacy single zoom. Prefer <see cref="EditView"/> / <see cref="PreviewView"/>.</summary>
    public double? PixelsPerSecond { get; set; }

    public bool? IsPreviewMode { get; set; }

    public bool? IsTasksPaneOpen { get; set; }

    public WindowPlacementDto? Window { get; set; }

    public TimelineViewStateDto? EditView { get; set; }

    public TimelineViewStateDto? PreviewView { get; set; }
}
