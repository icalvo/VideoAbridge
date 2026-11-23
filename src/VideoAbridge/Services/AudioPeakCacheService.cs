using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using VideoAbridge.FFmpeg;

namespace VideoAbridge.Services;

/// <summary>
/// Persists waveform peak envelopes keyed by <see cref="FileContentId"/> fingerprint.
/// </summary>
internal sealed class AudioPeakCacheService
{
    private const int FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "VAPK"u8;

    private readonly string _cacheDirectory;

    public AudioPeakCacheService(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoAbridge",
                "WaveformCache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    public AudioPeakEnvelope? TryLoad(
        string videoPath,
        TimeSpan duration,
        TimeSpan bucketDuration,
        int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        if (!TryGetFileHash(videoPath, duration, out var fileHash))
            return null;

        var file = GetCacheFilePath(fileHash);
        if (!File.Exists(file))
            return null;

        try
        {
            using var stream = File.OpenRead(file);
            return ReadEnvelope(stream, bucketDuration, sampleRate);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public void Save(
        string videoPath,
        TimeSpan duration,
        AudioPeakEnvelope envelope,
        int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.IsEmpty)
            return;

        if (!TryGetFileHash(videoPath, duration, out var fileHash))
            return;

        var file = GetCacheFilePath(fileHash);
        var temp = file + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
                WriteEnvelope(stream, envelope, sampleRate);

            File.Copy(temp, file, overwrite: true);
            File.Delete(temp);
        }
        catch (IOException)
        {
            TryDelete(temp);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
        }
    }

    public string GetCacheFilePath(string fileHash) =>
        Path.Combine(_cacheDirectory, fileHash + ".peaks");

    private static bool TryGetFileHash(string videoPath, TimeSpan duration, out string fileHash)
    {
        fileHash = "";
        var fullPath = Path.GetFullPath(videoPath);
        try
        {
            var hash = FileContentId.Compute(fullPath, duration).Match<string?>(h => h, _ => null);
            if (hash is null)
                return false;
            fileHash = hash;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteEnvelope(Stream stream, AudioPeakEnvelope envelope, int sampleRate)
    {
        Span<byte> header = stackalloc byte[4 + 4 + 4 + 8 + 8 + 4];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], sampleRate);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], envelope.BucketDuration.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header[20..], envelope.Duration.Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], envelope.Count);
        stream.Write(header);

        stream.Write(MemoryMarshal.AsBytes(envelope.Minima));
        stream.Write(MemoryMarshal.AsBytes(envelope.Maxima));
    }

    private static AudioPeakEnvelope? ReadEnvelope(
        Stream stream,
        TimeSpan expectedBucketDuration,
        int expectedSampleRate)
    {
        Span<byte> header = stackalloc byte[4 + 4 + 4 + 8 + 8 + 4];
        if (stream.Read(header) != header.Length)
            return null;

        if (!header[..4].SequenceEqual(Magic))
            return null;

        var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (version != FormatVersion)
            return null;

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (sampleRate != expectedSampleRate)
            return null;

        var bucketTicks = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
        var durationTicks = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        if (count < 0 || count > 50_000_000)
            return null;

        var bucketDuration = TimeSpan.FromTicks(bucketTicks);
        if (Math.Abs((bucketDuration - expectedBucketDuration).TotalMilliseconds) > 0.5)
            return null;

        var minima = new float[count];
        var maxima = new float[count];
        var minimaBytes = MemoryMarshal.AsBytes(minima.AsSpan());
        var maximaBytes = MemoryMarshal.AsBytes(maxima.AsSpan());
        if (stream.Read(minimaBytes) != minimaBytes.Length)
            return null;
        if (stream.Read(maximaBytes) != maximaBytes.Length)
            return null;

        return new AudioPeakEnvelope(
            TimeSpan.FromTicks(durationTicks),
            bucketDuration,
            minima,
            maxima);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
