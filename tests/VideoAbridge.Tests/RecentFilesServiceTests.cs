using System.IO;
using System.Text.Json;
using FluentAssertions;
using VideoAbridge.Services;

namespace VideoAbridge.Tests;

public class RecentFilesServiceTests
{
    [Fact]
    public void AddPromotesExistingPathAndCapsCount()
    {
        using var dir = new TempDir();
        var storage = Path.Combine(dir.Path, "recent-files.json");
        var service = new RecentFilesService(storage, maxCount: 3);

        service.Add(Path.Combine(dir.Path, "a.mp4")).Should().Equal(
            Path.GetFullPath(Path.Combine(dir.Path, "a.mp4")));
        service.Add(Path.Combine(dir.Path, "b.mp4"));
        service.Add(Path.Combine(dir.Path, "c.mp4"));
        service.Add(Path.Combine(dir.Path, "d.mp4"));

        var afterCap = service.Load();
        afterCap.Should().HaveCount(3);
        afterCap[0].Should().Be(Path.GetFullPath(Path.Combine(dir.Path, "d.mp4")));
        afterCap[1].Should().Be(Path.GetFullPath(Path.Combine(dir.Path, "c.mp4")));
        afterCap[2].Should().Be(Path.GetFullPath(Path.Combine(dir.Path, "b.mp4")));

        service.Add(Path.Combine(dir.Path, "b.mp4"));
        var afterPromote = service.Load();
        afterPromote[0].Should().Be(Path.GetFullPath(Path.Combine(dir.Path, "b.mp4")));
        afterPromote.Should().HaveCount(3);

        var text = File.ReadAllText(storage);
        text.Should().Contain("paths");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("paths").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void RemoveDropsPathCaseInsensitively()
    {
        using var dir = new TempDir();
        var storage = Path.Combine(dir.Path, "recent-files.json");
        var service = new RecentFilesService(storage);
        var path = Path.Combine(dir.Path, "clip.mp4");
        service.Add(path);

        service.Remove(path.ToUpperInvariant()).Should().BeEmpty();
        service.Load().Should().BeEmpty();
    }

    [Fact]
    public void LoadReturnsEmptyWhenFileMissingOrCorrupt()
    {
        using var dir = new TempDir();
        var storage = Path.Combine(dir.Path, "recent-files.json");
        var service = new RecentFilesService(storage);
        service.Load().Should().BeEmpty();

        File.WriteAllText(storage, "{ not json");
        service.Load().Should().BeEmpty();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "VideoAbridgeTests_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
