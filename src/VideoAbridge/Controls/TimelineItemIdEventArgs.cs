namespace VideoAbridge.Controls;

public sealed class TimelineItemIdEventArgs(TimelineItemId id) : EventArgs
{
    public TimelineItemId Id { get; } = id;
}
