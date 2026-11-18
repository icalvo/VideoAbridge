
namespace VideoAbridge.ViewModels;

/// <summary>Linear undo/redo stack of immutable <see cref="Timeline"/> snapshots.</summary>
internal sealed class EditHistory
{
    private const int MaxDepth = 100;
    private readonly List<Timeline> _undo = [];
    private readonly List<Timeline> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Records <paramref name="before"/> so Undo can restore it after the current edit.</summary>
    public void RecordBeforeChange(Timeline before)
    {
        ArgumentNullException.ThrowIfNull(before);
        _undo.Add(before);
        if (_undo.Count > MaxDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool TryUndo(Timeline current, out Timeline previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_undo.Count == 0)
        {
            previous = null!;
            return false;
        }

        previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        if (_redo.Count > MaxDepth)
            _redo.RemoveAt(0);
        return true;
    }

    public bool TryRedo(Timeline current, out Timeline next)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_redo.Count == 0)
        {
            next = null!;
            return false;
        }

        next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        if (_undo.Count > MaxDepth)
            _undo.RemoveAt(0);
        return true;
    }
}
