using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using static VideoAbridge.Core.Results;

namespace VideoAbridge.Core;

public sealed class ProjectDocument
{
    public string VideoPath { get; init; } = "";
    public double DurationSeconds { get; init; }
    public Collection<MarkerDto> Markers { get; init; } = [];
    public Collection<bool> SegmentDeleted { get; init; } = [];
    public Collection<JoinDto> Joins { get; init; } = [];
    public bool FadeFromBlackAtStart { get; init; }
    public double FadeFromBlackSeconds { get; init; }
    public bool FadeToBlackAtEnd { get; init; }
    public double FadeToBlackSeconds { get; init; }

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
            FadeFromBlackAtStart = timeline.Settings.FadeFromBlackAtStart,
            FadeFromBlackSeconds = timeline.Settings.FadeFromBlackSeconds,
            FadeToBlackAtEnd = timeline.Settings.FadeToBlackAtEnd,
            FadeToBlackSeconds = timeline.Settings.FadeToBlackSeconds
        };
    }

    public Result<(string VideoPath, Timeline Timeline)> ToTimeline()
    {
        if (DurationSeconds <= 0)
            return Fail<(string, Timeline)>("InvalidProject", "Duration must be positive.");

        if (SegmentDeleted.Count != Markers.Count + 1)
            return Fail<(string, Timeline)>("InvalidProject", "Segment count must equal marker count + 1.");

        var markers = new List<Marker>();
        foreach (var marker in Markers.OrderBy(m => m.TimeSeconds))
        {
            if (marker.TimeSeconds <= 0 || marker.TimeSeconds >= DurationSeconds)
                return Fail<(string, Timeline)>("InvalidProject", "Marker time out of range.");

            if (!Enum.TryParse<MarkerSource>(marker.Source, out var source))
                source = MarkerSource.User;

            markers.Add(new Marker(marker.Id, TimeSpan.FromSeconds(marker.TimeSeconds), source));
        }

        var joins = new List<JoinTransition>();
        foreach (var dto in Joins)
        {
            if (!Enum.TryParse<TransitionMode>(dto.Mode, out var mode))
                mode = TransitionMode.HardCut;

            joins.Add(new JoinTransition(
                dto.DeletedRunId == Guid.Empty ? Guid.NewGuid() : dto.DeletedRunId,
                mode,
                mode == TransitionMode.Crossfade ? dto.CrossfadeSeconds : 0));
        }

        var settings = new ProjectSettings(
            FadeFromBlackAtStart,
            FadeFromBlackAtStart ? FadeFromBlackSeconds : 0,
            FadeToBlackAtEnd,
            FadeToBlackAtEnd ? FadeToBlackSeconds : 0);
        var timeline = Timeline.FromPersistedState(
            TimeSpan.FromSeconds(DurationSeconds),
            markers,
            SegmentDeleted,
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

public sealed class MarkerDto
{
    public Guid Id { get; init; }
    public double TimeSeconds { get; init; }
    public string Source { get; init; } = nameof(MarkerSource.User);
}

public sealed class JoinDto
{
    public Guid DeletedRunId { get; init; }
    public string Mode { get; init; } = nameof(TransitionMode.HardCut);
    public double CrossfadeSeconds { get; init; }
}
