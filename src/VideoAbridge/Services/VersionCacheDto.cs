using System.Collections.ObjectModel;

namespace VideoAbridge.Services;

/// <summary>
/// One named abridged version inside a multi-version cache document.
/// </summary>
internal sealed class VersionCacheDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = AbridgedVersionSet.DefaultVersionName;
    public Collection<MarkerDto> Markers { get; init; } = [];
    public Collection<bool> SegmentDeleted { get; init; } = [];
    public Collection<JoinDto> Joins { get; init; } = [];
    public double? FadeFromBlackSeconds { get; set; }
    public double? FadeToBlackSeconds { get; set; }
}
