namespace VideoAbridge.Core.Editing;

public sealed record ProjectSettings(
    double? FadeFromBlackSeconds,
    double? FadeToBlackSeconds)
{
    public static ProjectSettings Default { get; } = new(null, null);
}
