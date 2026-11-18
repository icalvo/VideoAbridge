
namespace VideoAbridge.Services;

internal sealed class MarkerDto
{
    public Guid Id { get; init; }
    public double TimeSeconds { get; init; }
    public string Source { get; init; } = nameof(MarkerSource.User);
}
