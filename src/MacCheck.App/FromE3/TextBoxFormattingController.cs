using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using E3.Core;
using TableForge.Domain;

namespace E3.Editor;

/// <summary>What both prototypes' editors offer the window: the session they edit and the formatting/history commands.</summary>
public interface IFormattingEditor
{
    FormattedTextSession Session { get; }
    TextSelection Selection { get; }
    /// <summary>What the last command did, or why it did nothing (1.0.0's FormatMessage).</summary>
    string Message { get; }
    event Action? MessageChanged;
    void Execute(EditorCommand command);
    /// <summary>Puts new text and formatting into the editor (opening a row), with an empty undo history.</summary>
    void Load(string text, TextStyles styles);
}

/// <summary>
/// Approach A — the 1.0.0 design on Avalonia: a stock <see cref="TextBox"/> edits the plain text, the formatting lives beside
/// it in a <see cref="FormattedTextSession"/>, and a separate read-only view shows how the result looks when rolled (the
/// Review screen's "how this result looks when rolled" preview). The text box itself stays plain, exactly as in 1.0.0.
/// <para>
/// Avalonia's TextBox raises TextChanged asynchronously and without the change itself, where WPF gives
/// TextChangedEventArgs.Changes; the exact edit is recovered from the caret instead (<see cref="EditInference"/>). The text
/// box's own undo is turned off: Ctrl+Z / Ctrl+Y go to the session's history, which covers formatting too.
/// </para>
/// </summary>
public sealed class TextBoxFormattingController : IFormattingEditor
{
    private readonly TextBox _box;
    private TextSelection _stableSelection; // the last selection seen while the box and the session agreed on the text
    private bool _syncing;
    private string _message = "";

    public TextBoxFormattingController(TextBox box, FormattedTextSession session, bool disableLigatures = true)
    {
        _box = box;
        Session = session;
        _box.IsUndoEnabled = false;
        // Fonts with standard ligatures (DejaVu Sans on Linux: "fi", "fl") make the TextBox treat the pair as a single
        // caret stop, so the caret could not be put between f and i ("fire", "flame"). Result text never needs ligatures.
        if (disableLigatures) _box.FontFeatures = NoLigatures();
        _box.Text = session.Text;
        _box.TextChanged += OnTextChanged;
        _box.PropertyChanged += OnBoxPropertyChanged;
        _box.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public static FontFeatureCollection NoLigatures() => [new() { Tag = "liga", Value = 0 }, new() { Tag = "clig", Value = 0 }];

    public FormattedTextSession Session { get; }
    public string Message { get => _message; private set { _message = value; MessageChanged?.Invoke(); } }
    public event Action? MessageChanged;

    /// <summary>Number of edits whose exact position had to fall back to text comparison (expected 0 for normal typing).</summary>
    public int InferenceFallbacks { get; private set; }

    public TextSelection Selection
    {
        get
        {
            var a = _box.SelectionStart;
            var b = _box.SelectionEnd;
            return new TextSelection(Math.Min(a, b), Math.Abs(b - a));
        }
    }

    public void Load(string text, TextStyles styles)
    {
        Session.Load(text, styles);
        Show(TextSelection.Caret(0));
    }

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty) return;
        if ((_box.Text ?? "") == Session.Text) _stableSelection = Selection;
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        var text = _box.Text ?? "";
        if (text == Session.Text) return; // already applied (TextChanged is posted, so it can arrive after an undo set the text)
        if (!Session.TextReplaced(text, _box.CaretIndex, _stableSelection)) InferenceFallbacks++;
        _stableSelection = Selection;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (FormattingShortcuts.Match(_box, e) is not { } command) return;
        e.Handled = true;
        Execute(command);
    }

    public void Execute(EditorCommand command)
    {
        var selection = Selection;
        switch (command)
        {
            case EditorCommand.Undo:
            case EditorCommand.Redo:
                var restored = command == EditorCommand.Undo ? Session.Undo(selection) : Session.Redo(selection);
                if (restored is { } s) Show(s);
                Message = restored is null ? $"Nothing to {command.ToString().ToLowerInvariant()}." : $"{command} done.";
                return;
            default:
                Message = FormatMessages.Apply(Session, selection, command);
                return;
        }
    }

    /// <summary>Puts the session's text into the box and restores a selection, without reporting it back as an edit.</summary>
    private void Show(TextSelection selection)
    {
        _syncing = true;
        try
        {
            if ((_box.Text ?? "") != Session.Text) _box.Text = Session.Text;
            var start = Math.Clamp(selection.Start, 0, Session.Text.Length);
            var end = Math.Clamp(selection.End, start, Session.Text.Length);
            _box.SelectionStart = start;
            _box.SelectionEnd = end;
            _box.CaretIndex = end;
            _box.SelectionStart = start; // CaretIndex collapses the selection in some versions; set it again
            _box.SelectionEnd = end;
            _stableSelection = new TextSelection(start, end - start);
        }
        finally { _syncing = false; }
    }
}

/// <summary>The formatting commands and the messages 1.0.0's Review screen shows for them.</summary>
public static class FormatMessages
{
    public static string Apply(FormattedTextSession session, TextSelection selection, EditorCommand command)
    {
        if (selection.Length <= 0 || selection.Start < 0 || selection.End > session.Text.Length)
            return "Select some result text first, then choose Bold, Italic or Clear Formatting.";
        if (command == EditorCommand.Clear)
            return session.Clear(selection) ? "Removed bold and italic from the selection." : "The selection has no bold or italic.";

        var style = command == EditorCommand.Bold ? TextStyle.Bold : TextStyle.Italic;
        var name = command == EditorCommand.Bold ? "bold" : "italic";
        session.Toggle(selection, style);
        var styles = session.Styles.ToArray(session.Text.Length);
        var all = true;
        for (var i = selection.Start; i < selection.End; i++) all &= styles[i].HasFlag(style);
        return all ? $"Made the selection {name}." : $"Removed {name} from the selection.";
    }
}
