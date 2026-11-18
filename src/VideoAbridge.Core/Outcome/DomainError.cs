namespace VideoAbridge.Core.Outcome;

public readonly record struct DomainError(string Code, string Message);
