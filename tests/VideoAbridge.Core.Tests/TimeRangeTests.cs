using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class TimeRangeTests
{
    [Fact]
    public void ContainsIsInclusive()
    {
        var range = new TimeRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        range.Contains(TimeSpan.FromSeconds(1)).Should().BeTrue();
        range.Contains(TimeSpan.FromSeconds(2)).Should().BeTrue();
        range.Contains(TimeSpan.FromSeconds(3)).Should().BeTrue();
        range.Contains(TimeSpan.FromSeconds(0.999)).Should().BeFalse();
        range.Contains(TimeSpan.FromSeconds(3.001)).Should().BeFalse();
    }

    [Fact]
    public void OverlapsUsesOpenIntervalSemantics()
    {
        var a = new TimeRange(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2));
        var b = new TimeRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        var c = new TimeRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        a.Overlaps(b).Should().BeFalse();
        a.Overlaps(c).Should().BeTrue();
        b.Overlaps(c).Should().BeTrue();
    }

    [Fact]
    public void InteriorCoverAndTouchHelpers()
    {
        var duration = TimeSpan.FromSeconds(10);
        new TimeRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(9)).IsInteriorOf(duration).Should().BeTrue();
        new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(2)).IsInteriorOf(duration).Should().BeFalse();
        new TimeRange(TimeSpan.Zero, duration).Covers(duration).Should().BeTrue();
        new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(2)).TouchesStart.Should().BeTrue();
        new TimeRange(TimeSpan.FromSeconds(8), duration).TouchesEnd(duration).Should().BeTrue();
    }

    [Fact]
    public void NearestEdgePrefersCloserBound()
    {
        var range = new TimeRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        range.NearestEdge(TimeSpan.FromSeconds(8)).Should().Be(TimeSpan.FromSeconds(10));
        range.NearestEdge(TimeSpan.FromSeconds(19)).Should().Be(TimeSpan.FromSeconds(20));
        range.AbsDistanceTo(TimeSpan.FromSeconds(8)).Should().Be(TimeSpan.FromSeconds(2));
        range.AbsDistanceTo(TimeSpan.FromSeconds(15)).Should().Be(TimeSpan.Zero);
    }
}
