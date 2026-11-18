using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class FileContentIdTests
{
    [Fact]
    public void ComputeSameFileAndDurationReturnsSameId()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.bin", CreateBytes(length: 100));
        var duration = TimeSpan.FromSeconds(12.5);

        var first = FileContentId.Compute(path, duration);
        var second = FileContentId.Compute(path, duration);

        first.Should().Be(second);
        first.Should().HaveLength(32);
        first.Should().MatchRegex("^[0-9A-F]{32}$");
    }

    [Fact]
    public void ComputeDurationChangeReturnsDifferentId()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.bin", CreateBytes(length: 100));

        var a = FileContentId.Compute(path, TimeSpan.FromSeconds(10));
        var b = FileContentId.Compute(path, TimeSpan.FromSeconds(11));

        a.Should().NotBe(b);
    }

    [Fact]
    public void ComputeContentChangeReturnsDifferentId()
    {
        using var dir = new TempDir();
        var pathA = dir.WriteFile("a.bin", CreateBytes(length: 100, seed: 1));
        var pathB = dir.WriteFile("b.bin", CreateBytes(length: 100, seed: 2));
        var duration = TimeSpan.FromSeconds(5);

        FileContentId.Compute(pathA, duration).Should().NotBe(FileContentId.Compute(pathB, duration));
    }

    [Fact]
    public void ComputeLengthChangeReturnsDifferentId()
    {
        using var dir = new TempDir();
        var pathA = dir.WriteFile("a.bin", CreateBytes(length: 100));
        var pathB = dir.WriteFile("b.bin", CreateBytes(length: 101));
        var duration = TimeSpan.FromSeconds(5);

        FileContentId.Compute(pathA, duration).Should().NotBe(FileContentId.Compute(pathB, duration));
    }

    [Fact]
    public void ComputeFileSmallerThanSampleHashesEntireFile()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("small.bin", CreateBytes(length: 64));
        var id = FileContentId.Compute(path, TimeSpan.FromSeconds(1));
        id.Should().HaveLength(32);
    }

    [Fact]
    public void ComputeFileBetweenOneAndTwoSamplesIsStable()
    {
        using var dir = new TempDir();
        var length = FileContentId.SampleSizeBytes + (FileContentId.SampleSizeBytes / 2);
        var path = dir.WriteFile("mid.bin", CreateBytes(length));
        var duration = TimeSpan.FromMinutes(3);

        var first = FileContentId.Compute(path, duration);
        var second = FileContentId.Compute(path, duration);
        first.Should().Be(second);
    }

    [Fact]
    public void ComputeFileLargerThanTwoSamplesUsesHeadAndTail()
    {
        using var dir = new TempDir();
        var length = (FileContentId.SampleSizeBytes * 2) + 100;
        var bytes = CreateBytes(length, seed: 9);
        var path = dir.WriteFile("large.bin", bytes);
        var duration = TimeSpan.FromSeconds(90);

        var baseline = FileContentId.Compute(path, duration);

        // Mutate only the middle (outside head/tail samples) — identity must stay the same.
        bytes[FileContentId.SampleSizeBytes + 50] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        FileContentId.Compute(path, duration).Should().Be(baseline);

        // Mutate the tail sample — identity must change.
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        FileContentId.Compute(path, duration).Should().NotBe(baseline);
    }

    private static byte[] CreateBytes(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)((i + seed) & 0xFF);
        return bytes;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "VideoAbridgeTests_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string WriteFile(string name, byte[] content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(full, content);
            return full;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
