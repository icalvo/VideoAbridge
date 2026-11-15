namespace VideoAbridge.Core;

public enum MarkerSource
{
    SceneDetect,
    User
}

public sealed record Marker(Guid Id, TimeSpan Time, MarkerSource Source);
