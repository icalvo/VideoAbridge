namespace VideoAbridge.Core;

public sealed record ProjectSettings(
    bool FadeToBlackAtEnd,
    double FadeToBlackSeconds)
{
    public static ProjectSettings Default { get; } = new(false, 0);
}
