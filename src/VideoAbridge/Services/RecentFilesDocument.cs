using System.Collections.ObjectModel;

namespace VideoAbridge.Services;

/// <summary>JSON shape for <see cref="RecentFilesService"/> persistence.</summary>
internal sealed class RecentFilesDocument
{
    public Collection<string> Paths { get; init; } = [];
}
