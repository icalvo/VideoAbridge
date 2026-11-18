
namespace VideoAbridge.Services;

internal sealed class JoinDto
{
    public Guid DeletedRunId { get; init; }
    public string Mode { get; init; } = nameof(TransitionMode.HardCut);
    public double CrossfadeSeconds { get; init; }
}
