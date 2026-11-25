namespace VideoAbridge.ViewModels;

/// <summary>Sticky Left/Right step type; selected with keys 5–9 (mode only).</summary>
internal enum PlayheadMoveMode
{
    Marker,
    Cut,
    Frame,
    Second,
    TenSeconds,
}
