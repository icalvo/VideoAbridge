namespace VideoAbridge.Core.Editing;

public sealed record Marker(Guid Id, TimeSpan Time, MarkerSource Source);
