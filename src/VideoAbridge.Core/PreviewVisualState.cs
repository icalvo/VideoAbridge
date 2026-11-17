namespace VideoAbridge.Core;

/// <summary>
/// Realtime preview frame state produced by <see cref="PreviewComposer"/>.
/// </summary>
public readonly record struct PreviewVisualState(
    TimeSpan PlayheadSourceTime,
    TimeSpan AbridgedTime,
    double BlackoutOpacity,
    bool InCrossfade,
    TimeSpan IncomingSourceTime,
    double IncomingOpacity,
    TimeSpan? JumpPrimaryTo,
    bool PreviewComplete);
