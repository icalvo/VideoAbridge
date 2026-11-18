namespace VideoAbridge.Controls;

public sealed class TimeSpanEventArgs(TimeSpan time) : EventArgs
{
    public TimeSpan Time { get; } = time;
}
