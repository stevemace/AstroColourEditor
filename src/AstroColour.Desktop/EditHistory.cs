using AstroColour.Core;
using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Desktop;

/// <summary>One colour editor entry as it was at some point in the history.</summary>
internal sealed record AdjustmentState(ColourAdjustment Model, bool EditWithCurves);

/// <summary>The stars image added back over the image, at full and preview size.</summary>
internal sealed record StarsState(RgbImage Full, RgbImage? Proxy, string Path);

/// <summary>
/// Every edit to the open image at one moment, for undo and redo. Its parts are copies that are never changed
/// afterwards, apart from the stars images, which are shared since they are never changed at all.
/// </summary>
internal sealed record EditState(
    AdjustmentState[] Adjustments,
    int SelectedIndex,
    CurveSet Curves,
    HueCurveSet HueCurves,
    float Saturation,
    float RemovePurple,
    NarrowbandSettings Narrowband,
    StarsState? Stars,
    float StarBrightness,
    Rotation Rotation,
    CropRect? Crop)
{
    /// <summary>
    /// Identifies what this state makes of the image: edits that change nothing, such as picking a colour
    /// but not yet adjusting it, keep the previous state's value. Used to tell whether there are unsaved changes.
    /// </summary>
    public long ContentId { get; init; }
}

/// <summary>
/// Undo and redo stacks of <see cref="EditState"/>s. Each edit records the state after it; a run of edits with the
/// same key (one slider drag, one curve point being dragged) in quick succession becomes a single step.
/// </summary>
internal sealed class EditHistory
{
    private const int MaxSteps = 200;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1);

    private readonly List<EditState> _undo = [];
    private readonly List<EditState> _redo = [];
    private EditState? _current;
    private object? _lastKey;
    private DateTime _lastTime;
    private long _nextId;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>The state after the latest edit, or the one undone or redone to.</summary>
    public EditState? Current => _current;

    /// <summary>Forgets every step and starts again from <paramref name="initial"/>, e.g. when a file is opened.</summary>
    public EditState Reset(EditState initial)
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
        return _current = initial with { ContentId = ++_nextId };
    }

    /// <summary>
    /// Records <paramref name="state"/> as the result of an edit. If <paramref name="mergeKey"/> matches the previous
    /// edit's and that was moments ago, the two are one step; a null key never merges.
    /// </summary>
    /// <param name="changesImage">False for an edit that leaves the image as it was.</param>
    public void Record(EditState state, object? mergeKey = null, bool changesImage = true)
    {
        var now = DateTime.UtcNow;
        state = state with { ContentId = changesImage || _current is null ? ++_nextId : _current.ContentId };
        if (_current is null)
        {
            _current = state;
            return;
        }

        bool merge = mergeKey is not null && Equals(mergeKey, _lastKey) && now - _lastTime < MergeWindow;
        if (!merge)
        {
            // Undoing returns to the entry being edited now, even if another was selected after the previous edit.
            _undo.Add(_current with { SelectedIndex = state.SelectedIndex });
            if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _current = state;
        _lastKey = mergeKey;
        _lastTime = now;
    }

    /// <summary>Ends the current run of edits, so the next one starts a new step even if it has the same key.</summary>
    public void Seal() => _lastKey = null;

    /// <summary>Steps back, returning the state to show, or null if there is nothing to undo.</summary>
    public EditState? Undo()
    {
        if (_undo.Count == 0 || _current is null) return null;
        _redo.Add(_current);
        _current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _lastKey = null;
        return _current;
    }

    /// <summary>Steps forward again, returning the state to show, or null if there is nothing to redo.</summary>
    public EditState? Redo()
    {
        if (_redo.Count == 0 || _current is null) return null;
        _undo.Add(_current);
        _current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _lastKey = null;
        return _current;
    }
}
