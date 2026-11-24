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

    // Centers: D1@7.5, D2@27.5, D3@55
    private static readonly (TimelineItemId Id, TimeSpan Start, TimeSpan End)[] Runs =
    [
        (D1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)),
        (D2, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30)),
        (D3, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60)),
    ];

    [Fact]
    public void ResolveAtPlayheadPrefersExactMarkerOverContainingCut()
    {
        // M1@10 is also the end of D1 [5,10]
        TimelineFocusNavigation.ResolveAtPlayhead(TimeSpan.FromSeconds(10), Markers, Runs)
            .Should().Be((TimelineItemId)M1);
    }

    [Fact]
    public void ResolveAtPlayheadUsesContainingCutWhenNoExactMarker()
    {
        TimelineFocusNavigation.ResolveAtPlayhead(TimeSpan.FromSeconds(27), Markers, Runs)
            .Should().Be((TimelineItemId)D2);
    }

    [Fact]
    public void ResolveAtPlayheadReturnsNullInKeptSection()
    {
        TimelineFocusNavigation.ResolveAtPlayhead(TimeSpan.FromSeconds(15), Markers, Runs)
            .Should().BeNull();
    }

    [Fact]
    public void ResolveAtPlayheadRequiresExactMarkerTime()
    {
        TimelineFocusNavigation.ResolveAtPlayhead(TimeSpan.FromSeconds(10.001), Markers, Runs)
            .Should().BeNull();
    }

    [Fact]
    public void PreviousMarkerStepsFromExactMarker()
    {
        TimelineFocusNavigation.PreviousMarkerTime(TimeSpan.FromSeconds(20), Markers)
            .Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void PreviousMarkerFromBetweenMarkers()
    {
        TimelineFocusNavigation.PreviousMarkerTime(TimeSpan.FromSeconds(25), Markers)
            .Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PreviousMarkerAtFirstReturnsNull()
    {
        TimelineFocusNavigation.PreviousMarkerTime(TimeSpan.FromSeconds(10), Markers)
            .Should().BeNull();
    }

    [Fact]
    public void NextMarkerStepsFromExactMarker()
    {
        TimelineFocusNavigation.NextMarkerTime(TimeSpan.FromSeconds(20), Markers)
            .Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void NextMarkerAtLastReturnsNull()
    {
        TimelineFocusNavigation.NextMarkerTime(TimeSpan.FromSeconds(40), Markers)
            .Should().BeNull();
    }

    [Fact]
    public void PreviousCutCenterStepsFromExactCenter()
    {
        // D2 center = 27.5 → D1 center = 7.5
        TimelineFocusNavigation.PreviousCutCenter(TimeSpan.FromSeconds(27.5), Runs)
            .Should().Be(TimeSpan.FromSeconds(7.5));
    }

    [Fact]
    public void NextCutCenterFromBeforeCenterLandsOnThatCenter()
    {
        // Playhead inside D2 but before its center → next is D2@27.5
        TimelineFocusNavigation.NextCutCenter(TimeSpan.FromSeconds(26), Runs)
            .Should().Be(TimeSpan.FromSeconds(27.5));
    }

    [Fact]
    public void NextCutCenterFromAfterCenterGoesToFollowingCut()
    {
        TimelineFocusNavigation.NextCutCenter(TimeSpan.FromSeconds(28), Runs)
            .Should().Be(TimeSpan.FromSeconds(55));
    }

    [Fact]
    public void NextCutCenterAtLastReturnsNull()
    {
        TimelineFocusNavigation.NextCutCenter(TimeSpan.FromSeconds(55), Runs)
            .Should().BeNull();
    }

    [Fact]
    public void PreviousCutCenterAtFirstReturnsNull()
    {
        TimelineFocusNavigation.PreviousCutCenter(TimeSpan.FromSeconds(7.5), Runs)
            .Should().BeNull();
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
    public void ResolveAtPlayheadReturnsNullWhenEmpty()
    {
        TimelineFocusNavigation.ResolveAtPlayhead(TimeSpan.FromSeconds(1), [], [])
            .Should().BeNull();
    }
}
