namespace VideoAbridge.Core;

public readonly record struct DomainError(string Code, string Message);

public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public DomainError? Error { get; }

    private Result(bool isSuccess, T? value, DomainError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(string code, string message) =>
        new(false, default, new DomainError(code, message));

    public static Result<T> Fail(DomainError error) => new(false, default, error);
}
