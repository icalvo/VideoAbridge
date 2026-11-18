namespace VideoAbridge.Core.Versions;

public sealed class AbridgedVersion
{
    public AbridgedVersion(Guid id, string name, Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id;
        Name = name.Trim();
        Timeline = timeline;
    }

    public Guid Id { get; }
    public string Name { get; }
    public Timeline Timeline { get; }

    public AbridgedVersion WithName(string name) => new(Id, name, Timeline);

    public AbridgedVersion WithTimeline(Timeline timeline) => new(Id, Name, timeline);
}
