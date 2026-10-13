using TableForge.Domain;

namespace E3.Core;

/// <summary>A selection in the edited text: <see cref="Start"/> is where it begins, <see cref="Length"/> may be 0 (a caret).</summary>
public readonly record struct TextSelection(int Start, int Length)
{
    public int End => Start + Length;
    public static TextSelection Caret(int index) => new(index, 0);
}

/// <summary>
/// One result's text and its bold/italic while it is being edited — the model both Avalonia prototypes share, so neither
/// editor control owns the formatting. Text and formatting stay exactly as TableForge 1.0.0 keeps them: plain
/// <see cref="Text"/> plus <see cref="TextStyles"/> beside it (the unchanged 1.0.0 class).
/// <para>
/// Unlike 1.0.0 (whose WPF TextBox undo only knows the text, and whose formatting commands have no undo of their own), every
/// change — typing, deleting, pasting, Bold, Italic, Clear — is one step on a single undo history that restores text,
/// formatting and selection together. Consecutive typing at one place is merged into one step, as text editors usually do.
/// </para>
/// </summary>
public sealed class FormattedTextSession
{
    private readonly record struct Snapshot(string Text, TextStyles Styles, TextSelection Selection);

    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private (int Caret, char Last)? _coalesce; // where the last typed character ended (and what it was), so the next can join its step

    public FormattedTextSession(string text = "", TextStyles? styles = null)
    {
        Text = text;
        Styles = styles ?? TextStyles.Empty;
    }

    public string Text { get; private set; }
    public TextStyles Styles { get; private set; }

    /// <summary>Raised after any change to the text or formatting (including undo and redo).</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<FormattedSegment> Segments => Styles.Segments(Text);

    // ---- loading and saving in TableForge's own representation ------------------------------------------------------

    /// <summary>Starts from a stored row: Entries.Text and Entries.TextFormatting exactly as TableForge 1.0.0 stores them.</summary>
    public static FormattedTextSession FromStored(string text, string? textFormatting) => new(text, TextStyles.Parse(textFormatting, text));

    /// <summary>The Entries.TextFormatting value TableForge 1.0.0 would store for the current text (null when plain).</summary>
    public string? SerializeFormatting() => Styles.Serialize(Text);

    /// <summary>Replaces everything, as opening another row does. Clears the undo history.</summary>
    public void Load(string text, TextStyles styles)
    {
        Text = text;
        Styles = styles;
        _undo.Clear();
        _redo.Clear();
        _coalesce = null;
        Changed?.Invoke();
    }

    // ---- editing ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// One exact edit reported by an editor: <paramref name="removed"/> characters at <paramref name="offset"/> replaced by
    /// <paramref name="inserted"/>. Formatting follows the 1.0.0 rule (<see cref="TextStyles.ApplyEdit"/>): characters before
    /// and after keep theirs, new characters get only what both neighbours share.
    /// </summary>
    /// <param name="selectionBefore">The selection just before the edit, restored by Undo.</param>
    public void Edit(int offset, int removed, string inserted, TextSelection selectionBefore)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        removed = Math.Clamp(removed, 0, Text.Length - offset);
        if (removed == 0 && inserted.Length == 0) return;

        // Typing one character straight after the last one joins its undo step, except that a space after a word starts a
        // new step (so "hello world" undoes to "hello"); anything else is a step of its own.
        var typing = removed == 0 && inserted.Length == 1 && inserted[0] is not ('\n' or '\r');
        var joins = typing && _coalesce is { } c && c.Caret == offset && !(char.IsWhiteSpace(inserted[0]) && !char.IsWhiteSpace(c.Last));
        if (!joins) PushUndo(selectionBefore);
        _coalesce = typing ? (offset + 1, inserted[0]) : null;

        Styles = Styles.ApplyEdit(Text.Length, offset, removed, inserted.Length);
        Text = string.Concat(Text.AsSpan(0, offset), inserted, Text.AsSpan(offset + removed));
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The editor's text changed and the exact edit is not known: inferred from the caret where possible
    /// (<see cref="EditInference"/>), otherwise moved with TableForge's own fallback, <see cref="TextStyles.Remap"/>.
    /// </summary>
    /// <returns>True when the exact edit could be inferred.</returns>
    public bool TextReplaced(string newText, int caretAfter, TextSelection selectionBefore)
    {
        if (newText == Text) return true;
        if (EditInference.Infer(Text, newText, caretAfter) is { } edit)
        {
            Edit(edit.Offset, edit.Removed, newText.Substring(edit.Offset, edit.Added), selectionBefore);
            return true;
        }
        PushUndo(selectionBefore);
        _coalesce = null;
        Styles = Styles.Remap(Text, newText);
        Text = newText;
        _redo.Clear();
        Changed?.Invoke();
        return false;
    }

    // ---- formatting -------------------------------------------------------------------------------------------------

    /// <summary>Bold or italic on the selection (1.0.0's <see cref="TextStyles.Toggle"/>). False when nothing changed.</summary>
    public bool Toggle(TextSelection selection, TextStyle style) => SetStyles(Styles.Toggle(Text, selection.Start, selection.Length, style), selection);

    /// <summary>Clear Formatting on the selection (1.0.0's <see cref="TextStyles.Clear"/>). False when nothing changed.</summary>
    public bool Clear(TextSelection selection) => SetStyles(Styles.Clear(Text, selection.Start, selection.Length), selection);

    private bool SetStyles(TextStyles styles, TextSelection selection)
    {
        if (styles.Equals(Styles)) return false;
        PushUndo(selection);
        _coalesce = null;
        Styles = styles;
        _redo.Clear();
        Changed?.Invoke();
        return true;
    }

    // ---- undo -------------------------------------------------------------------------------------------------------

    /// <summary>Undoes one step; the selection to show afterwards, or null when there was nothing to undo.</summary>
    public TextSelection? Undo(TextSelection current) => Step(_undo, _redo, current);

    /// <summary>Redoes one step; the selection to show afterwards, or null when there was nothing to redo.</summary>
    public TextSelection? Redo(TextSelection current) => Step(_redo, _undo, current);

    private TextSelection? Step(List<Snapshot> from, List<Snapshot> to, TextSelection current)
    {
        if (from.Count == 0) return null;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(new Snapshot(Text, Styles, current));
        _coalesce = null;
        Text = snapshot.Text;
        Styles = snapshot.Styles;
        Changed?.Invoke();
        return snapshot.Selection;
    }

    private void PushUndo(TextSelection selection)
    {
        _undo.Add(new Snapshot(Text, Styles, selection));
        if (_undo.Count > 200) _undo.RemoveAt(0);
    }
}

/// <summary>
/// Works out the single edit that turned one text into another when the editor does not say (Avalonia's TextBox raises
/// TextChanged without the change). The caret after an edit sits at the end of what was inserted, which settles the case a
/// plain text comparison cannot: typing "a" beside "aa" — the 1.0.0 WPF editor uses TextChangedEventArgs.Changes for this.
/// </summary>
public static class EditInference
{
    public readonly record struct TextEdit(int Offset, int Removed, int Added);

    /// <summary>The edit, or null when no single edit ending at <paramref name="caretAfter"/> explains the change.</summary>
    public static TextEdit? Infer(string before, string after, int caretAfter)
    {
        if (caretAfter < 0 || caretAfter > after.Length) return null;
        var suffix = after.Length - caretAfter; // everything after the caret was there before, unchanged
        if (suffix > before.Length || !before.AsSpan(before.Length - suffix).SequenceEqual(after.AsSpan(caretAfter))) return null;

        var limit = Math.Min(caretAfter, before.Length - suffix);
        var prefix = 0;
        while (prefix < limit && before[prefix] == after[prefix]) prefix++;
        return new TextEdit(prefix, before.Length - suffix - prefix, caretAfter - prefix);
    }
}
