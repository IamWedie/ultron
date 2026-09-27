namespace Ultron.Services;

/// <summary>
/// A stack of reversible actions the assistant performed, so "undo that" is a
/// first-class capability rather than a per-handler special case.
/// </summary>
/// <remarks>
/// Owned by the service layer, not the window: file operations, app launches and
/// settings changes all register their reversal here through
/// <see cref="Push"/>, and the <c>undo</c> tool drains it. Nothing that pushes an
/// undo needs a reference to the UI.
/// </remarks>
public sealed class UndoLedger
{
    private readonly object _gate = new();
    private readonly List<(string Description, Func<string> Undo)> _entries = new();
    private readonly int _maxDepth;

    public UndoLedger(int maxDepth = 20)
    {
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        _maxDepth = maxDepth;
    }

    /// <summary>Registers a reversal. Trimming drops the OLDEST entry.</summary>
    public void Push(string description, Func<string> undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        lock (_gate)
        {
            _entries.Add((description ?? "", undo));
            while (_entries.Count > _maxDepth) _entries.RemoveAt(0);
        }
    }

    /// <summary>Reverses the most recent action. Never throws.</summary>
    public string Undo() => UndoCore().Message;

    /// <summary>
    /// The same reversal as <see cref="Undo"/>, with the outcome classified.
    /// <see cref="Undo"/> can only answer with a sentence, so the <c>undo</c> tool
    /// had to guess from the wording whether the ledger was empty or a reversal
    /// had thrown; both read to the model as a success.
    /// </summary>
    public ToolResult UndoResult()
    {
        var outcome = UndoCore();
        return outcome.Error switch
        {
            ToolError.NotFound => ToolResult.NotFound(outcome.Message),
            ToolError.Failed => ToolResult.Fail(outcome.Message),
            _ => ToolResult.Ok(outcome.Message),
        };
    }

    private (string Message, ToolError Error) UndoCore()
    {
        (string Description, Func<string> Undo) item;
        lock (_gate)
        {
            if (_entries.Count == 0) return ("Nothing to undo.", ToolError.NotFound);
            item = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
        }

        try
        {
            return ($"Undid {item.Description}. {item.Undo()}", ToolError.None);
        }
        catch (Exception ex)
        {
            return ($"Undo of {item.Description} failed: {ex.Message}", ToolError.Failed);
        }
    }

    /// <summary>Newest-first description of the pending actions.</summary>
    public string History()
    {
        lock (_gate)
        {
            var ordered = new List<string>(_entries.Count);
            for (var i = _entries.Count - 1; i >= 0; i--) ordered.Add(_entries[i].Description);
            return string.Join(", ", ordered);
        }
    }

    public int Count
    {
        get { lock (_gate) { return _entries.Count; } }
    }
}
