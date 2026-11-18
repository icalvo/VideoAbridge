namespace VideoAbridge.Controls;

public sealed class GuidEventArgs(Guid id) : EventArgs
{
    public Guid Id { get; } = id;
}
