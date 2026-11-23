namespace VideoAbridge.Core.Export;

public sealed record ExportJoin(
    TransitionMode Mode,
    TimeSpan Crossfade);
