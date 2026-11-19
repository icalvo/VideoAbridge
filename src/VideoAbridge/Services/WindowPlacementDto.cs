namespace VideoAbridge.Services;

/// <summary>Cached main-window bounds for a video (restore when reopening that file).</summary>
internal sealed class WindowPlacementDto
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>WPF <c>WindowState</c> name: Normal, Maximized, or Minimized.</summary>
    public string State { get; set; } = "Normal";
}
