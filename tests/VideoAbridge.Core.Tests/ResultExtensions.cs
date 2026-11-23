using VideoAbridge.Core.Outcome;

namespace VideoAbridge.Core.Tests;

internal static class ResultExtensions
{
    public static T RequireOk<T>(this Result<T> result)
        where T : notnull =>
        result.Match(
            value => value,
            error => throw new InvalidOperationException($"{error.Code}: {error.Message}"));

    public static DomainError RequireError<T>(this Result<T> result)
        where T : notnull =>
        result.Match(
            _ => throw new InvalidOperationException("Expected an error result."),
            error => error);
}
