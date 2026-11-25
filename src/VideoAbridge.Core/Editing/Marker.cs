namespace VideoAbridge.Core.Editing;

public sealed record Marker(MarkerId Id, TimeSpan Time, MarkerSource Source, string Label = "");
