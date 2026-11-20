using FluentAssertions;

namespace VideoAbridge.Core.Tests;

public class MarkerSelectionTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid C = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid D = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly IReadOnlyDictionary<Guid, TimeSpan> Times = new Dictionary<Guid, TimeSpan>
    {
        [A] = TimeSpan.FromSeconds(10),
        [B] = TimeSpan.FromSeconds(20),
        [C] = TimeSpan.FromSeconds(30),
        [D] = TimeSpan.FromSeconds(40),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroSelectedClickOrShiftSelectsClicked(bool extend)
    {
        var next = MarkerSelection.ApplyClick([], A, extend, Times);
        next.Should().Equal(A);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneSelectedClickOrShiftOnSelectedDeselects(bool extend)
    {
        var next = MarkerSelection.ApplyClick([A], A, extend, Times);
        next.Should().BeEmpty();
    }

    [Fact]
    public void OneSelectedClickOnOtherReplacesSelection()
    {
        var next = MarkerSelection.ApplyClick([A], B, extendSelection: false, Times);
        next.Should().Equal(B);
    }

    [Fact]
    public void OneSelectedShiftClickOnOtherAddsToSelection()
    {
        var next = MarkerSelection.ApplyClick([A], B, extendSelection: true, Times);
        next.Should().Equal(A, B);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoSelectedClickOrShiftOnSelectedDeselectsThatOne(bool extend)
    {
        var next = MarkerSelection.ApplyClick([A, C], A, extend, Times);
        next.Should().Equal(C);
    }

    [Fact]
    public void TwoSelectedClickOnOtherReplacesWithClickedOnly()
    {
        var next = MarkerSelection.ApplyClick([A, C], B, extendSelection: false, Times);
        next.Should().Equal(B);
    }

    [Fact]
    public void TwoSelectedShiftClickOutsideReplacesNearest()
    {
        // A@10, C@30 selected; D@40 is nearer to C than A → drop C, keep A, add D
        var next = MarkerSelection.ApplyClick([A, C], D, extendSelection: true, Times);
        next.Should().BeEquivalentTo([A, D]);
    }

    [Fact]
    public void TwoSelectedShiftClickBetweenReplacesNearest()
    {
        // A@10, C@30 selected; B@20 equidistant → ties pick A
        var next = MarkerSelection.ApplyClick([A, C], B, extendSelection: true, Times);
        next.Should().BeEquivalentTo([C, B]);
    }

    [Fact]
    public void TwoSelectedShiftClickCloserToLeftDropsLeft()
    {
        // A@10, D@40 selected; B@20 nearer to A
        var next = MarkerSelection.ApplyClick([A, D], B, extendSelection: true, Times);
        next.Should().BeEquivalentTo([D, B]);
    }
}
