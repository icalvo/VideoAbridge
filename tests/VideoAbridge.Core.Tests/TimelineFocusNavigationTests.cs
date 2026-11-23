using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class TimelineFocusNavigationTests
{
    private static readonly MarkerId M1 = MarkerId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MarkerId M2 = MarkerId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly MarkerId M3 = MarkerId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DeletedRunId D1 = DeletedRunId.From(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly DeletedRunId D2 = DeletedRunId.From(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly DeletedRunId D3 = DeletedRunId.From(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));

    private static readonly (TimelineItemId Id, TimeSpan Time)[] Markers =
    [
        (M1, TimeSpan.FromSeconds(10)),
        (M2, TimeSpan.FromSeconds(20)),
        (M3, TimeSpan.FromSeconds(40)),
    ];

    private static readonly (TimelineItemId Id, TimeSpan Time)[] Runs =
    [
        (D1, TimeSpan.FromSeconds(5)),
        (D2, TimeSpan.FromSeconds(25)),
        (D3, TimeSpan.FromSeconds(50)),
    ];

    [Fact]
    public void MoveHorizontalClampsAtEnds()
    {
        TimelineItemId atFirst = M1;
        TimelineFocusNavigation.MoveHorizontal(atFirst, -1, Markers, Runs)
            .Should().Be(atFirst);

        TimelineItemId atLast = M3;
        TimelineFocusNavigation.MoveHorizontal(atLast, 1, Markers, Runs)
            .Should().Be(atLast);
    }

    [Fact]
    public void MoveHorizontalStepsWithinLane()
    {
        TimelineItemId current = M1;
        var next = TimelineFocusNavigation.MoveHorizontal(current, 1, Markers, Runs);
        next.Should().Be((TimelineItemId)M2);

        TimelineItemId run = D1;
        TimelineFocusNavigation.MoveHorizontal(run, 1, Markers, Runs)
            .Should().Be((TimelineItemId)D2);
    }

    [Fact]
    public void MoveVerticalFromMarkerPicksNearestDeletedRun()
    {
        // M2@20 → nearer D2@25 than D1@5
        TimelineItemId current = M2;
        TimelineFocusNavigation.MoveVertical(current, Markers, Runs)
            .Should().Be((TimelineItemId)D2);
    }

    [Fact]
    public void MoveVerticalFromDeletedRunPicksNearestMarker()
    {
        TimelineItemId current = D2;
        TimelineFocusNavigation.MoveVertical(current, Markers, Runs)
            .Should().Be((TimelineItemId)M2);
    }

    [Fact]
    public void MoveVerticalTiePrefersEarlier()
    {
        // Focus mid between M1@10 and M2@20 at deleted D@15 → equidistant markers
        (TimelineItemId Id, TimeSpan Time)[] markers =
        [
            (M1, TimeSpan.FromSeconds(10)),
            (M2, TimeSpan.FromSeconds(20)),
        ];
        (TimelineItemId Id, TimeSpan Time)[] runs =
        [
            (D1, TimeSpan.FromSeconds(15)),
        ];

        TimelineItemId current = D1;
        TimelineFocusNavigation.MoveVertical(current, markers, runs)
            .Should().Be((TimelineItemId)M1);
    }

    [Fact]
    public void MoveVerticalEmptyOtherLaneKeepsCurrent()
    {
        TimelineItemId current = M1;
        TimelineFocusNavigation.MoveVertical(current, Markers, [])
            .Should().Be(current);
    }

    [Fact]
    public void NearestToTimePrefersMarkers()
    {
        TimelineFocusNavigation.NearestToTime(TimeSpan.FromSeconds(24), Markers, Runs)
            .Should().Be((TimelineItemId)M2);
    }

    [Fact]
    public void NearestToTimeFallsBackToDeletedRuns()
    {
        TimelineFocusNavigation.NearestToTime(TimeSpan.FromSeconds(24), [], Runs)
            .Should().Be((TimelineItemId)D2);
    }

    [Fact]
    public void AfterRemovalPrefersSameLane()
    {
        TimelineItemId removed = M2;
        (TimelineItemId Id, TimeSpan Time)[] remaining =
        [
            (M1, TimeSpan.FromSeconds(10)),
            (M3, TimeSpan.FromSeconds(40)),
        ];

        TimelineFocusNavigation.AfterRemoval(removed, TimeSpan.FromSeconds(20), remaining, Runs)
            .Should().Be((TimelineItemId)M1);
    }

    [Fact]
    public void AfterRemovalFallsBackToOtherLane()
    {
        TimelineItemId removed = M1;
        TimelineFocusNavigation.AfterRemoval(removed, TimeSpan.FromSeconds(10), [], Runs)
            .Should().Be((TimelineItemId)D1);
    }

    [Fact]
    public void TimelineItemIdEqualityUsesCaseTypes()
    {
        TimelineItemId marker = M1;
        marker.Should().Be((TimelineItemId)M1);
        marker.Should().NotBe((TimelineItemId)M2);
        marker.Should().NotBe((TimelineItemId)DeletedRunId.From(M1.Value));
    }

    [Fact]
    public void NearestToTimeReturnsNullWhenEmpty()
    {
        TimelineFocusNavigation.NearestToTime(TimeSpan.FromSeconds(1), [], [])
            .Should().BeNull();
    }
}
