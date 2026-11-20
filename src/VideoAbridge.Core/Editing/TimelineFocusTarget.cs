namespace VideoAbridge.Core.Editing;

public readonly record struct TimelineFocusTarget(TimelineFocusKind Kind, Guid Id)
{
    public static TimelineFocusTarget None { get; } = new(TimelineFocusKind.None, Guid.Empty);

    public bool IsNone => Kind == TimelineFocusKind.None;
}
