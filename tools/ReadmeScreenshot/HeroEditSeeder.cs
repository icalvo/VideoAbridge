namespace ReadmeScreenshot;

/// <summary>Builds and persists the README hero timeline (markers + one deleted section).</summary>
internal static class HeroEditSeeder
{
    public static async Task SeedAsync(string videoPath, string cacheDirectory, CancellationToken cancellationToken)
    {
        var probe = new MediaProbe();
        var info = await probe.ProbeAsync(videoPath, cancellationToken).ConfigureAwait(false);
        var duration = info.Duration;
        if (duration < TimeSpan.FromSeconds(8))
        {
            throw new InvalidOperationException(
                $"Sample video must be at least 8 seconds (got {duration.TotalSeconds:0.###}s).");
        }

        var timeline = Timeline.Create(duration);
        timeline = RequireOk(timeline.AddMarker(TimeSpan.FromSeconds(2), MarkerSource.User));
        timeline = RequireOk(timeline.AddMarker(TimeSpan.FromSeconds(4), MarkerSource.User));
        timeline = RequireOk(timeline.AddMarker(TimeSpan.FromSeconds(6), MarkerSource.User));
        timeline = RequireOk(timeline.AddMarker(TimeSpan.FromSeconds(8), MarkerSource.User));
        timeline = RequireOk(timeline.DeleteBetween(timeline.Markers[1].Id, timeline.Markers[2].Id));

        var set = AbridgedVersionSet.CreateDefault(duration).ReplaceActiveTimeline(timeline);
        var cache = new MarkerCacheService(cacheDirectory);
        cache.SaveVersionSet(videoPath, set);
    }

    private static T RequireOk<T>(Result<T> result) =>
        result.Match(
            value => value,
            error => throw new InvalidOperationException($"{error.Code}: {error.Message}"));
}
