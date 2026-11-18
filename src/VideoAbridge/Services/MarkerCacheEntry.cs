using System.Collections.ObjectModel;

namespace VideoAbridge.Services;

/// <summary>
/// Per-video edit-state cache: versions, shared scene progress, and fades/cuts per version.
/// </summary>
internal sealed class MarkerCacheEntry
{
    public string FileHash { get; set; } = "";
    public double SceneDetectPositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public Guid ActiveVersionId { get; set; }
    public Collection<VersionCacheDto> Versions { get; init; } = [];
}
