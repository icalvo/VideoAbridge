namespace VideoAbridge.FFmpeg;

public sealed class BinaryProcessResult
{
    public required int ExitCode { get; init; }
    public required ReadOnlyMemory<byte> StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public bool Success => ExitCode == 0;
}
