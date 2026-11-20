using System.IO;
using System.Text.Json;
using FluentAssertions;
using VideoAbridge.Services;

namespace VideoAbridge.Tests;

public class MarkerCacheServiceTests
{
    [Fact]
    public void SaveAndTryLoadRoundTripMultiVersion()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(200));
        var duration = TimeSpan.FromSeconds(42);
        var cache = new MarkerCacheService(Path.Combine(dir.Path, "MarkerCache"));

        var set = AbridgedVersionSet.CreateDefault(duration).RequireOk();
        set = set.ReplaceActiveTimeline(
            set.ActiveTimeline.AddMarker(TimeSpan.FromSeconds(10), MarkerSource.User).RequireOk()).RequireOk();
        set = set.ReplaceActiveTimeline(set.ActiveTimeline.SetStartFade(1.25).RequireOk()).RequireOk();
        set = set.WithSceneDetectPosition(TimeSpan.FromSeconds(15));
        set = set.CreateVersionFromScenes("Alt").RequireOk();
        cache.SaveVersionSet(
            video,
            set,
            new UiCacheState
            {
                IsPreviewMode = true,
                IsTasksPaneOpen = true,
                PixelsPerSecond = 64,
                EditView = new TimelineViewStateDto
                {
                    PixelsPerSecond = 64,
                    VisibleStartSeconds = 12.5,
                    PlayheadSeconds = 20
                },
                PreviewView = new TimelineViewStateDto
                {
                    PixelsPerSecond = 80,
                    VisibleStartSeconds = 3,
                    PlayheadSeconds = 5
                },
                Window = new WindowPlacementDto
                {
                    Left = 40,
                    Top = 50,
                    Width = 1280,
                    Height = 800,
                    State = "Maximized"
                }
            });

        var loaded = cache.TryLoadVersionSet(video, duration);
        loaded.Should().NotBeNull();
        loaded.Versions.Should().HaveCount(2);
        loaded.Active.Name.Should().Be("Alt");
        loaded.SceneDetectPosition.Should().Be(TimeSpan.FromSeconds(15));
        loaded.Versions.First(v => v.Name == AbridgedVersionSet.DefaultVersionName)
            .Timeline.Settings.FadeFromBlackSeconds.Should().Be(1.25);

        var entry = cache.TryLoad(video, duration);
        entry.Should().NotBeNull();
        entry.IsPreviewMode.Should().BeTrue();
        entry.IsTasksPaneOpen.Should().BeTrue();
        entry.EditView.Should().NotBeNull();
        entry.EditView.PixelsPerSecond.Should().Be(64);
        entry.EditView.VisibleStartSeconds.Should().Be(12.5);
        entry.EditView.PlayheadSeconds.Should().Be(20);
        entry.PreviewView.Should().NotBeNull();
        entry.PreviewView.PixelsPerSecond.Should().Be(80);
        entry.Window.Should().NotBeNull();
        entry.Window.Left.Should().Be(40);
        entry.Window.State.Should().Be("Maximized");

        var files = Directory.GetFiles(cache.CacheDirectory, "*.json");
        files.Should().ContainSingle();
        var text = File.ReadAllText(files[0]);
        text.Should().Contain("versions");
        text.Should().Contain("activeVersionId");
        text.Should().Contain("\"name\": \"Alt\"");
        text.Should().Contain("editView");
        text.Should().Contain("previewView");
        text.Should().Contain("isTasksPaneOpen");
        text.Should().NotContain("fadeFromBlackAtStart");
        text.Should().NotContain("videoPath");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.TryGetProperty("markers", out _).Should().BeFalse();
    }

    [Fact]
    public void TryLoadReturnsNullWhenDurationDoesNotMatch()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(200));
        var cache = new MarkerCacheService(Path.Combine(dir.Path, "MarkerCache"));
        var set = AbridgedVersionSet.CreateDefault(TimeSpan.FromSeconds(42)).RequireOk();
        cache.SaveVersionSet(video, set);

        cache.TryLoadVersionSet(video, TimeSpan.FromSeconds(43)).Should().BeNull();
    }

    [Fact]
    public void SaveAndTryLoadPreservesVersionOrder()
    {
        using var dir = new TempDir();
        var video = dir.WriteFile("clip.bin", CreateBytes(200));
        var duration = TimeSpan.FromSeconds(42);
        var cache = new MarkerCacheService(Path.Combine(dir.Path, "MarkerCache"));

        var set = AbridgedVersionSet.CreateDefault(duration).RequireOk();
        set = set.CreateVersionFromScenes("Two").RequireOk();
        set = set.CreateVersionFromScenes("Three").RequireOk();
        var lastId = set.ActiveVersionId;
        set = set.Reorder(lastId, 0).RequireOk();
        set.Versions.Select(v => v.Name).Should().Equal("Three", "Default", "Two");

        cache.SaveVersionSet(video, set);

        var loaded = cache.TryLoadVersionSet(video, duration);
        loaded.Should().NotBeNull();
        loaded.Versions.Select(v => v.Name).Should().Equal("Three", "Default", "Two");
        loaded.ActiveVersionId.Should().Be(lastId);
    }

    private static byte[] CreateBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i & 0xFF);
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
            }
        }
    }
}
