using System.Buffers.Binary;
using System.IO.Hashing;

namespace VideoAbridge.Core;

/// <summary>
/// Fast non-cryptographic file identity for cache keys: length, 1 MiB head/tail samples, and duration.
/// </summary>
public static class FileContentId
{
    public const int SampleSizeBytes = 1 << 20; // 1 MiB

    /// <summary>
    /// Computes an uppercase hex XxHash128 fingerprint of file length, head/tail samples, and duration.
    /// Duration is folded in as rounded milliseconds from <see cref="TimeSpan.TotalSeconds"/> so migration
    /// from cached <c>durationSeconds</c> matches later loads that pass a probed <see cref="TimeSpan"/>.
    /// </summary>
    public static string Compute(string filePath, TimeSpan duration) =>
        Compute(filePath, duration.TotalSeconds);

    /// <summary>
    /// Same as <see cref="Compute(string, TimeSpan)"/> using a duration in seconds (e.g. cache JSON).
    /// </summary>
    public static string Compute(string filePath, double durationSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Video file was not found.", fullPath);

        var length = info.Length;
        var hash = new XxHash128();

        Span<byte> meta = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(meta, length);
        hash.Append(meta);

        using (var stream = new FileStream(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   SampleSizeBytes,
                   FileOptions.SequentialScan))
        {
            AppendSample(hash, stream, offset: 0, count: (int)Math.Min(length, SampleSizeBytes));

            if (length > SampleSizeBytes)
            {
                var tailOffset = Math.Max(0L, length - SampleSizeBytes);
                AppendSample(hash, stream, offset: tailOffset, count: (int)Math.Min(length, SampleSizeBytes));
            }
        }

        BinaryPrimitives.WriteInt64LittleEndian(meta, DurationKeyMilliseconds(durationSeconds));
        hash.Append(meta);

        return Convert.ToHexString(hash.GetHashAndReset()).ToUpperInvariant();
    }

    public static long DurationKeyMilliseconds(double durationSeconds) =>
        (long)Math.Round(durationSeconds * 1000.0, MidpointRounding.AwayFromZero);

    private static void AppendSample(XxHash128 hash, FileStream stream, long offset, int count)
    {
        if (count <= 0)
            return;

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }

            if (totalRead > 0)
                hash.Append(buffer.AsSpan(0, totalRead));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
