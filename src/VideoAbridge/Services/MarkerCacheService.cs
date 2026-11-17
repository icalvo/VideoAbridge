using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoAbridge.Core;

namespace VideoAbridge.Services;

/// <summary>
/// Per-video edit-state cache: markers, cuts, joins/crossfades, fades, and scene-detect progress.
/// </summary>
internal sealed class MarkerCacheEntry
{
    public string VideoPath { get; set; } = "";
    public double SceneDetectPositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public List<MarkerDto> Markers { get; set; } = [];
    public List<bool> SegmentDeleted { get; set; } = [];
    public List<JoinDto> Joins { get; set; } = [];
    public bool FadeFromBlackAtStart { get; set; }
    public double FadeFromBlackSeconds { get; set; }
    public bool FadeToBlackAtEnd { get; set; }
    public double FadeToBlackSeconds { get; set; }
}

/// <summary>
/// Persists full timeline edit state keyed by the video file's full path.
/// </summary>
internal sealed class MarkerCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _cacheDirectory;

    public MarkerCacheService(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoAbridge",
                "MarkerCache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public MarkerCacheEntry? TryLoad(string videoPath)
    {
        var fullPath = Path.GetFullPath(videoPath);
        var file = GetCacheFilePath(fullPath);
        if (!File.Exists(file))
            return null;

        try
        {
            var json = File.ReadAllText(file);
            var entry = JsonSerializer.Deserialize<MarkerCacheEntry>(json, JsonOptions);
            if (entry is null)
                return null;

            // Require path match so a hash collision cannot restore the wrong video's state.
            if (!PathsEqual(entry.VideoPath, fullPath))
                return null;

            return entry;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(MarkerCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var fullPath = Path.GetFullPath(entry.VideoPath);
        entry.VideoPath = fullPath;
        var file = GetCacheFilePath(fullPath);
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Copy(temp, file, overwrite: true);
        File.Delete(temp);
    }

    public void SaveFromTimeline(string videoPath, Timeline timeline, TimeSpan sceneDetectPosition)
    {
        var doc = ProjectDocument.FromTimeline(videoPath, timeline);
        var entry = new MarkerCacheEntry
        {
            VideoPath = videoPath,
            SceneDetectPositionSeconds = Math.Max(0, sceneDetectPosition.TotalSeconds),
            DurationSeconds = doc.DurationSeconds,
            Markers = [.. doc.Markers],
            SegmentDeleted = [.. doc.SegmentDeleted],
            Joins = [.. doc.Joins],
            FadeFromBlackAtStart = doc.FadeFromBlackAtStart,
            FadeFromBlackSeconds = doc.FadeFromBlackSeconds,
            FadeToBlackAtEnd = doc.FadeToBlackAtEnd,
            FadeToBlackSeconds = doc.FadeToBlackSeconds
        };
        Save(entry);
    }

    private string GetCacheFilePath(string fullVideoPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullVideoPath))).ToUpperInvariant();
        return Path.Combine(_cacheDirectory, hash + ".json");
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
