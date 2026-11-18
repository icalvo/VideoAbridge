using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoAbridge.Services;

internal sealed class ProjectDocument
{
    public string VideoPath { get; init; } = "";
    public double DurationSeconds { get; init; }
    public Collection<MarkerDto> Markers { get; init; } = [];
    public Collection<bool> SegmentDeleted { get; init; } = [];
    public Collection<JoinDto> Joins { get; init; } = [];
    /// <summary>Null disables fade-from-black.</summary>
    public double? FadeFromBlackSeconds { get; init; }

    /// <summary>Null disables fade-to-black.</summary>
    public double? FadeToBlackSeconds { get; init; }

    public static ProjectDocument FromTimeline(string videoPath, Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        return new()
        {
            VideoPath = videoPath,
            DurationSeconds = timeline.Duration.TotalSeconds,
            Markers =
            [
                .. timeline.Markers.Select(m => new MarkerDto
                {
                    Id = m.Id,
                    TimeSeconds = m.Time.TotalSeconds,
                    Source = m.Source.ToString()
                })
            ],
            SegmentDeleted = [.. timeline.Segments.Select(s => s.IsDeleted)],
            Joins =
            [
                .. timeline.Joins.Select(j => new JoinDto
                {
                    DeletedRunId = j.DeletedRunId,
                    Mode = j.Mode.ToString(),
                    CrossfadeSeconds = j.CrossfadeSeconds
                })
            ],
            FadeFromBlackSeconds = timeline.Settings.FadeFromBlackSeconds,
            FadeToBlackSeconds = timeline.Settings.FadeToBlackSeconds
        };
    }

    public Result<(string VideoPath, Timeline Timeline)> ToTimeline()
    {
        if (DurationSeconds <= 0)
            return Fail<(string, Timeline)>("InvalidProject", "Duration must be positive.");

        if (SegmentDeleted.Count != Markers.Count + 1)
            return Fail<(string, Timeline)>("InvalidProject", "Segment count must equal marker count + 1.");

        if (Markers.Any(m => m.TimeSeconds <= 0 || m.TimeSeconds >= DurationSeconds))
            return Fail<(string, Timeline)>("InvalidProject", "Marker time out of range.");

        ImmutableList<Marker> markers =
        [
            .. Markers
                .OrderBy(m => m.TimeSeconds)
                .Select(marker =>
                {
                    if (!Enum.TryParse<MarkerSource>(marker.Source, out var source))
                        source = MarkerSource.User;
                    return new Marker(marker.Id, TimeSpan.FromSeconds(marker.TimeSeconds), source);
                })
        ];

        ImmutableList<JoinTransition> joins =
        [
            .. Joins.Select(dto =>
            {
                if (!Enum.TryParse<TransitionMode>(dto.Mode, out var mode))
                    mode = TransitionMode.HardCut;
                return new JoinTransition(
                    dto.DeletedRunId == Guid.Empty ? Guid.NewGuid() : dto.DeletedRunId,
                    mode,
                    mode == TransitionMode.Crossfade ? dto.CrossfadeSeconds : 0);
            })
        ];

        var settings = new ProjectSettings(FadeFromBlackSeconds, FadeToBlackSeconds);
        var timeline = Timeline.FromPersistedState(
            TimeSpan.FromSeconds(DurationSeconds),
            markers,
            [.. SegmentDeleted],
            joins,
            settings);

        var interiorCount = timeline.GetDeletedRuns().Count(r => r.IsInterior);
        if (joins.Count != interiorCount)
        {
            return Fail<(string, Timeline)>(
                "InvalidProject",
                "Join count does not match interior deleted runs.");
        }

        return Ok((VideoPath, timeline));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ProjectDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    public static Result<ProjectDocument> Deserialize(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions);
            if (doc is null)
                return Fail<ProjectDocument>("InvalidProject", "Project file was empty.");
            return Ok(doc);
        }
        catch (JsonException ex)
        {
            return Fail<ProjectDocument>("InvalidProject", ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Fail<ProjectDocument>("InvalidProject", ex.Message);
        }
    }
}
