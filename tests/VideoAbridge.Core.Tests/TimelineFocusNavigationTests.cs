using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class TimelineFocusNavigationTests
{
    private static readonly Guid M1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid M2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid M3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid D1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid D2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid D3 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly (Guid Id, TimeSpan Time)[] Markers =
    [
        (M1, TimeSpan.FromSeconds(10)),
        (M2, TimeSpan.FromSeconds(20)),
        (M3, TimeSpan.FromSeconds(40)),
    ];

    private static readonly (Guid Id, TimeSpan Time)[] Runs =
    [
        (D1, TimeSpan.FromSeconds(5)),
        (D2, TimeSpan.FromSeconds(25)),
        (D3, TimeSpan.FromSeconds(50)),
    ];

    [Fact]
    public void MoveHorizontalClampsAtEnds()
    {
        var atFirst = new TimelineFocusTarget(TimelineFocusKind.Marker, M1);
        TimelineFocusNavigation.MoveHorizontal(atFirst, -1, Markers, Runs)
            .Should().Be(atFirst);

        var atLast = new TimelineFocusTarget(TimelineFocusKind.Marker, M3);
        TimelineFocusNavigation.MoveHorizontal(atLast, 1, Markers, Runs)
            .Should().Be(atLast);
    }

    [Fact]
    public void MoveHorizontalStepsWithinLane()
    {
        var current = new TimelineFocusTarget(TimelineFocusKind.Marker, M1);
        var next = TimelineFocusNavigation.MoveHorizontal(current, 1, Markers, Runs);
        next.Should().Be(new TimelineFocusTarget(TimelineFocusKind.Marker, M2));

        var run = new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D1);
        TimelineFocusNavigation.MoveHorizontal(run, 1, Markers, Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D2));
    }

    [Fact]
    public void MoveVerticalFromMarkerPicksNearestDeletedRun()
    {
        // M2@20 → nearer D2@25 than D1@5
        var current = new TimelineFocusTarget(TimelineFocusKind.Marker, M2);
        TimelineFocusNavigation.MoveVertical(current, Markers, Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D2));
    }

    [Fact]
    public void MoveVerticalFromDeletedRunPicksNearestMarker()
    {
        var current = new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D2);
        TimelineFocusNavigation.MoveVertical(current, Markers, Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.Marker, M2));
    }

    [Fact]
    public void MoveVerticalTiePrefersEarlier()
    {
        // Focus mid between M1@10 and M2@20 at deleted D@15 → equidistant markers
        (Guid Id, TimeSpan Time)[] markers =
        [
            (M1, TimeSpan.FromSeconds(10)),
            (M2, TimeSpan.FromSeconds(20)),
        ];
        (Guid Id, TimeSpan Time)[] runs =
        [
            (D1, TimeSpan.FromSeconds(15)),
        ];

        var current = new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D1);
        TimelineFocusNavigation.MoveVertical(current, markers, runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.Marker, M1));
    }

    [Fact]
    public void MoveVerticalEmptyOtherLaneKeepsCurrent()
    {
        var current = new TimelineFocusTarget(TimelineFocusKind.Marker, M1);
        TimelineFocusNavigation.MoveVertical(current, Markers, [])
            .Should().Be(current);
    }

    [Fact]
    public void NearestToTimePrefersMarkers()
    {
        TimelineFocusNavigation.NearestToTime(TimeSpan.FromSeconds(24), Markers, Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.Marker, M2));
    }

    [Fact]
    public void NearestToTimeFallsBackToDeletedRuns()
    {
        TimelineFocusNavigation.NearestToTime(TimeSpan.FromSeconds(24), [], Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D2));
    }

    [Fact]
    public void AfterRemovalPrefersSameLane()
    {
        var removed = new TimelineFocusTarget(TimelineFocusKind.Marker, M2);
        (Guid Id, TimeSpan Time)[] remaining =
        [
            (M1, TimeSpan.FromSeconds(10)),
            (M3, TimeSpan.FromSeconds(40)),
        ];

        TimelineFocusNavigation.AfterRemoval(removed, TimeSpan.FromSeconds(20), remaining, Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.Marker, M1));
    }

    [Fact]
    public void AfterRemovalFallsBackToOtherLane()
    {
        var removed = new TimelineFocusTarget(TimelineFocusKind.Marker, M1);
        TimelineFocusNavigation.AfterRemoval(removed, TimeSpan.FromSeconds(10), [], Runs)
            .Should().Be(new TimelineFocusTarget(TimelineFocusKind.DeletedRun, D1));
    }
}
