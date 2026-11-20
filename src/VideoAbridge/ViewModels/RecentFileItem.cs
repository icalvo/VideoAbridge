namespace VideoAbridge.ViewModels;

/// <summary>One entry in the File → Recent Files submenu.</summary>
internal sealed class RecentFileItem(string fullPath, string displayName)
{
    public string FullPath { get; } = fullPath;

    public string DisplayName { get; } = displayName;
}
