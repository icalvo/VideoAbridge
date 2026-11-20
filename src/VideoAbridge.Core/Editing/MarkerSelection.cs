using System.Collections.Immutable;

namespace VideoAbridge.Core.Editing;

/// <summary>
/// Click / Shift+click rules for marker multi-select (at most two markers).
/// </summary>
public static class MarkerSelection
{
    /// <summary>
    /// Returns the selection after clicking <paramref name="clickedId"/>.
    /// When <paramref name="extendSelection"/> is true (Shift), selection can grow to two
    /// or, when already at two, replace the nearest selected marker.
    /// </summary>
    public static ImmutableList<Guid> ApplyClick(
        IReadOnlyList<Guid> selectedIds,
        Guid clickedId,
        bool extendSelection,
        IReadOnlyDictionary<Guid, TimeSpan> markerTimes)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        ArgumentNullException.ThrowIfNull(markerTimes);

        // Click / Shift+click on an already-selected marker: deselect only that marker.
        if (selectedIds.Contains(clickedId))
            return [.. selectedIds.Where(id => id != clickedId)];

        return selectedIds.Count switch
        {
            0 => [clickedId],
            1 => extendSelection
                ? [selectedIds[0], clickedId]
                : [clickedId],
            _ => extendSelection
                ? ReplaceNearest(selectedIds, clickedId, markerTimes)
                : [clickedId],
        };
    }

    private static ImmutableList<Guid> ReplaceNearest(
        IReadOnlyList<Guid> selectedIds,
        Guid clickedId,
        IReadOnlyDictionary<Guid, TimeSpan> markerTimes)
    {
        if (!markerTimes.TryGetValue(clickedId, out var clickedTime))
            return [clickedId];

        var selected = selectedIds
            .Where(id => markerTimes.ContainsKey(id))
            .Distinct()
            .Take(2)
            .ToList();

        if (selected.Count != 2)
            return [clickedId];

        var a = selected[0];
        var b = selected[1];
        var nearestId = AbsDiff(clickedTime, markerTimes[a]) <= AbsDiff(clickedTime, markerTimes[b])
            ? a
            : b;

        return
        [
            .. selectedIds
                .Where(id => id != nearestId)
                .Append(clickedId)
                .Distinct()
                .Take(2)
        ];
    }

    private static TimeSpan AbsDiff(TimeSpan a, TimeSpan b) => a > b ? a - b : b - a;
}
