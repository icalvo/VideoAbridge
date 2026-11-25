
namespace VideoAbridge.Services;

internal sealed class MarkerDto
{
    public Guid Id { get; init; }
    public double TimeSeconds { get; init; }
    public string Source { get; init; } = nameof(MarkerSource.User);
    /// <summary>Null or omitted means no label.</summary>
    public string? Label { get; init; }
}
