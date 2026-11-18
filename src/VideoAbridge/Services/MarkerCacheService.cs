using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoAbridge.Services;

/// <summary>
/// Persists multi-version timeline edit state keyed by a fast file-content fingerprint.
/// </summary>
internal sealed class MarkerCacheService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
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

    public string CacheDirectory => _cacheDirectory;

    public MarkerCacheEntry? TryLoad(string videoPath, TimeSpan duration)
    {
        var fullPath = Path.GetFullPath(videoPath);
        string fileHash;
        try
        {
            fileHash = FileContentId.Compute(fullPath, duration);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var file = GetCacheFilePath(fileHash);
        if (!File.Exists(file))
            return null;

        try
        {
            var json = File.ReadAllText(file);
            var entry = JsonSerializer.Deserialize<MarkerCacheEntry>(json, JsonOptions);
            if (entry is null || entry.Versions.Count == 0)
                return null;

            entry.FileHash = fileHash;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.FileHash);
        if (entry.Versions.Count == 0)
            throw new ArgumentException("At least one version is required.", nameof(entry));

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var file = GetCacheFilePath(entry.FileHash);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Copy(temp, file, overwrite: true);
        File.Delete(temp);
    }

    public void SaveVersionSet(string videoPath, AbridgedVersionSet versionSet)
    {
        ArgumentNullException.ThrowIfNull(versionSet);

        var fullPath = Path.GetFullPath(videoPath);
        var fileHash = FileContentId.Compute(fullPath, versionSet.Duration);
        Save(ToEntry(fileHash, versionSet));
    }

    public AbridgedVersionSet? TryLoadVersionSet(string videoPath, TimeSpan duration)
    {
        var entry = TryLoad(videoPath, duration);
        if (entry is null)
            return null;

        return TryToVersionSet(entry, duration);
    }

    public string GetCacheFilePath(string fileHash) =>
        Path.Combine(_cacheDirectory, fileHash + ".json");

    internal static MarkerCacheEntry ToEntry(string fileHash, AbridgedVersionSet versionSet)
    {
        return new MarkerCacheEntry
        {
            FileHash = fileHash,
            DurationSeconds = versionSet.Duration.TotalSeconds,
            SceneDetectPositionSeconds = Math.Max(0, versionSet.SceneDetectPosition.TotalSeconds),
            ActiveVersionId = versionSet.ActiveVersionId,
            Versions =
            [
                .. versionSet.Versions.Select(v =>
                {
                    var doc = ProjectDocument.FromTimeline("", v.Timeline);
                    return new VersionCacheDto
                    {
                        Id = v.Id,
                        Name = v.Name,
                        Markers = [.. doc.Markers],
                        SegmentDeleted = [.. doc.SegmentDeleted],
                        Joins = [.. doc.Joins],
                        FadeFromBlackSeconds = doc.FadeFromBlackSeconds,
                        FadeToBlackSeconds = doc.FadeToBlackSeconds
                    };
                })
            ]
        };
    }

    internal static AbridgedVersionSet? TryToVersionSet(MarkerCacheEntry entry, TimeSpan probedDuration)
    {
        if (entry.Versions.Count == 0)
            return null;

        ImmutableList<(VersionCacheDto Dto, Timeline? Timeline)> restored =
        [
            .. entry.Versions.Select(dto => (Dto: dto, Timeline: RestoreVersionTimeline(dto, probedDuration)))
        ];

        if (restored.Count == 0 || restored.Any(v => v.Timeline is null))
            return null;

        ImmutableList<AbridgedVersion> versions =
        [
            .. restored.Select(item =>
            {
                var name = string.IsNullOrWhiteSpace(item.Dto.Name)
                    ? AbridgedVersionSet.DefaultVersionName
                    : item.Dto.Name;
                return new AbridgedVersion(
                    item.Dto.Id == Guid.Empty ? Guid.NewGuid() : item.Dto.Id,
                    name,
                    item.Timeline!);
            })
        ];

        var activeId = entry.ActiveVersionId;
        if (versions.All(v => v.Id != activeId))
            activeId = versions[0].Id;

        var scenePos = TimeSpan.FromSeconds(Math.Max(0, entry.SceneDetectPositionSeconds));
        var lastScene = versions
            .SelectMany(v => v.Timeline.Markers)
            .Where(m => m.Source == MarkerSource.SceneDetect)
            .Select(m => m.Time)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        if (lastScene > scenePos)
            scenePos = lastScene;

        return AbridgedVersionSet.FromPersisted(probedDuration, scenePos, activeId, versions);
    }

    private static Timeline? RestoreVersionTimeline(VersionCacheDto dto, TimeSpan probedDuration)
    {
        var markers = dto.Markers
            .OrderBy(m => m.TimeSeconds)
            .Where(item => item.TimeSeconds > 0 && item.TimeSeconds < probedDuration.TotalSeconds)
            .Aggregate(
                ImmutableList<MarkerDto>.Empty,
                (acc, item) =>
                    acc.Count > 0
                    && Math.Abs(acc[^1].TimeSeconds - item.TimeSeconds) <= Timeline.MarkerEpsilon.TotalSeconds
                        ? acc
                        : acc.Add(item));

        ImmutableList<bool> segments = dto.SegmentDeleted is { Count: var n } && n == markers.Count + 1
            ? [.. dto.SegmentDeleted]
            : [.. Enumerable.Repeat(false, markers.Count + 1)];

        var interiorCount = CountInteriorDeletedRuns(segments);
        ImmutableList<JoinDto> joins =
        [
            .. dto.Joins.Take(interiorCount),
            .. Enumerable.Range(0, Math.Max(0, interiorCount - dto.Joins.Count)).Select(_ => new JoinDto
            {
                DeletedRunId = Guid.NewGuid(),
                Mode = nameof(TransitionMode.HardCut),
                CrossfadeSeconds = 0
            })
        ];

        var doc = new ProjectDocument
        {
            DurationSeconds = probedDuration.TotalSeconds,
            Markers = [.. markers],
            SegmentDeleted = [.. segments],
            Joins = [.. joins],
            FadeFromBlackSeconds = dto.FadeFromBlackSeconds,
            FadeToBlackSeconds = dto.FadeToBlackSeconds
        };

        return doc.ToTimeline().Match<Timeline?>(
            restored =>
            {
                var timeline = restored.Timeline;

                if (timeline.Settings.FadeFromBlackSeconds is { } startFade)
                {
                    timeline = timeline.SetStartFade(startFade)
                        .Match(next => next, _ => timeline);
                }

                if (timeline.Settings.FadeToBlackSeconds is { } endFade)
                {
                    timeline = timeline.SetEndFade(endFade)
                        .Match(next => next, _ => timeline);
                }

                return timeline.GetDeletedRuns()
                    .Where(r => r.IsInterior && r.Join is { Mode: TransitionMode.Crossfade })
                    .Aggregate(
                        timeline,
                        (current, run) =>
                            current.SetJoinCrossfade(run.Id, run.Join!.CrossfadeSeconds)
                                .Match(next => next, _ => current));
            },
            _ => null);
    }

    private static int CountInteriorDeletedRuns(ImmutableList<bool> segmentDeleted)
    {
        var count = 0;
        var i = 0;
        while (i < segmentDeleted.Count)
        {
            if (!segmentDeleted[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < segmentDeleted.Count && segmentDeleted[i])
                i++;

            var isLeading = start == 0;
            var isTrailing = i == segmentDeleted.Count;
            if (!isLeading && !isTrailing)
                count++;
        }

        return count;
    }
}
