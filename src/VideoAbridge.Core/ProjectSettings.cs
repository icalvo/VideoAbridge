namespace VideoAbridge.Core;

public sealed record ProjectSettings(
    bool FadeFromBlackAtStart,
    double FadeFromBlackSeconds,
    bool FadeToBlackAtEnd,
    double FadeToBlackSeconds)
{
    public static ProjectSettings Default { get; } = new(false, 0, false, 0);
}
