namespace SharpTurns.App.ViewModels;

/// <summary>
/// Inserts a transcript at the composer's caret, spaced from the text around it, and keeps the
/// composer's text and caret from before each insertion so Undo can restore them.
/// </summary>
internal sealed class DictationComposerHistory
{
    private readonly Func<string> _getComposerText;
    private readonly Action<string> _setComposerText;
    private readonly Func<int> _getComposerCaretIndex;
    private readonly Action<int> _setComposerCaretIndex;
    private readonly List<ComposerSnapshot> _undoStack = new();

    public DictationComposerHistory(
        Func<string> getComposerText,
        Action<string> setComposerText,
        Func<int> getComposerCaretIndex,
        Action<int> setComposerCaretIndex)
    {
        _getComposerText = getComposerText ?? throw new ArgumentNullException(nameof(getComposerText));
        _setComposerText = setComposerText ?? throw new ArgumentNullException(nameof(setComposerText));
        _getComposerCaretIndex = getComposerCaretIndex ?? throw new ArgumentNullException(nameof(getComposerCaretIndex));
        _setComposerCaretIndex = setComposerCaretIndex ?? throw new ArgumentNullException(nameof(setComposerCaretIndex));
    }

    public int UndoCount => _undoStack.Count;

    public bool ApplyTranscript(string? transcript)
    {
        var normalizedTranscript = transcript?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return false;
        }

        var existingText = _getComposerText();
        var insertionIndex = Math.Clamp(_getComposerCaretIndex(), 0, existingText.Length);
        _undoStack.Add(new ComposerSnapshot(existingText, insertionIndex));

        var prefix = existingText[..insertionIndex];
        var suffix = existingText[insertionIndex..];
        var needsLeadingSpace = prefix.Length > 0
            && !char.IsWhiteSpace(prefix[^1])
            && !char.IsWhiteSpace(normalizedTranscript[0]);
        var needsTrailingSpace = suffix.Length > 0
            && !char.IsWhiteSpace(normalizedTranscript[^1])
            && !char.IsWhiteSpace(suffix[0])
            && !IsClosingPunctuation(suffix[0]);
        var insertedText = (needsLeadingSpace ? " " : string.Empty)
            + normalizedTranscript
            + (needsTrailingSpace ? " " : string.Empty);

        _setComposerText(prefix + insertedText + suffix);
        _setComposerCaretIndex(insertionIndex + insertedText.Length);
        return true;
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0)
        {
            return false;
        }

        var previousSnapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _setComposerText(previousSnapshot.Text);
        _setComposerCaretIndex(previousSnapshot.CaretIndex);
        return true;
    }

    public void Clear()
    {
        _undoStack.Clear();
    }

    private static bool IsClosingPunctuation(char character) => ",.;:!?%)]}'’".Contains(character);

    private readonly record struct ComposerSnapshot(string Text, int CaretIndex);
}
