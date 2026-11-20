using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoAbridge.Services;

/// <summary>Persists a short MRU list of successfully opened video paths.</summary>
internal sealed class RecentFilesService
{
    internal const int DefaultMaxCount = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _storagePath;
    private readonly int _maxCount;

    public RecentFilesService(string? storagePath = null, int maxCount = DefaultMaxCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        _maxCount = maxCount;
        _storagePath = storagePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoAbridge",
                "recent-files.json");
        var directory = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public string StoragePath => _storagePath;

    public List<string> Load()
    {
        if (!File.Exists(_storagePath))
            return [];

        try
        {
            var json = File.ReadAllText(_storagePath);
            var document = JsonSerializer.Deserialize<RecentFilesDocument>(json, JsonOptions);
            if (document?.Paths is null || document.Paths.Count == 0)
                return [];

            return Normalize(document.Paths);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public List<string> Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var paths = Load();
        paths.RemoveAll(p => PathsEqual(p, fullPath));
        paths.Insert(0, fullPath);
        return Save(paths);
    }

    public List<string> Remove(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var paths = Load();
        paths.RemoveAll(p => PathsEqual(p, fullPath));
        return Save(paths);
    }

    private List<string> Save(IReadOnlyList<string> paths)
    {
        var normalized = Normalize(paths);
        var document = new RecentFilesDocument
        {
            Paths = [.. normalized]
        };
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var temp = _storagePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Copy(temp, _storagePath, overwrite: true);
        File.Delete(temp);
        return normalized;
    }

    private List<string> Normalize(IEnumerable<string> paths)
    {
        var result = new List<string>(_maxCount);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (PathTooLongException)
            {
                continue;
            }

            if (result.Exists(existing => PathsEqual(existing, fullPath)))
                continue;

            result.Add(fullPath);
            if (result.Count >= _maxCount)
                break;
        }

        return result;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
